using System.Buffers;
using System.Text.Json;

namespace Centrifugal.Cent;

/// <summary>
/// Automatic batching (spec 8): below <c>maxInFlight</c> requests every call is sent at
/// once, alone; at the cap calls queue, first in first out, and when a request finishes
/// those queued go out as one batch. There are no timers: nothing waits for a batch to fill.
/// </summary>
internal sealed class AutoBatcher(ClientCore core, int maxInFlight, int maxBatchSize)
{
    private readonly object _lock = new();
    private readonly LinkedList<Queued> _queue = [];
    private int _inFlight;

    /// <summary>A call waiting for a batch: its command, encoded before it queued.</summary>
    private sealed class Queued(string method, bool proOnly, byte[] body, Deadline deadline, ExecutionContext? context)
    {
        public string Method { get; } = method;
        public bool ProOnly { get; } = proOnly;
        public byte[] Body { get; } = body;
        public Deadline Deadline { get; } = deadline;
        public ExecutionContext? Context { get; } = context;
        public TaskCompletionSource<JsonElement> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Queued>? Node { get; set; }
    }

    public async Task<JsonElement> CallAsync(string method, bool proOnly, byte[] body, Deadline deadline, CancellationToken cancellationToken)
    {
        Queued? queued = null;
        lock (_lock)
        {
            if (_inFlight < maxInFlight)
            {
                _inFlight++;
            }
            else
            {
                queued = new Queued(method, proOnly, body, deadline, ExecutionContext.Capture());
                queued.Node = _queue.AddLast(queued);
            }
        }
        if (queued is null)
        {
            try
            {
                return await core.SendAloneAsync(method, proOnly, body, deadline, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Release();
            }
        }

        using var timeoutCts = deadline.CreateTimeoutSource(core.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await queued.Reply.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (linked.IsCancellationRequested)
        {
            lock (_lock)
            {
                if (queued.Node?.List is not null)
                {
                    // Still queued: it leaves without its command being sent.
                    _queue.Remove(queued.Node);
                    queued.Node = null;
                }
            }
            // A reply arriving as the deadline ends is returned, not a timeout.
            if (queued.Reply.Task.IsCompletedSuccessfully)
            {
                return queued.Reply.Task.Result;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new CentTimeoutException(method, deadline.Timeout, e);
        }
    }

    /// <summary>A request finished: its slot goes to the calls queued, as one batch, or is freed.</summary>
    private void Release()
    {
        List<Queued>? batch = null;
        lock (_lock)
        {
            if (_queue.Count == 0)
            {
                _inFlight--;
                return;
            }
            batch = new List<Queued>(Math.Min(_queue.Count, maxBatchSize));
            while (batch.Count < maxBatchSize && _queue.First is { } node)
            {
                _queue.RemoveFirst();
                node.Value.Node = null;
                batch.Add(node.Value);
            }
        }
        // Sent from a task of the client's, in an empty context: never on a caller's.
        var scope = core.EnterBackground();
        Detached.Run(async () =>
        {
            try
            {
                await SendBatchAsync(batch).ConfigureAwait(false);
            }
            finally
            {
                scope.Dispose();
                Release();
            }
        });
    }

    private async Task SendBatchAsync(List<Queued> calls)
    {
        // Until the latest deadline of its calls, and never less than a full timeout from
        // now: a call which waited in the queue still gets one.
        var deadline = Deadline.After(core.Time, core.RequestTimeout);
        foreach (var c in calls)
        {
            deadline = Deadline.Max(deadline, c.Deadline);
        }
        var body = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteStartArray("commands");
            foreach (var c in calls)
            {
                w.WriteStartObject();
                w.WritePropertyName(c.Method);
                w.WriteRawValue(c.Body, skipInputValidation: true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteBoolean("parallel", true);
            w.WriteEndObject();
        }
        IDisposable? context = null;
        try
        {
            if (core.BatchContext is { } makeContext)
            {
                context = makeContext(calls.Select(c => c.Context).OfType<ExecutionContext>().ToList());
            }
            var replies = await core.PostAsync("batch", false, body.WrittenMemory, calls.Count, true, deadline, root =>
            {
                var list = BatchReplies.Read(root, calls.Count);
                var failed = list.Count(r => r.TryGetProperty("error", out var e) && e.ValueKind != JsonValueKind.Null);
                return new Outcome<IReadOnlyList<JsonElement>>(list, null, failed);
            }, CancellationToken.None).ConfigureAwait(false);
            for (var i = 0; i < calls.Count; i++)
            {
                var c = calls[i];
                try
                {
                    c.Reply.TrySetResult(BatchReplies.Result(c.Method, c.ProOnly, replies[i]));
                }
                catch (CentException e)
                {
                    c.Reply.TrySetException(e);
                }
            }
        }
        catch (Exception e)
        {
            // Failed as a whole: every call in it fails with its error.
            foreach (var c in calls)
            {
                c.Reply.TrySetException(e);
            }
        }
        finally
        {
            context?.Dispose();
        }
    }
}

/// <summary>The replies of a batch: one per command, in order.</summary>
internal static class BatchReplies
{
    public static IReadOnlyList<JsonElement> Read(JsonElement root, int commands)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new CentDecodeException("batch", $"the reply is a JSON {ClientCore.Kind(root)}, not an object");
        }
        if (!root.TryGetProperty("replies", out var replies) || replies.ValueKind != JsonValueKind.Array)
        {
            if (commands == 0 && (replies.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null))
            {
                return [];
            }
            throw new CentDecodeException("batch", "the reply has no replies array");
        }
        var count = replies.GetArrayLength();
        if (count != commands)
        {
            throw new CentDecodeException("batch", $"{count} replies to {commands} commands");
        }
        var list = new List<JsonElement>(count);
        foreach (var r in replies.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object)
            {
                throw new CentDecodeException("batch", $"a reply is a JSON {ClientCore.Kind(r)}, not an object");
            }
            list.Add(r);
        }
        return list;
    }

    /// <summary>A command's result in its reply - <c>{"publish": {...}}</c> - or its error.</summary>
    public static JsonElement Result(string method, bool proOnly, JsonElement reply)
    {
        if (reply.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            throw ClientCore.ApiError(method, proOnly, error, inBatch: true);
        }
        return ClientCore.ResultElement(method, reply, method);
    }
}

/// <summary>Work of the SDK's own, run on the thread pool in an empty execution context.</summary>
internal static class Detached
{
    public static void Run(Func<Task> work)
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            _ = Task.Run(work);
            return;
        }
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(work);
        }
    }
}
