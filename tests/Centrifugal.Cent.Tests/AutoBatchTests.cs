using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Centrifugal.Cent.Tests;

public sealed class AutoBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fake whose requests wait for the test to release them, one at a time.</summary>
    private sealed class Gate
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _waiting = new();

        public int Waiting => _waiting.Count;

        public Answer Hold(Recorded r)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue(tcs);
            return Answer.When(tcs.Task, Answer.Ok(EchoReply(r)));
        }

        public void ReleaseOne()
        {
            if (_waiting.TryDequeue(out var tcs))
            {
                tcs.SetResult();
            }
        }

        public async Task WaitForAsync(int requests)
        {
            while (Waiting < requests)
            {
                await Task.Delay(2, Ct);
            }
        }
    }

    /// <summary>
    /// A reply echoing each publish's channel as its epoch, so that every call can check it
    /// got its own reply.
    /// </summary>
    private static string EchoReply(Recorded r)
    {
        static JsonNode Result(string method, JsonElement req) =>
            method == "publish"
                ? new JsonObject { ["offset"] = 1, ["epoch"] = req.GetProperty("channel").GetString() }
                : FakeCentrifugo.CommandResult(method, req);
        if (r.ApiMethod != "batch")
        {
            return new JsonObject { ["result"] = Result(r.ApiMethod, r.Json) }.ToJsonString();
        }
        var replies = new JsonArray();
        foreach (var c in r.Json.GetProperty("commands").EnumerateArray())
        {
            var command = c.EnumerateObject().Single();
            replies.Add(command.Value.TryGetProperty("channel", out var ch) && ch.GetString()!.StartsWith("fail", StringComparison.Ordinal)
                ? new JsonObject { ["error"] = new JsonObject { ["code"] = 102, ["message"] = "unknown channel" } }
                : new JsonObject { [command.Name] = Result(command.Name, command.Value) });
        }
        return new JsonObject { ["replies"] = replies }.ToJsonString();
    }

    private static PublishRequest Publish(string channel) => new() { Channel = channel, Data = 1 };

    [Fact]
    public async Task BelowTheCapEveryCallIsSentAloneAtOnce()
    {
        var gate = new Gate();
        var fake = new FakeCentrifugo(gate.Hold);
        await using var client = fake.CreateClient(o => o.AutoBatch = new AutoBatchOptions { MaxInFlight = 3 });
        var calls = Enumerable.Range(0, 3).Select(i => client.PublishAsync(Publish($"c{i}"), Ct)).ToList();
        await gate.WaitForAsync(3);
        Assert.All(fake.Paths, p => Assert.Equal("/api/publish", p));
        for (var i = 0; i < 3; i++)
        {
            gate.ReleaseOne();
        }
        var results = await Task.WhenAll(calls);
        Assert.Equal(["c0", "c1", "c2"], results.Select(r => r.Epoch));
    }

    [Fact]
    public async Task AtTheCapCallsQueueAndGoOutTogetherInOrder()
    {
        var gate = new Gate();
        var infos = new ConcurrentQueue<RequestInfo>();
        var fake = new FakeCentrifugo(gate.Hold);
        await using var client = fake.CreateClient(o =>
        {
            o.AutoBatch = new AutoBatchOptions { MaxInFlight = 1, MaxBatchSize = 3 };
            o.OnRequest = infos.Enqueue;
        });
        var first = client.PublishAsync(Publish("first"), Ct);
        await gate.WaitForAsync(1);
        var queued = new List<Task<PublishResult>>();
        for (var i = 0; i < 5; i++)
        {
            queued.Add(client.PublishAsync(Publish($"q{i}"), Ct));
            await Task.Delay(5, Ct);
        }
        // One request in flight: nothing else is sent until it finishes.
        Assert.Single(fake.Requests);
        gate.ReleaseOne();
        await gate.WaitForAsync(1);
        gate.ReleaseOne();
        await gate.WaitForAsync(1);
        gate.ReleaseOne();

        Assert.Equal("first", (await first).Epoch);
        var results = await Task.WhenAll(queued);
        Assert.Equal(["q0", "q1", "q2", "q3", "q4"], results.Select(r => r.Epoch));
        var requests = fake.Requests;
        Assert.Equal(["/api/publish", "/api/batch", "/api/batch"], requests.Select(r => r.Uri.AbsolutePath));
        Assert.Equal(["publish", "publish", "publish"], requests[1].BatchMethods());
        Assert.Equal(2, requests[2].BatchMethods().Count);
        Assert.True(requests[1].Json.GetProperty("parallel").GetBoolean());
        Assert.Equal(["q0", "q1", "q2"], requests[1].Json.GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("publish").GetProperty("channel").GetString()));
        Assert.Equal([false, true, true], infos.Select(i => i.AutoBatch));
        Assert.Equal([1, 3, 2], infos.Select(i => i.Commands));
    }

    [Fact]
    public async Task EveryCallGetsItsOwnReplyOrError()
    {
        var gate = new Gate();
        var fake = new FakeCentrifugo(gate.Hold);
        await using var client = fake.CreateClient(o => o.AutoBatch = new AutoBatchOptions { MaxInFlight = 1 });
        var first = client.PublishAsync(Publish("first"), Ct);
        await gate.WaitForAsync(1);
        var ok = client.PublishAsync(Publish("ok"), Ct);
        var failing = client.PublishAsync(Publish("fail-1"), Ct);
        var broadcast = client.BroadcastAsync(new BroadcastRequest { Channels = ["x", "y"], Data = 1 }, Ct);
        await Task.Delay(20, Ct);
        gate.ReleaseOne();
        await gate.WaitForAsync(1);
        gate.ReleaseOne();
        await first;
        Assert.Equal("ok", (await ok).Epoch);
        var e = await Assert.ThrowsAsync<CentApiException>(() => failing);
        Assert.Equal(ErrorCodes.UnknownChannel, e.Code);
        Assert.Equal(2, (await broadcast).Channels.Count);
    }

    [Fact]
    public async Task ABatchFailingAsAWholeFailsEveryCallInIt()
    {
        var gate = new Gate();
        var fake = new FakeCentrifugo(r => r.ApiMethod == "batch" ? Answer.WithStatus(413, "too large") : gate.Hold(r));
        await using var client = fake.CreateClient(o => o.AutoBatch = new AutoBatchOptions { MaxInFlight = 1 });
        var first = client.PublishAsync(Publish("first"), Ct);
        await gate.WaitForAsync(1);
        var a = client.PublishAsync(Publish("a"), Ct);
        var b = client.PublishAsync(Publish("b"), Ct);
        await Task.Delay(20, Ct);
        gate.ReleaseOne();
        await first;
        Assert.Equal(413, (await Assert.ThrowsAsync<CentHttpException>(() => a)).StatusCode);
        Assert.Equal(413, (await Assert.ThrowsAsync<CentHttpException>(() => b)).StatusCode);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task ACallWhoseDeadlineEndsWhileQueuedLeavesUnsent()
    {
        var gate = new Gate();
        var fake = new FakeCentrifugo(gate.Hold);
        await using var client = fake.CreateClient(o => o.AutoBatch = new AutoBatchOptions { MaxInFlight = 1 });
        var first = client.PublishAsync(Publish("first"), Ct);
        await gate.WaitForAsync(1);
        var leaving = client.WithTimeout(TimeSpan.FromMilliseconds(50)).PublishAsync(Publish("leaving"), Ct);
        using var cts = new CancellationTokenSource();
        var cancelled = client.PublishAsync(Publish("cancelled"), cts.Token);
        var staying = client.PublishAsync(Publish("staying"), Ct);
        await Assert.ThrowsAsync<CentTimeoutException>(() => leaving);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.ReleaseOne();
        await gate.WaitForAsync(1);
        gate.ReleaseOne();
        await first;
        Assert.Equal("staying", (await staying).Epoch);
        Assert.Equal(["publish"], fake.Requests[1].BatchMethods());
    }

    [Fact]
    public async Task ABatchRunsUntilTheLatestDeadlineOfItsCalls()
    {
        var gate = new Gate();
        var fake = new FakeCentrifugo(r => r.ApiMethod == "batch"
            ? Answer.After(TimeSpan.FromMilliseconds(300), Answer.Ok(EchoReply(r)))
            : gate.Hold(r));
        await using var client = fake.CreateClient(o =>
        {
            o.AutoBatch = new AutoBatchOptions { MaxInFlight = 1 };
            o.RequestTimeout = TimeSpan.FromSeconds(5);
        });
        var first = client.PublishAsync(Publish("first"), Ct);
        await gate.WaitForAsync(1);
        // The early caller leaves at its deadline; the batch goes on for the later one.
        var early = client.WithTimeout(TimeSpan.FromMilliseconds(150)).PublishAsync(Publish("early"), Ct);
        var late = client.PublishAsync(Publish("late"), Ct);
        await Task.Delay(20, Ct);
        gate.ReleaseOne();
        await first;
        await Assert.ThrowsAsync<CentTimeoutException>(() => early);
        Assert.Equal("late", (await late).Epoch);
    }

    [Fact]
    public async Task CloseWaitsForBatchesWhoseCallersLeft()
    {
        var gate = new Gate();
        var batchSent = new TaskCompletionSource();
        var fake = new FakeCentrifugo(r =>
        {
            if (r.ApiMethod == "batch")
            {
                batchSent.TrySetResult();
            }
            return gate.Hold(r);
        });
        var client = fake.CreateClient(o => o.AutoBatch = new AutoBatchOptions { MaxInFlight = 1 });
        var first = client.PublishAsync(Publish("first"), Ct);
        await gate.WaitForAsync(1);
        var leaving = client.WithTimeout(TimeSpan.FromMilliseconds(300)).PublishAsync(Publish("leaving"), Ct);
        await Task.Delay(20, Ct);
        gate.ReleaseOne();
        await first;
        await batchSent.Task;
        await Assert.ThrowsAsync<CentTimeoutException>(() => leaving);
        var close = client.CloseAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(close.IsCompleted);
        gate.ReleaseOne();
        await close;
    }

    [Fact]
    public async Task ABatchRunsInAnEmptyContextMadeByBatchContext()
    {
        var local = new AsyncLocal<string>();
        var gate = new Gate();
        string? seenInBatch = "unset";
        var callers = new List<string?>();
        var fake = new FakeCentrifugo(r =>
        {
            if (r.ApiMethod == "batch")
            {
                seenInBatch = local.Value;
            }
            return gate.Hold(r);
        });
        await using var client = fake.CreateClient(o =>
        {
            o.AutoBatch = new AutoBatchOptions { MaxInFlight = 1 };
            o.BatchContext = contexts =>
            {
                foreach (var c in contexts)
                {
                    ExecutionContext.Run(c, _ => callers.Add(local.Value), null);
                }
                return null;
            };
        });
        local.Value = "first";
        var first = client.PublishAsync(Publish("first"), Ct);
        await gate.WaitForAsync(1);
        local.Value = "a";
        var a = client.PublishAsync(Publish("a"), Ct);
        local.Value = "b";
        var b = client.PublishAsync(Publish("b"), Ct);
        await Task.Delay(20, Ct);
        gate.ReleaseOne();
        await gate.WaitForAsync(1);
        gate.ReleaseOne();
        await Task.WhenAll(first, a, b);
        Assert.Null(seenInBatch);
        Assert.Equal(["a", "b"], callers);
    }

    [Fact]
    public async Task RandomConcurrencyNeverExceedsTheCapAndEveryCallGetsItsReply()
    {
        const int maxInFlight = 4;
        var inFlight = 0;
        var peak = 0;
        var fake = new FakeCentrifugo(r =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            var reply = Answer.After(TimeSpan.FromMilliseconds(Random.Shared.Next(0, 4)), Answer.Ok(EchoReply(r)));
            return new CountingAnswer(reply, () => Interlocked.Decrement(ref inFlight)).Inner;
        });
        await using var client = fake.CreateClient(o => o.AutoBatch = new AutoBatchOptions { MaxInFlight = maxInFlight, MaxBatchSize = 7 });
        var calls = Enumerable.Range(0, 500).Select(async i =>
        {
            await Task.Delay(Random.Shared.Next(0, 20), Ct);
            var channel = i % 13 == 0 ? $"fail-{i}" : $"c{i}";
            try
            {
                var r = await client.PublishAsync(Publish(channel), Ct);
                Assert.Equal(channel, r.Epoch);
            }
            catch (CentApiException e)
            {
                Assert.StartsWith("fail-", channel, StringComparison.Ordinal);
                Assert.Equal(ErrorCodes.UnknownChannel, e.Code);
            }
        });
        await Task.WhenAll(calls);
        Assert.InRange(peak, 1, maxInFlight);
        var commands = fake.Requests.Sum(r => r.ApiMethod == "batch" ? r.BatchMethods().Count : 1);
        Assert.Equal(500, commands);
        Assert.All(fake.Requests.Where(r => r.ApiMethod == "batch"), r => Assert.InRange(r.BatchMethods().Count, 1, 7));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>Decrements a counter when the answer's delay has passed: the request's end.</summary>
    private sealed class CountingAnswer(Answer inner, Action done)
    {
        public Answer Inner { get; } = Answer.When(Task.Run(async () =>
        {
            if (inner is Answer.Delayed d)
            {
                await Task.Delay(d.Delay);
            }
            done();
        }), inner is Answer.Delayed d2 ? d2.Then : inner);
    }

    [Fact]
    public async Task CallsMadeOneAfterAnotherKeepTheirOrder()
    {
        var fake = new FakeCentrifugo(r => Answer.Ok(EchoReply(r)));
        await using var client = fake.CreateClient(o => o.AutoBatch = new AutoBatchOptions { MaxInFlight = 1 });
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal($"c{i}", (await client.PublishAsync(Publish($"c{i}"), Ct)).Epoch);
        }
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"c{i}"), fake.Requests.Select(r => r.Json.GetProperty("channel").GetString()));
    }

    [Fact]
    public async Task TheTransportErrorModeGivesTheSameErrorAloneAndInABatch()
    {
        var gate = new Gate();
        var fake = new FakeCentrifugo(r => r.ApiMethod == "batch"
            ? Answer.Ok(EchoReply(r))
            : r.Json.GetProperty("channel").GetString() == "fail-alone"
                ? Answer.WithStatus(400, """{"code":102,"message":"unknown channel"}""")
                : gate.Hold(r));
        await using var client = fake.CreateClient(o => o.AutoBatch = new AutoBatchOptions { MaxInFlight = 1 });
        var alone = await Assert.ThrowsAsync<CentApiException>(() => client.PublishAsync(Publish("fail-alone"), Ct));
        var first = client.PublishAsync(Publish("first"), Ct);
        await gate.WaitForAsync(1);
        var batched = client.PublishAsync(Publish("fail-batched"), Ct);
        await Task.Delay(20, Ct);
        gate.ReleaseOne();
        await first;
        var inBatch = await Assert.ThrowsAsync<CentApiException>(() => batched);
        Assert.Equal(alone.Code, inBatch.Code);
        Assert.Equal(alone.ApiMessage, inBatch.ApiMessage);
    }
}
