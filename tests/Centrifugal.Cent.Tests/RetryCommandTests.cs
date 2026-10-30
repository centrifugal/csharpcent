using System.Security.Authentication;
using System.Text.Json;

namespace Centrifugal.Cent.Tests;

public sealed class RetryCommandTests
{
    [Fact]
    public void RetryableFollowsTheSpec()
    {
        Assert.True(Retry.IsRetryable(new CentApiException(100, "internal")));
        Assert.True(Retry.IsRetryable(new CentApiException(111, "too many", temporary: true)));
        Assert.False(Retry.IsRetryable(new CentApiException(102, "unknown channel")));
        Assert.False(Retry.IsRetryable(new CentApiException(107, "bad request")));
        Assert.True(Retry.IsRetryable(new CentHttpException(429, "")));
        Assert.True(Retry.IsRetryable(new CentHttpException(503, "")));
        Assert.False(Retry.IsRetryable(new CentHttpException(400, "")));
        Assert.False(Retry.IsRetryable(new CentUnauthorizedException()));
        Assert.False(Retry.IsRetryable(new OperationCanceledException()));
        Assert.False(Retry.IsRetryable(new InvalidOperationException()));
        // An endpoint or token function's own errors, judged by the same rules.
        Assert.True(Retry.IsRetryable(new HttpRequestException("refused")));
        Assert.True(Retry.IsRetryable(new TimeoutException()));
        Assert.False(Retry.IsRetryable(new HttpRequestException("tls", new AuthenticationException("certificate"))));
    }

    [Fact]
    public void EveryErrorIsConstructibleByApplications()
    {
        Assert.True(Retry.IsRetryable(new CentApiException(ErrorCodes.TooManyRequests, "slow down", temporary: true)));
        Assert.True(Retry.IsRetryable(new CentNetworkException("publish", new HttpRequestException("connection reset"))));
        Assert.True(Retry.IsRetryable(new CentTimeoutException("publish", TimeSpan.FromSeconds(10))));
        Assert.False(Retry.IsRetryable(new CentDecodeException("publish", "not JSON")));
        Assert.False(Retry.IsRetryable(new CentInvalidRequestException("publish", "no channel")));
        Assert.False(Retry.IsRetryable(new CentInvalidConfigException("ApiEndpoint is not set")));
        Assert.False(Retry.IsRetryable(new CentClosedException()));
        Assert.False(Retry.IsRetryable(new CentBatchSentException("sent")));
        Assert.False(Retry.IsRetryable(new CentBatchNotSentException()));
        Assert.False(Retry.IsRetryable(new CentRetryStoppedException(new InvalidOperationException("enough"), new CentApiException(100, "x"))));
        Assert.False(Retry.IsRetryable(new Tokens.CentTokenException("no expiry")));
        int code = ErrorCodes.UnknownChannel;
        Assert.Equal(102, code);
    }

    [Fact]
    public void BackoffDoublesUpToItsLimit()
    {
        var b = TimeSpan.FromMilliseconds(100);
        var l = TimeSpan.FromSeconds(1);
        Assert.Equal(b, Retry.Step(b, l, 0));
        Assert.Equal(b, Retry.Step(b, l, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), Retry.Step(b, l, 2));
        Assert.Equal(TimeSpan.FromMilliseconds(800), Retry.Step(b, l, 4));
        Assert.Equal(l, Retry.Step(b, l, 5));
        Assert.Equal(l, Retry.Step(b, l, int.MaxValue));
        Assert.Equal(TimeSpan.MaxValue, Retry.Step(TimeSpan.FromDays(1), TimeSpan.MaxValue, 1000));
    }

    [Fact]
    public void BackoffClampsItsArgumentsAndJitters()
    {
        var zero = Retry.ExponentialBackoff(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(-5));
        Assert.Equal(TimeSpan.Zero, zero(3));
        var limitBelowBase = Retry.ExponentialBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        for (var i = 0; i < 100; i++)
        {
            Assert.InRange(limitBelowBase(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        }
        var delay = Retry.ExponentialBackoff(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
        var seen = new HashSet<TimeSpan>();
        for (var i = 0; i < 100; i++)
        {
            var d = delay(3);
            Assert.InRange(d, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400));
            seen.Add(d);
        }
        Assert.True(seen.Count > 10);
        Assert.InRange(delay(-7), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void ACommandIsTheMethodAndTheRequestAsAnObject()
    {
        var command = Command.Publish(new PublishRequest { Channel = "news", Data = new { text = "hi" }, IdempotencyKey = "k" });
        Assert.Equal("publish", command.Method);
        Assert.Equal("""{"method":"publish","payload":{"channel":"news","data":{"text":"hi"},"idempotency_key":"k"}}""", command.ToJson());
        Assert.Equal(command.ToJson(), System.Text.Encoding.UTF8.GetString(command.ToUtf8Bytes()));
        // Encoded the language's way, held in another value: payload is the object, never a string.
        Assert.Equal(
            """{"Id":1,"Command":{"method":"publish","payload":{"channel":"news","data":{"text":"hi"},"idempotency_key":"k"}}}""",
            JsonSerializer.Serialize(new { Id = 1, Command = command }));
    }

    [Fact]
    public void ACommandValidatesAsACallDoes()
    {
        Assert.Throws<CentInvalidRequestException>(() => Command.Publish(new PublishRequest { Channel = "c" }));
        Assert.Throws<CentInvalidRequestException>(() => Command.Broadcast(new BroadcastRequest { Channels = [""], Data = 1 }));
        Assert.Equal("broadcast", Command.Broadcast(new BroadcastRequest { Channels = ["a"], Data = 1 }).Method);
    }
}
