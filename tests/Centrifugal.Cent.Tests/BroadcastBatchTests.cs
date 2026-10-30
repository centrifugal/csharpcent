using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Centrifugal.Cent.Tests;

public sealed class BroadcastBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BroadcastRequest Broadcast(params string[] channels) => new() { Channels = channels, Data = new { n = 1 } };

    /// <summary>A broadcast reply: per channel, a publication or the error code given.</summary>
    private static string Responses(Recorded r, Func<string, int?> error)
    {
        var responses = new JsonArray();
        var i = 0;
        foreach (var ch in r.Json.GetProperty("channels").EnumerateArray())
        {
            i++;
            responses.Add(error(ch.GetString()!) is { } code
                ? new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = $"error {code}" } }
                : new JsonObject { ["result"] = new JsonObject { ["offset"] = i, ["epoch"] = "e" } });
        }
        return new JsonObject { ["result"] = new JsonObject { ["responses"] = responses } }.ToJsonString();
    }

    [Fact]
    public async Task EveryChannelHasAnOutcomeInOrder()
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        var result = await client.BroadcastAsync(Broadcast("a", "b", "a"), Ct);
        Assert.Equal(["a", "b", "a"], result.Channels.Select(c => c.Channel));
        Assert.Equal([1UL, 2UL, 3UL], result.Channels.Select(c => c.Result!.Offset));
        Assert.True(result.Replied);
        Assert.Empty(result.Failed);
        Assert.All(result.Channels, c => Assert.Equal(1, c.Attempts));
    }

    [Fact]
    public async Task SomeChannelsFailingIsAPartialErrorHoldingEveryOutcome()
    {
        var fake = new FakeCentrifugo(r => Answer.Ok(Responses(r, ch => ch switch { "b" => 102, "c" => 100, _ => null })));
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<BroadcastFailedChannelsException>(() => client.BroadcastAsync(Broadcast("a", "b", "c"), Ct));
        Assert.Equal(3, e.Total);
        Assert.Equal(["b", "c"], e.Failed.Select(c => c.Channel));
        Assert.Equal(1UL, e.Result.Channels[0].Result!.Offset);
        Assert.Equal(ErrorCodes.UnknownChannel, Assert.IsType<CentApiException>(e.Failed[0].Error).Code);
        Assert.Equal(["c"], e.Result.RetryableChannels());
        Assert.True(Retry.IsRetryable(e));
        Assert.Equal(
            "broadcast: 2 of 3 channels failed, 1 got the publication: b: centrifugo error 102: error 102 (retry only RetryableChannels, with the same IdempotencyKey)",
            e.Message);
    }

    [Fact]
    public async Task APartialErrorNeverMatchesItsParts()
    {
        var fake = new FakeCentrifugo(r => Answer.Ok(Responses(r, _ => 102)));
        await using var client = fake.CreateClient();
        // Every channel failing, one channel alone: still the partial error, never a CentApiException.
        var all = await Record.ExceptionAsync(() => client.BroadcastAsync(Broadcast("a", "b"), Ct));
        var one = await Record.ExceptionAsync(() => client.BroadcastAsync(Broadcast("a"), Ct));
        Assert.IsType<BroadcastFailedChannelsException>(all);
        Assert.IsType<BroadcastFailedChannelsException>(one);
        Assert.False(Retry.IsRetryable(one));
        Assert.DoesNotContain("retry only", one.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailingAsAWholeIsItsOwnError()
    {
        var fake = new FakeCentrifugo(_ => Answer.WithStatus(503));
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<CentHttpException>(() => client.BroadcastAsync(Broadcast("a", "b"), Ct));
        Assert.Equal(503, e.StatusCode);
    }

    [Fact]
    public async Task AWrongResponseCountIsADecodeError()
    {
        var fake = new FakeCentrifugo(_ => Answer.Ok("""{"result":{"responses":[{"result":{}}]}}"""));
        await using var client = fake.CreateClient();
        await Assert.ThrowsAsync<CentDecodeException>(() => client.BroadcastAsync(Broadcast("a", "b"), Ct));
    }

    [Fact]
    public void PartialErrorsAreConstructibleByApplications()
    {
        var result = new BroadcastResult([new ChannelResult("a", new PublishResult { Offset = 1 }), new ChannelResult("b", new CentApiException(100, "internal"))], "key");
        var e = new BroadcastFailedChannelsException(result);
        Assert.Equal(["b"], e.Failed.Select(c => c.Channel));
        Assert.Equal(["b"], result.RetryableChannels());
        var batch = new BatchFailedCommandsException(new BatchResult(2, [new FailedCommand(1, "publish", new CentApiException(102, "x"))]));
        Assert.Equal(2, batch.Total);
        Assert.Equal("batch: 1 of 2 commands failed, the others succeeded: command 1 (publish): centrifugo error 102: x", batch.Message);
    }

    [Fact]
    public void ResultsEncodeWithWireNamesAndErrorsAsCodeAndMessage()
    {
        var result = new BroadcastResult([
            new ChannelResult("a", new PublishResult { Offset = 1, Epoch = "e" }),
            new ChannelResult("b", new CentApiException(102, "unknown channel")),
            new ChannelResult("c", new TimeoutException("slow")),
        ], "key");
        Assert.Equal(
            """{"channels":[{"channel":"a","result":{"offset":1,"epoch":"e"},"error":null,"attempts":1},{"channel":"b","result":null,"error":{"code":102,"message":"unknown channel"},"attempts":1},{"channel":"c","result":null,"error":{"message":"slow"},"attempts":1}],"idempotency_key":"key","replied":true}""",
            JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task RetrySendsOnlyTheChannelsWhichMayStillSucceedWithOneKey()
    {
        var attempt = 0;
        var fake = new FakeCentrifugo(r =>
        {
            attempt++;
            return Answer.Ok(Responses(r, ch => (attempt, ch) switch
            {
                (1, "b") => 100, // temporary: retried
                (1, "c") => 102, // for good: not retried
                _ => null,
            }));
        });
        var attempts = new List<BroadcastAttempt>();
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<BroadcastFailedChannelsException>(() => client.BroadcastWithRetryAsync(Broadcast("a", "b", "c"), new BroadcastRetryOptions
        {
            MaxAttempts = 3,
            OnAttemptError = a =>
            {
                attempts.Add(a);
                return TimeSpan.Zero;
            },
        }, Ct));

        Assert.Equal(2, fake.Requests.Count);
        var keys = fake.Requests.Select(r => r.Json.GetProperty("idempotency_key").GetString()).Distinct().ToList();
        var key = Assert.Single(keys);
        Assert.Matches("^csharpcent-[A-Z2-7]{26}$", key);
        Assert.Equal(["b"], fake.Requests[1].Json.GetProperty("channels").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(key, e.Result.IdempotencyKey);
        Assert.Equal([1, 2, 1], e.Result.Channels.Select(c => c.Attempts));
        Assert.Equal(["c"], e.Failed.Select(c => c.Channel));
        var a = Assert.Single(attempts);
        Assert.Equal(1, a.Attempt);
        Assert.Equal(["b"], a.RetryChannels);
        Assert.Equal(key, a.IdempotencyKey);
    }

    [Fact]
    public async Task RetryKeepsTheRequestsKeyAndSucceeds()
    {
        var attempt = 0;
        var fake = new FakeCentrifugo(r => ++attempt == 1 ? Answer.WithStatus(503) : Answer.Ok(Responses(r, _ => null)));
        await using var client = fake.CreateClient();
        var request = Broadcast("a", "b");
        request.IdempotencyKey = "order-42-paid";
        var result = await client.BroadcastWithRetryAsync(request, new BroadcastRetryOptions { OnAttemptError = _ => TimeSpan.Zero }, Ct);
        Assert.Equal("order-42-paid", result.IdempotencyKey);
        Assert.All(fake.Requests, r => Assert.Equal("order-42-paid", r.Json.GetProperty("idempotency_key").GetString()));
        Assert.Equal([2, 2], result.Channels.Select(c => c.Attempts));
        Assert.Equal(["a", "b"], request.Channels);
    }

    [Fact]
    public async Task AFailureNoRetryCanFixStopsAtOnce()
    {
        var called = false;
        var fake = new FakeCentrifugo(_ => Answer.WithStatus(401));
        await using var client = fake.CreateClient();
        await Assert.ThrowsAsync<CentUnauthorizedException>(() => client.BroadcastWithRetryAsync(Broadcast("a"), new BroadcastRetryOptions
        {
            MaxAttempts = 5,
            OnAttemptError = _ =>
            {
                called = true;
                return null;
            },
        }, Ct));
        Assert.False(called);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task ThrowingFromTheCallbackStopsTheRetries()
    {
        var fake = new FakeCentrifugo(r => Answer.Ok(Responses(r, ch => ch == "b" ? 100 : null)));
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<CentRetryStoppedException>(() => client.BroadcastWithRetryAsync(Broadcast("a", "b"), new BroadcastRetryOptions
        {
            MaxAttempts = 5,
            OnAttemptError = _ => throw new InvalidOperationException("enough"),
        }, Ct));
        Assert.Equal("enough", e.InnerException!.Message);
        var partial = Assert.IsType<BroadcastFailedChannelsException>(e.BroadcastError);
        Assert.Equal(["b"], partial.Failed.Select(c => c.Channel));
        Assert.Single(fake.Requests);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(2, -1)]
    public async Task InvalidRetryOptionsAreRefusedBeforeAnyAttempt(int maxAttempts, int? attemptTimeoutMs)
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        await Assert.ThrowsAsync<CentInvalidRequestException>(() => client.BroadcastWithRetryAsync(Broadcast("a"), new BroadcastRetryOptions
        {
            MaxAttempts = maxAttempts,
            AttemptTimeout = attemptTimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
        }, Ct));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task AnInvalidRequestIsNotRetried()
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        await Assert.ThrowsAsync<CentInvalidRequestException>(() => client.BroadcastWithRetryAsync(new BroadcastRequest { Channels = ["a"] }, null, Ct));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task NoAttemptStartsAfterTheDeadline()
    {
        var time = new FakeTimeProvider();
        var fake = new FakeCentrifugo(r => Answer.Ok(Responses(r, _ => 100)));
        await using var client = fake.CreateClient(o =>
        {
            o.TimeProvider = time;
            o.RequestTimeout = TimeSpan.FromSeconds(5);
        });
        var e = await Assert.ThrowsAsync<CentRetryStoppedException>(() => client.BroadcastWithRetryAsync(Broadcast("a"), new BroadcastRetryOptions
        {
            MaxAttempts = 5,
            OnAttemptError = _ =>
            {
                time.Advance(TimeSpan.FromSeconds(6));
                return TimeSpan.Zero;
            },
        }, Ct));
        Assert.Single(fake.Requests);
        Assert.IsType<CentTimeoutException>(e.InnerException);
        Assert.Equal(1, Assert.IsType<BroadcastFailedChannelsException>(e.BroadcastError).Result.Channels[0].Attempts);
    }

    [Fact]
    public async Task ADeadlinePassingInTheWaitStopsTheRetries()
    {
        var time = new FakeTimeProvider();
        var fake = new FakeCentrifugo(r => Answer.Ok(Responses(r, _ => 100)));
        await using var client = fake.CreateClient(o =>
        {
            o.TimeProvider = time;
            o.RequestTimeout = TimeSpan.FromSeconds(5);
        });
        var call = client.BroadcastWithRetryAsync(Broadcast("a"), new BroadcastRetryOptions { MaxAttempts = 5, OnAttemptError = _ => TimeSpan.FromMinutes(1) }, Ct);
        while (!call.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, Ct);
        }
        var e = await Assert.ThrowsAsync<CentRetryStoppedException>(() => call);
        Assert.IsType<CentTimeoutException>(e.InnerException);
        Assert.IsType<BroadcastFailedChannelsException>(e.BroadcastError);
        Assert.True(Retry.IsRetryable(e));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task BatchCommandsHaveTheirOwnOutcomes()
    {
        var fake = new FakeCentrifugo(_ => Answer.Ok("""{"replies":[{"publish":{"offset":5,"epoch":"e"}},{"error":{"code":104,"message":"not found"}},{"presence":{"presence":{"c1":{"client":"c1","user":"42"}}}}]}"""));
        await using var client = fake.CreateClient();
        var batch = client.NewBatch(parallel: true);
        var publish = batch.Publish(new PublishRequest { Channel = "a", Data = 1 });
        var status = batch.GetUserStatus(new GetUserStatusRequest { Users = ["42"] });
        var presence = batch.Presence(new PresenceRequest { Channel = "a" });
        Assert.Throws<CentBatchNotSentException>(() => publish.Result);
        Assert.Equal(3, batch.Count);

        var e = await Assert.ThrowsAsync<BatchFailedCommandsException>(() => batch.SendAsync(Ct));
        Assert.Equal(5UL, publish.Result.Offset);
        Assert.Equal("42", presence.Result.Presence["c1"].User);
        var notFound = Assert.IsType<CentApiException>(status.Error);
        Assert.Equal(ErrorCodes.NotFound, notFound.Code);
        Assert.Contains("the server may be Centrifugo OSS", notFound.Message, StringComparison.Ordinal);
        Assert.Throws<CentApiException>(() => status.Result);
        var failed = Assert.Single(e.Failed);
        Assert.Equal((1, "get_user_status"), (failed.Index, failed.Method));
        Assert.Equal(3, e.Total);
        Assert.True(fake.Requests[0].Json.GetProperty("parallel").GetBoolean());
    }

    [Fact]
    public async Task ABatchIsSentOnce()
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        var batch = client.NewBatch();
        batch.Info(new InfoRequest());
        await batch.SendAsync(Ct);
        await Assert.ThrowsAsync<CentBatchSentException>(() => batch.SendAsync(Ct));
        Assert.Throws<CentBatchSentException>(() => batch.Info(new InfoRequest()));
        Assert.Single(fake.Requests);
        Assert.False(fake.Requests[0].Json.TryGetProperty("parallel", out _));
    }

    [Fact]
    public async Task AnEmptyBatchSucceedsWithoutARequest()
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        var result = await client.NewBatch().SendAsync(Ct);
        Assert.Equal(0, result.Total);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task AnInvalidCommandFailsTheWholeBatchBeforeSending()
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        var batch = client.NewBatch();
        var ok = batch.Info(new InfoRequest());
        batch.Publish(new PublishRequest { Channel = "c" });
        var e = await Assert.ThrowsAsync<CentInvalidRequestException>(() => batch.SendAsync(Ct));
        Assert.StartsWith("batch: command 1 (publish):", e.Message, StringComparison.Ordinal);
        Assert.Same(e, ok.Error);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task ABatchFailingAsAWholeFailsEveryCommand()
    {
        var fake = new FakeCentrifugo(_ => Answer.WithStatus(500, "oops"));
        await using var client = fake.CreateClient();
        var batch = client.NewBatch();
        var a = batch.Info(new InfoRequest());
        var b = batch.Info(new InfoRequest());
        var e = await Assert.ThrowsAsync<CentHttpException>(() => batch.SendAsync(Ct));
        Assert.Same(e, a.Error);
        Assert.Same(e, b.Error);
    }

    [Fact]
    public async Task AWrongReplyCountIsADecodeError()
    {
        var fake = new FakeCentrifugo(_ => Answer.Ok("""{"replies":[{"info":{}}]}"""));
        await using var client = fake.CreateClient();
        var batch = client.NewBatch();
        batch.Info(new InfoRequest());
        batch.Info(new InfoRequest());
        await Assert.ThrowsAsync<CentDecodeException>(() => batch.SendAsync(Ct));
    }

    [Fact]
    public async Task ABroadcastInABatchReturnsWhatADirectOneDoes()
    {
        var fake = new FakeCentrifugo(r => Answer.Ok(
            """{"replies":[{"broadcast":{"responses":[{"result":{"offset":1}},{"error":{"code":102,"message":"x"}}]}},{"broadcast":{"responses":[{"result":{"offset":1}}]}}]}"""));
        await using var client = fake.CreateClient();
        var batch = client.NewBatch();
        var partial = batch.Broadcast(Broadcast("a", "b"));
        var whole = batch.Broadcast(Broadcast("c"));
        var e = await Assert.ThrowsAsync<BatchFailedCommandsException>(() => batch.SendAsync(Ct));
        var failed = Assert.IsType<BroadcastFailedChannelsException>(partial.Error);
        Assert.Equal(["b"], failed.Failed.Select(c => c.Channel));
        Assert.Equal(1UL, whole.Result.Channels[0].Result!.Offset);
        Assert.Equal(0, Assert.Single(e.Failed).Index);
    }
}
