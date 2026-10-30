using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Centrifugal.Cent;

/// <summary>A command's outcome in a batch, whatever its type.</summary>
public interface IPending
{
    /// <summary>Whether its batch was sent: its outcome is known.</summary>
    bool IsSent { get; }

    /// <summary>The command's error; null when it succeeded.</summary>
    /// <exception cref="CentBatchNotSentException">The batch was not sent.</exception>
    Exception? Error { get; }
}

/// <summary>A command's outcome, known once its batch is sent.</summary>
/// <typeparam name="T">The command's result.</typeparam>
public sealed class Pending<T> : IPending
{
    private T? _result;
    private Exception? _error;

    internal Pending()
    {
    }

    internal Pending(T result)
    {
        SetResult(result);
    }

    internal Pending(Exception error)
    {
        SetError(error);
    }

    /// <inheritdoc/>
    public bool IsSent { get; private set; }

    /// <summary>The command's result.</summary>
    /// <exception cref="CentBatchNotSentException">The batch was not sent.</exception>
    /// <exception cref="CentException">The command's error.</exception>
    public T Result
    {
        get
        {
            if (!IsSent)
            {
                throw new CentBatchNotSentException();
            }
            if (_error is not null)
            {
                ExceptionDispatchInfo.Throw(_error);
            }
            return _result!;
        }
    }

    /// <inheritdoc/>
    public Exception? Error => IsSent ? _error : throw new CentBatchNotSentException();

    internal void SetResult(T result)
    {
        _result = result;
        IsSent = true;
    }

    internal void SetError(Exception error)
    {
        _error = error;
        IsSent = true;
    }
}

/// <summary>Outcomes of batch commands, made by test doubles of applications.</summary>
public static class Pending
{
    /// <summary>A command's outcome as if its batch was sent and it succeeded.</summary>
    /// <typeparam name="T">The command's result.</typeparam>
    /// <param name="result">Its result.</param>
    /// <returns>The outcome.</returns>
    public static Pending<T> FromResult<T>(T result) => new(result);

    /// <summary>A command's outcome as if its batch was sent and it failed.</summary>
    /// <typeparam name="T">The command's result.</typeparam>
    /// <param name="error">Its error.</param>
    /// <returns>The outcome.</returns>
    public static Pending<T> FromException<T>(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Pending<T>(error);
    }
}

/// <summary>A failed command of a batch.</summary>
/// <param name="Index">Its position in the batch, from 0.</param>
/// <param name="Method">Its API method.</param>
/// <param name="Error">Its error.</param>
public sealed record FailedCommand(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("error"), JsonConverter(typeof(ErrorJsonConverter))] Exception Error);

/// <summary>The outcome of a sent batch; each command's is in its <see cref="Pending{T}"/>.</summary>
public sealed class BatchResult
{
    /// <summary>Creates a result - for test doubles of applications.</summary>
    /// <param name="total">The commands of the batch.</param>
    /// <param name="failed">The failed ones.</param>
    public BatchResult(int total, IReadOnlyList<FailedCommand> failed)
    {
        Total = total;
        Failed = failed;
    }

    /// <summary>The commands of the batch.</summary>
    [JsonPropertyName("total")]
    public int Total { get; }

    /// <summary>The failed commands, in order.</summary>
    [JsonPropertyName("failed")]
    public IReadOnlyList<FailedCommand> Failed { get; }

    /// <summary>Whether Centrifugo replied: always true for a result the SDK gives, as for <see cref="BroadcastResult.Replied"/>.</summary>
    [JsonPropertyName("replied")]
    public bool Replied { get; } = true;
}

/// <summary>
/// Centrifugo carried a batch out, and one or more commands failed: each command's
/// outcome is in its <see cref="Pending{T}"/>, the failed ones in <see cref="Failed"/>.
/// </summary>
public sealed class BatchFailedCommandsException : CentException
{
    /// <summary>Creates the error - for test doubles of applications.</summary>
    /// <param name="result">The batch's outcome, with at least one failed command.</param>
    public BatchFailedCommandsException(BatchResult result)
        : base(Describe(result))
    {
        Result = result;
    }

    /// <summary>The batch's outcome.</summary>
    public BatchResult Result { get; }

    /// <summary>The failed commands, in order.</summary>
    public IReadOnlyList<FailedCommand> Failed => Result.Failed;

    /// <summary>The commands of the batch.</summary>
    public int Total => Result.Total;

    private static string Describe(BatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = $"batch: {result.Failed.Count} of {result.Total} commands failed, the others succeeded";
        if (result.Failed.Count > 0 && result.Failed[0] is { } first)
        {
            text += $": command {first.Index} ({first.Method}): {first.Error.Message}";
        }
        return text;
    }
}

/// <summary>
/// Commands sent together in one request (spec 7.2): a method per API method adds a
/// command and returns its <see cref="Pending{T}"/>. A batch is sent once, and is not safe
/// for concurrent use.
/// </summary>
/// <example>
/// <code>
/// var batch = client.NewBatch();
/// var news = batch.Publish(new PublishRequest { Channel = "news", Data = data });
/// var presence = batch.Presence(new PresenceRequest { Channel = "chat" });
/// await batch.SendAsync(ct);
/// Console.WriteLine(news.Result.Offset);
/// </code>
/// </example>
/// <remarks>
/// Its methods are virtual, for mocking libraries, as <see cref="CentClient"/>'s: a mock's
/// commands return <see cref="Pending.FromResult"/> or <see cref="Pending.FromException"/>.
/// </remarks>
public partial class CentBatch
{
    private readonly ClientCore _core;
    private readonly TimeSpan _timeout;
    private readonly List<Entry> _commands = [];
    private bool _sent;

