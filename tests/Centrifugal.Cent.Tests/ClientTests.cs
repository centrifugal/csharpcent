using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Centrifugal.Cent.Tests;

public sealed class ClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PublishRequest Publish(string channel = "news") => new() { Channel = channel, Data = new { text = "hi" } };

    [Theory]
    [InlineData(null, "set exactly one of ApiEndpoint and ApiEndpointFunc")]
    [InlineData("localhost:8000/api", "not an absolute http or https URL")]
    [InlineData("ftp://localhost/api", "not an absolute http or https URL")]
    [InlineData("http://localhost:8000/api?x=1", "has a query, fragment or credentials")]
    [InlineData("http://localhost:8000/api#x", "has a query, fragment or credentials")]
    [InlineData("http://user:pass@localhost:8000/api", "has a query, fragment or credentials")]
    [InlineData("http://localhost:8000/api/publish", "ends in the method publish")]
    [InlineData("http://localhost:8000/api/batch/", "ends in the method batch")]
    public void InvalidEndpointsAreRefusedWithAFix(string? endpoint, string message)
    {
        var e = Assert.Throws<CentInvalidConfigException>(() => new CentClient(new CentClientOptions { ApiEndpoint = endpoint }));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointAndEndpointFuncAreNotBoth() =>
        Assert.Throws<CentInvalidConfigException>(() => new CentClient(new CentClientOptions
        {
            ApiEndpoint = "http://localhost:8000/api",
            ApiEndpointFunc = _ => new("http://localhost:8000/api"),
        }));

    [Fact]
    public void ApiKeyAndBearerTokenAreNotBoth()
    {
        var e = Assert.Throws<CentInvalidConfigException>(() => new CentClient(new CentClientOptions
        {
            ApiEndpoint = "http://localhost:8000/api",
            ApiKey = "key",
            BearerTokenFunc = _ => new("token"),
        }));
        Assert.Contains("not both", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("X-API-Key", "v", "is a credential")]
    [InlineData("authorization", "v", "is a credential")]
    [InlineData("Content-Type", "text/plain", "Requests are always JSON")]
    [InlineData("Bad Name", "v", "not a valid header name")]
    [InlineData("X-Ok", "a\nb", "control characters")]
    [InlineData("Host", "", "Host is empty")]
    public void InvalidHeadersAreRefused(string name, string value, string message)
    {
        var e = Assert.Throws<CentInvalidConfigException>(() => new CentClient(new CentClientOptions
        {
            ApiEndpoint = "http://localhost:8000/api",
            Headers = new Dictionary<string, string> { [name] = value },
        }));
        Assert.Contains(message, e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 10)]
    [InlineData(4, 1)]
    public void InvalidAutoBatchIsRefused(int maxInFlight, int maxBatchSize) =>
        Assert.Throws<CentInvalidConfigException>(() => new CentClient(new CentClientOptions
        {
            ApiEndpoint = "http://localhost:8000/api",
            AutoBatch = new AutoBatchOptions { MaxInFlight = maxInFlight, MaxBatchSize = maxBatchSize },
        }));

    [Fact]
    public void ApiKeyWithANewlineIsRefused() =>
        Assert.Throws<CentInvalidConfigException>(() => new CentClient("http://localhost:8000/api", "key\n"));

    [Fact]
    public void NegativeTimeoutIsRefused() =>
        Assert.Throws<CentInvalidConfigException>(() => new CentClient(new CentClientOptions
        {
            ApiEndpoint = "http://localhost:8000/api",
            RequestTimeout = TimeSpan.FromSeconds(-1),
        }));

    [Fact]
    public async Task RequestIsAPostOfJsonWithCredentials()
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient(o => o.ApiEndpoint = "http://centrifugo.test/api/");
        var result = await client.PublishAsync(Publish(), Ct);

        Assert.Equal(1UL, result.Offset);
        Assert.Equal("e", result.Epoch);
        var r = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("http://centrifugo.test/api/publish", r.Uri.ToString());
        Assert.Equal("key", r.Headers["X-API-Key"]);
        Assert.Equal("application/json", r.Headers["Content-Type"]);
        Assert.Equal("csharpcent/0.1", r.Headers["User-Agent"]);
        Assert.Equal("""{"channel":"news","data":{"text":"hi"}}""", r.Body);
    }

    [Fact]
    public async Task ZeroValuesAreNotSentAndBoolValuesAreTriState()
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        await client.SubscribeAsync(new SubscribeRequest
        {
            Channel = "chat",
            User = "42",
            Override = new SubscribeOptionOverride { Presence = true, JoinLeave = false },
        }, Ct);
        Assert.Equal(
            """{"channel":"chat","user":"42","override":{"presence":{"value":true},"join_leave":{"value":false}}}""",
            fake.Requests[0].Body);
    }

    [Fact]
    public async Task HeadersGoOnEveryRequest()
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient(o => o.Headers = new Dictionary<string, string>
        {
            ["X-Request-Source"] = "tests",
            ["User-Agent"] = "app/1",
            ["Host"] = "centrifugo.internal",
        });
        await client.InfoAsync(new InfoRequest(), Ct);
        var r = fake.Requests[0];
        Assert.Equal("tests", r.Headers["X-Request-Source"]);
        Assert.Equal("app/1", r.Headers["User-Agent"]);
        Assert.Equal("centrifugo.internal", r.Headers["Host"]);
    }

    [Fact]
    public async Task ACallersHttpClientIsNotChanged()
    {
        var fake = new FakeCentrifugo();
        using var http = new HttpClient(fake, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(100) };
        await using (var client = new CentClient(new CentClientOptions { ApiEndpoint = "http://centrifugo.test/api", ApiKey = "key", HttpClient = http }))
        {
            await client.InfoAsync(new InfoRequest(), Ct);
        }
        Assert.Empty(http.DefaultRequestHeaders);
        Assert.Null(http.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(100), http.Timeout);
        // Closing the client leaves the caller's HTTP client usable.
        Assert.NotNull(await http.GetAsync(new Uri("http://centrifugo.test/api/info"), Ct));
    }

    [Fact]
    public async Task EndpointFuncIsValidatedPerRequest()
    {
        var endpoint = "http://a.test/api";
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient(o =>
        {
            o.ApiEndpoint = null;
            o.ApiEndpointFunc = _ => new(endpoint);
        });
        await client.InfoAsync(new InfoRequest(), Ct);
        endpoint = "http://b.test/api/info";
        await Assert.ThrowsAsync<CentInvalidConfigException>(() => client.InfoAsync(new InfoRequest(), Ct));
        Assert.Equal(["http://a.test/api/info"], fake.Requests.ConvertAll(r => r.Uri.ToString()));
    }

    [Fact]
    public async Task BearerTokenIsSentPerRequestAndAnEmptyOneFailsBeforeSending()
    {
        var token = "t1";
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient(o =>
        {
            o.ApiKey = null;
            o.BearerTokenFunc = _ => new(token);
        });
        await client.InfoAsync(new InfoRequest(), Ct);
        Assert.Equal("Bearer t1", fake.Requests[0].Headers["Authorization"]);
        Assert.False(fake.Requests[0].Headers.ContainsKey("X-API-Key"));
        token = "";
        await Assert.ThrowsAsync<CentInvalidConfigException>(() => client.InfoAsync(new InfoRequest(), Ct));
        token = "a\r\nb";
        await Assert.ThrowsAsync<CentInvalidConfigException>(() => client.InfoAsync(new InfoRequest(), Ct));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task CentrifugoErrorIsMatchedByCode()
    {
        var fake = new FakeCentrifugo(_ => Answer.Ok("""{"error":{"code":102,"message":"unknown channel"}}"""));
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<CentApiException>(() => client.PublishAsync(Publish(), Ct));
        Assert.Equal(ErrorCodes.UnknownChannel, e.Code);
        Assert.Equal("unknown channel", e.ApiMessage);
        Assert.False(Retry.IsRetryable(e));
    }

    [Fact]
    public async Task UnknownCodeIsStillACentrifugoError()
    {
        var fake = new FakeCentrifugo(_ => Answer.Ok("""{"error":{"code":199,"message":"new","temporary":true}}"""));
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<CentApiException>(() => client.PublishAsync(Publish(), Ct));
        Assert.Equal(199, e.Code);
        Assert.True(e.Temporary);
        Assert.True(Retry.IsRetryable(e));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(416)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task TransportErrorModeGivesTheSameError(int status)
    {
        var infos = new List<RequestInfo>();
        var fake = new FakeCentrifugo(_ => Answer.WithStatus(status, """{"code":102,"message":"unknown channel"}"""));
        await using var client = fake.CreateClient(o => o.OnRequest = infos.Add);
        var e = await Assert.ThrowsAsync<CentApiException>(() => client.PublishAsync(Publish(), Ct));
        Assert.Equal(ErrorCodes.UnknownChannel, e.Code);
        var info = Assert.Single(infos);
        Assert.Equal(status, info.StatusCode);
        Assert.Same(e, info.Error);
        Assert.Equal(0, info.Failed);
    }

    [Fact]
    public async Task AGatewaysCodeIsAnHttpError()
    {
        var fake = new FakeCentrifugo(_ => Answer.WithStatus(429, """{"code":429,"message":"slow down"}""", new Dictionary<string, string> { ["Retry-After"] = "3" }));
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<CentHttpException>(() => client.PublishAsync(Publish(), Ct));
        Assert.Equal(429, e.StatusCode);
        Assert.Equal("3", Assert.Single(e.Headers["retry-after"]));
        Assert.True(Retry.IsRetryable(e));
    }

    [Fact]
    public async Task Status401IsUnauthorized()
    {
        var fake = new FakeCentrifugo(_ => Answer.WithStatus(401));
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<CentUnauthorizedException>(() => client.PublishAsync(Publish(), Ct));
        Assert.Contains("check the credentials", e.Message, StringComparison.Ordinal);
        Assert.False(Retry.IsRetryable(e));
    }

    [Fact]
    public async Task Status404HasAHint()
    {
        var fake = new FakeCentrifugo(_ => Answer.WithStatus(404, "404 page not found"));
        await using var client = fake.CreateClient();
        var plain = await Assert.ThrowsAsync<CentHttpException>(() => client.PublishAsync(Publish(), Ct));
        Assert.Contains("is the endpoint the API's base URL", plain.Message, StringComparison.Ordinal);
        Assert.Equal("404 page not found", plain.Body);
        var pro = await Assert.ThrowsAsync<CentHttpException>(() => client.GetUserStatusAsync(new GetUserStatusRequest { Users = ["1"] }, Ct));
        Assert.Contains("likely Centrifugo OSS", pro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectIsAnHttpErrorWithAHint()
    {
        var fake = new FakeCentrifugo(_ => Answer.WithStatus(301, "", new Dictionary<string, string> { ["Location"] = "https://centrifugo.test/api/publish" }));
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<CentHttpException>(() => client.PublishAsync(Publish(), Ct));
        Assert.Equal(301, e.StatusCode);
        Assert.Contains("check http or https", e.Message, StringComparison.Ordinal);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task ErrorBodyIsBounded()
    {
        var fake = new FakeCentrifugo(_ => Answer.WithStatus(502, new string('e', 100_000)));
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<CentHttpException>(() => client.PublishAsync(Publish(), Ct));
        Assert.Equal(512, e.Body.Length);
        Assert.True(Retry.IsRetryable(e));
    }

    [Theory]
    [InlineData("<html>not found</html>")]
    [InlineData("[]")]
    [InlineData("""{"result":[]}""")]
    [InlineData("""{"result":{"offset":"1"}}""")]
    [InlineData("""{"result":{"Offset":1,"offset":-1}}""")]
    public async Task AReplyNotOfTheReplysShapeIsADecodeError(string reply)
    {
        var fake = new FakeCentrifugo(_ => Answer.Ok(reply));
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<CentDecodeException>(() => client.PublishAsync(Publish(), Ct));
        Assert.False(Retry.IsRetryable(e));
    }

    [Fact]
    public async Task NamesAreCaseSensitiveAndNullIsTheZeroValue()
    {
        var fake = new FakeCentrifugo(_ => Answer.Ok("""{"result":{"Publications":[1],"epoch":null,"offset":7,"extra":1}}"""));
        await using var client = fake.CreateClient();
        var r = await client.HistoryAsync(new HistoryRequest { Channel = "c" }, Ct);
        Assert.Empty(r.Publications);
        Assert.Equal("", r.Epoch);
        Assert.Equal(7UL, r.Offset);
    }

    [Fact]
    public async Task NetworkFailureIsRetryable()
    {
        var fake = new FakeCentrifugo(_ => Answer.Throw(new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused))));
        await using var client = fake.CreateClient();
        var e = await Assert.ThrowsAsync<CentNetworkException>(() => client.PublishAsync(Publish(), Ct));
        Assert.True(Retry.IsRetryable(e));
    }

    [Fact]
    public async Task TheDeadlineEndsACallAsATimeout()
    {
        var fake = new FakeCentrifugo(_ => Answer.After(TimeSpan.FromSeconds(30), Answer.Ok("{}")));
        await using var client = fake.CreateClient(o => o.RequestTimeout = TimeSpan.FromMilliseconds(100));
        var e = await Assert.ThrowsAsync<CentTimeoutException>(() => client.PublishAsync(Publish(), Ct));
        Assert.True(Retry.IsRetryable(e));
        Assert.IsType<TimeoutException>(e.InnerException);

        var longer = client.WithTimeout(TimeSpan.FromMilliseconds(300));
        Assert.Equal(TimeSpan.FromMilliseconds(300), longer.Timeout);
        await Assert.ThrowsAsync<CentTimeoutException>(() => longer.PublishAsync(Publish(), Ct));
    }

    [Fact]
    public async Task AnHttpClientsOwnTimeoutIsATimeoutToo()
    {
        var fake = new FakeCentrifugo(_ => Answer.After(TimeSpan.FromSeconds(30), Answer.Ok("{}")));
        await using var client = fake.CreateClient(o => o.HttpClient = new HttpClient(fake) { Timeout = TimeSpan.FromMilliseconds(100) });
        await Assert.ThrowsAsync<CentTimeoutException>(() => client.PublishAsync(Publish(), Ct));
    }

    [Fact]
    public async Task CancellingACallIsNotATimeoutNorRetryable()
    {
        var fake = new FakeCentrifugo(_ => Answer.After(TimeSpan.FromSeconds(30), Answer.Ok("{}")));
        await using var client = fake.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PublishAsync(Publish(), cts.Token));
        Assert.False(Retry.IsRetryable(e));
    }

    [Fact]
    public void WithTimeoutRefusesANonPositiveTimeout()
    {
        using var client = new CentClient("http://localhost:8000/api");
        Assert.Throws<ArgumentOutOfRangeException>(() => client.WithTimeout(TimeSpan.Zero));
        Assert.Equal(Timeout.InfiniteTimeSpan, client.WithTimeout(Timeout.InfiniteTimeSpan).Timeout);
    }

    [Fact]
    public async Task InvalidRequestsAreNotSent()
    {
        var infos = new List<RequestInfo>();
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient(o => o.OnRequest = infos.Add);
        var noData = await Assert.ThrowsAsync<CentInvalidRequestException>(() => client.PublishAsync(new PublishRequest { Channel = "c" }, Ct));
        Assert.Contains("publish: neither Data nor B64Data", noData.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<CentInvalidRequestException>(() => client.PublishAsync(new PublishRequest { Channel = "", Data = 1 }, Ct));
        await Assert.ThrowsAsync<CentInvalidRequestException>(() => client.BroadcastAsync(new BroadcastRequest { Channels = [], Data = 1 }, Ct));
        var empty = await Assert.ThrowsAsync<CentInvalidRequestException>(() => client.BroadcastAsync(new BroadcastRequest { Channels = ["a", ""], Data = 1 }, Ct));
        Assert.Contains("channel 1 is empty", empty.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<CentInvalidRequestException>(() => client.PublishAsync(null!, Ct));
        Assert.Empty(fake.Requests);
        Assert.Equal(5, infos.Count);
        Assert.All(infos, i => Assert.IsType<CentInvalidRequestException>(i.Error));
        Assert.All(infos, i => Assert.Equal(0, i.StatusCode));
    }

    [Fact]
    public async Task OtherRulesStayCentrifugos()
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient();
        // History with a negative limit: Centrifugo's to judge, not the SDK's.
        await client.HistoryAsync(new HistoryRequest { Channel = "c", Limit = -5 }, Ct);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task OnRequestReportsEveryRequest()
    {
        var infos = new List<RequestInfo>();
        var time = new FakeTimeProvider();
        var fake = new FakeCentrifugo(r =>
        {
            time.Advance(TimeSpan.FromMilliseconds(25));
            return r.ApiMethod == "batch"
                ? Answer.Ok("""{"replies":[{"publish":{}},{"error":{"code":102,"message":"x"}}]}""")
                : Answer.Ok("""{"error":{"code":102,"message":"x"}}""");
        });
        await using var client = fake.CreateClient(o =>
        {
            o.OnRequest = infos.Add;
            o.TimeProvider = time;
        });
        await Assert.ThrowsAsync<CentApiException>(() => client.PublishAsync(Publish(), Ct));
        var batch = client.NewBatch();
        batch.Publish(Publish());
        batch.Publish(Publish());
        await Assert.ThrowsAsync<BatchFailedCommandsException>(() => batch.SendAsync(Ct));

        Assert.Equal(new RequestInfo("publish", 1, 1, false, TimeSpan.FromMilliseconds(25), 200, null), infos[0]);
        Assert.Equal(new RequestInfo("batch", 2, 1, false, TimeSpan.FromMilliseconds(25), 200, null), infos[1]);
    }

    [Fact]
    public async Task OnRequestThrowingIsTheCallsError()
    {
        var fake = new FakeCentrifugo();
        await using var client = fake.CreateClient(o => o.OnRequest = _ => throw new InvalidOperationException("hook"));
        Exception? caught = null;
        try
        {
            await client.InfoAsync(new InfoRequest(), Ct);
        }
        catch (InvalidOperationException e)
        {
            caught = e;
        }
        Assert.Equal("hook", caught?.Message);
    }

    [Fact]
    public async Task CloseRefusesNewCallsAndWaitsForThoseInProgress()
    {
        var release = new TaskCompletionSource();
        var fake = new FakeCentrifugo(_ => Answer.When(release.Task, Answer.Ok("""{"result":{}}""")));
        var client = fake.CreateClient();
        var call = Task.Run(() => client.InfoAsync(new InfoRequest(), Ct), Ct);
        while (fake.Requests.Count == 0)
        {
            await Task.Delay(5, Ct);
        }
        var close = client.CloseAsync(Ct);
        await Assert.ThrowsAsync<CentClosedException>(() => client.InfoAsync(new InfoRequest(), Ct));
        Assert.False(close.IsCompleted);

        using var short1 = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CloseAsync(short1.Token));

        release.SetResult();
        await call;
        await close;
        await client.CloseAsync(Ct);
        var empty = client.NewBatch();
        await Assert.ThrowsAsync<CentClosedException>(() => empty.SendAsync(Ct));
    }

    [Fact]
    public async Task ResultsEncodeWithWireNames()
    {
        var fake = new FakeCentrifugo(_ => Answer.Ok("""{"result":{"num_clients":3,"num_users":2}}"""));
        await using var client = fake.CreateClient();
        var stats = await client.PresenceStatsAsync(new PresenceStatsRequest { Channel = "c" }, Ct);
        Assert.Equal("""{"num_clients":3,"num_users":2}""", JsonSerializer.Serialize(stats));
    }
}