    private sealed record Entry(string Method, bool ProOnly, Func<byte[]> Encode, Action<JsonElement> Complete, Action<Exception> Fail, IPending Pending);

    /// <summary>For mocking only: a batch derived from it sends nothing unless its methods are overridden.</summary>
    protected CentBatch()
    {
        _core = null!;
    }

    internal CentBatch(ClientCore core, TimeSpan timeout, bool parallel)
    {
        _core = core;
        _timeout = timeout;
        Parallel = parallel;
    }

    /// <summary>Whether Centrifugo runs the commands concurrently, with no order between them.</summary>
    public virtual bool Parallel { get; }

    /// <summary>The commands added.</summary>
    public virtual int Count => _commands.Count;

    /// <summary>Adds a command: publishes the same data into many channels, as <see cref="CentClient.BroadcastAsync(BroadcastRequest, CancellationToken)"/>.</summary>
    /// <param name="request">The command's request.</param>
    /// <returns>The command's outcome, once the batch is sent: <see cref="BroadcastFailedChannelsException"/> when some channels failed.</returns>
    public virtual Pending<BroadcastResult> Broadcast(BroadcastRequest request)
    {
        var channels = request?.Channels?.ToList() ?? [];
        var method = ApiMethods.Broadcast;
        var pending = new Pending<BroadcastResult>();
        Append(new Entry(method.Name, method.ProOnly, () => method.Encode(request!), element =>
        {
            var wire = method.Decode(element);
            var result = Broadcasts.Build(channels, wire, request!.IdempotencyKey ?? "");
            if (result.Failed.Count > 0)
            {
                pending.SetError(new BroadcastFailedChannelsException(result));
            }
            else
            {
                pending.SetResult(result);
            }
        }, pending.SetError, pending));
        return pending;
    }

    internal Pending<TResult> Add<TRequest, TResult>(ApiMethod<TRequest, TResult> method, TRequest request)
    {
        var pending = new Pending<TResult>();
        Append(new Entry(method.Name, method.ProOnly, () => method.Encode(request), e => pending.SetResult(method.Decode(e)), pending.SetError, pending));
        return pending;
    }

    private void Append(Entry entry)
    {
        if (_sent)
        {
            throw new CentBatchSentException("a command added to a sent batch: a batch is sent once - make a new one");
        }
        _commands.Add(entry);
    }

    /// <summary>
    /// Sends the batch: returns when every command succeeded, each <see cref="Pending{T}"/>
    /// holding its result.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call: it then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The batch's outcome.</returns>
    /// <exception cref="BatchFailedCommandsException">Some commands failed: each pending holds its own outcome.</exception>
    /// <exception cref="CentBatchSentException">The batch was sent already.</exception>
    /// <exception cref="CentException">The batch failed as a whole: every pending holds this error.</exception>
    public virtual async Task<BatchResult> SendAsync(CancellationToken cancellationToken = default)
    {
        if (_sent)
        {
            throw new CentBatchSentException("the batch was sent already: a batch is sent once");
        }
        _sent = true;
        using var scope = _core.Enter();
        if (_commands.Count == 0)
        {
            return new BatchResult(0, []);
        }
        var start = _core.Time.GetTimestamp();
        var body = new ArrayBufferWriter<byte>();
        try
        {
            using var w = new Utf8JsonWriter(body);
            w.WriteStartObject();
            w.WriteStartArray("commands");
            for (var i = 0; i < _commands.Count; i++)
            {
                var c = _commands[i];
                byte[] encoded;
                try
                {
                    encoded = c.Encode();
                }
                catch (CentInvalidRequestException e)
                {
                    throw new CentInvalidRequestException("batch", $"command {i} ({c.Method}): {e.Message}", e);
                }
                w.WriteStartObject();
                w.WritePropertyName(c.Method);
                w.WriteRawValue(encoded, skipInputValidation: true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (Parallel)
            {
                w.WriteBoolean("parallel", true);
            }
            w.WriteEndObject();
        }
        catch (CentInvalidRequestException e)
        {
            FailAll(e);
            _core.Report(new RequestInfo("batch", _commands.Count, 0, false, _core.Time.GetElapsedTime(start), 0, e));
            throw;
        }

        IReadOnlyList<JsonElement> replies;
        try
        {
            replies = await _core.PostAsync("batch", false, body.WrittenMemory, _commands.Count, false, Deadline.After(_core.Time, _timeout), root =>
            {
                var list = BatchReplies.Read(root, _commands.Count);
                var failed = list.Count(r => r.TryGetProperty("error", out var e) && e.ValueKind != JsonValueKind.Null);
                return new Outcome<IReadOnlyList<JsonElement>>(list, null, failed);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            FailAll(e);
            throw;
        }

        var failures = new List<FailedCommand>();
        for (var i = 0; i < _commands.Count; i++)
        {
            var c = _commands[i];
            try
            {
                c.Complete(BatchReplies.Result(c.Method, c.ProOnly, replies[i]));
            }
            catch (CentException e)
            {
                c.Fail(e);
            }
        }
        for (var i = 0; i < _commands.Count; i++)
        {
            if (_commands[i].Pending.Error is { } error)
            {
                failures.Add(new FailedCommand(i, _commands[i].Method, error));
            }
        }
        var result = new BatchResult(_commands.Count, failures);
        return failures.Count > 0 ? throw new BatchFailedCommandsException(result) : result;
    }

    private void FailAll(Exception e)
    {
        foreach (var c in _commands)
        {
            c.Fail(e);
        }
    }
}
