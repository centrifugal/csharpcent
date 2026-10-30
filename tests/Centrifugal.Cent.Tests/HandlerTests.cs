using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Centrifugal.Cent.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Centrifugal.Cent.Tests;

public sealed class HandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HandlerResponse> Post(CentHandler h, string body, IReadOnlyDictionary<string, string>? headers = null) =>
        h.HandleAsync(new HandlerRequest("POST", Encoding.UTF8.GetBytes(body)) { Headers = headers ?? new Dictionary<string, string>() }, Ct);

    private static string Text(HandlerResponse r) => Encoding.UTF8.GetString(r.Body.Span);

    [Fact]
    public async Task ConnectAnswersWithAResult()
    {
        var errors = new List<Exception>();
        var proxy = new ProxyHandlers(new ProxyHandlerOptions { OnError = (_, e) => errors.Add(e) });
        string? header = null;
        var connect = proxy.Connect((request, context) =>
        {
            header = context.Headers["x-forwarded-for"];
            Assert.Equal(JsonValueKind.Object, request.Data!.Value.ValueKind);
            return Task.FromResult(new ConnectResult { User = "42", Data = request.Data, ExpireAt = 100 });
        });
        var r = await Post(connect, """{"client":"c1","transport":"websocket","protocol":"json","encoding":"json","data":{"token":"t","n":1.0}}""",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Forwarded-For"] = "10.0.0.1" });
        Assert.Equal(200, r.StatusCode);
        Assert.Equal("application/json", r.Headers["Content-Type"]);
        // The client's payload goes back exactly as it came.
        Assert.Equal("""{"result":{"user":"42","expire_at":100,"data":{"token":"t","n":1.0}}}""", Text(r));
        Assert.Equal("10.0.0.1", header);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ErrorsAndDisconnectsAnswerWhereTheProxyTypeCarriesThem()
    {
        var errors = new List<Exception>();
        var proxy = new ProxyHandlers(new ProxyHandlerOptions { OnError = (_, e) => errors.Add(e) });
        var subscribe = proxy.Subscribe((_, _) => throw new ProxyErrorException(403, "permission denied"));
        Assert.Equal("""{"error":{"code":403,"message":"permission denied"}}""", Text(await Post(subscribe, """{"channel":"c"}""")));

        // Wrapped ones count.
        var publish = proxy.Publish((_, _) => throw new InvalidOperationException("wrapper", new ProxyDisconnectException(4501, "unauthorized")));
        Assert.Equal("""{"disconnect":{"code":4501,"reason":"unauthorized"}}""", Text(await Post(publish, """{"channel":"c","data":1}""")));

        var poll = proxy.SharedPollRefresh((_, _) => throw new ProxyErrorException(500, "db down", temporary: true));
        Assert.Equal("""{"error":{"code":500,"message":"db down","temporary":true}}""", Text(await Post(poll, "{}")));
        Assert.Empty(errors);

        // A disconnect where the proxy carries only errors and results; anything where it carries only results.
        var pollDisconnect = proxy.SharedPollRefresh((_, _) => throw new ProxyDisconnectException(4000, "x"));
        Assert.Equal(500, (await Post(pollDisconnect, "{}")).StatusCode);
        var refresh = proxy.Refresh((_, _) => throw new ProxyErrorException(400, "no"));
        Assert.Equal(500, (await Post(refresh, "{}")).StatusCode);
        Assert.Equal(2, errors.Count);
        Assert.Contains("the refresh proxy cannot answer with an error", errors[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultOnlyProxiesEndThroughTheResult()
    {
        var proxy = new ProxyHandlers();
        var refresh = proxy.Refresh((_, _) => Task.FromResult(new Proxy.RefreshResult { Expired = true }));
        Assert.Equal("""{"result":{"expired":true}}""", Text(await Post(refresh, """{"client":"c","user":"42"}""")));
    }

    [Fact]
    public async Task AnyOtherErrorIsA500ReportedToOnError()
    {
        var errors = new List<(HandlerRequest, Exception)>();
        var proxy = new ProxyHandlers(new ProxyHandlerOptions { OnError = (r, e) => errors.Add((r, e)) });
        var rpc = proxy.Rpc((_, _) => throw new InvalidOperationException("database"));
        var r = await Post(rpc, """{"method":"m"}""");
        Assert.Equal(500, r.StatusCode);
        Assert.Equal("database", Assert.Single(errors).Item2.Message);
        var none = proxy.Rpc((_, _) => Task.FromResult<RpcResult>(null!));
        Assert.Equal(500, (await Post(none, "{}")).StatusCode);
        var unencodable = proxy.Rpc((_, _) => Task.FromResult(new RpcResult { Data = double.NaN }));
        Assert.Equal(500, (await Post(unencodable, "{}")).StatusCode);
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public async Task MalformedAndWrongRequestsAreRefused()
    {
        var errors = new List<Exception>();
        var proxy = new ProxyHandlers(new ProxyHandlerOptions { OnError = (_, e) => errors.Add(e), MaxRequestBytes = 100 });
        var connect = proxy.Connect((_, _) => Task.FromResult(new ConnectResult()));
        Assert.Equal(400, (await Post(connect, "not json")).StatusCode);
        Assert.Equal(400, (await Post(connect, """{"client":1}""")).StatusCode);
        Assert.Equal(400, (await Post(connect, new string(' ', 101))).StatusCode);
        var get = await connect.HandleAsync(new HandlerRequest("GET", ReadOnlyMemory<byte>.Empty), Ct);
        Assert.Equal(405, get.StatusCode);
        Assert.Equal("POST", get.Headers["Allow"]);
        Assert.Equal(2, errors.Count);
        Assert.Equal(100, connect.MaxBodyBytes);
        Assert.Equal(16 << 20, new ProxyHandlers().Connect((_, _) => Task.FromResult(new ConnectResult())).MaxBodyBytes);
    }

    [Fact]
    public async Task BinaryModePayloadsArriveInTheB64Fields()
    {
        var proxy = new ProxyHandlers();
        var publish = proxy.Publish((request, _) =>
        {
            Assert.Null(request.Data);
            Assert.Equal("binary", request.Encoding);
            return Task.FromResult(new Proxy.PublishResult { B64Data = request.B64Data });
        });
        Assert.Equal("""{"result":{"b64data":"AQI="}}""", Text(await Post(publish, """{"channel":"c","encoding":"binary","b64data":"AQI="}""")));
    }

    [Fact]
    public void ProxyCodesAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyErrorException(399, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyErrorException(2000, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyDisconnectException(3999, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyDisconnectException(4000, new string('x', 33)));
        Assert.Equal(4999U, new ProxyDisconnectException(4999, new string('x', 32)).Disconnect.Code);
    }

    [Fact]
    public async Task ConnectionTokenEndpoint()
    {
        var endpoints = new EndpointHandlers();
        var h = endpoints.ConnectionToken(context => context.Headers.TryGetValue("Cookie", out _)
            ? Task.FromResult("token")
            : throw new EndpointUnauthorizedException());
        var withCookie = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["cookie"] = "s=1" };
        foreach (var method in new[] { "GET", "POST" })
        {
            var ok = await h.HandleAsync(new HandlerRequest(method, Encoding.UTF8.GetBytes("ignored")) { Headers = withCookie }, Ct);
            Assert.Equal(200, ok.StatusCode);
            Assert.Equal("""{"token":"token"}""", Text(ok));
            Assert.Equal("no-store", ok.Headers["Cache-Control"]);
        }
        Assert.Equal(403, (await Post(h, "")).StatusCode);
        var put = await h.HandleAsync(new HandlerRequest("PUT", ReadOnlyMemory<byte>.Empty), Ct);
        Assert.Equal(405, put.StatusCode);
        Assert.Equal("GET, POST", put.Headers["Allow"]);
    }

    [Fact]
    public async Task SubscriptionTokenEndpoint()
    {
        var errors = new List<Exception>();
        var endpoints = new EndpointHandlers(new EndpointHandlerOptions { OnError = (_, e) => errors.Add(e) });
        var h = endpoints.SubscriptionToken((channel, _) => channel switch
        {
            "secret" => throw new InvalidOperationException("wrapped", new EndpointUnauthorizedException()),
            "broken" => throw new InvalidOperationException("db"),
            _ => Task.FromResult($"token-{channel}"),
        });
        Assert.Equal("""{"token":"token-news"}""", Text(await Post(h, """{"channel":"news"}""")));
        var get = await h.HandleAsync(new HandlerRequest("GET", ReadOnlyMemory<byte>.Empty) { Query = new Dictionary<string, string> { ["channel"] = "news" } }, Ct);
        Assert.Equal("""{"token":"token-news"}""", Text(get));
        Assert.Equal(403, (await Post(h, """{"channel":"secret"}""")).StatusCode);
        Assert.Empty(errors);
        Assert.Equal(400, (await Post(h, "{}")).StatusCode);
        Assert.Equal(400, (await Post(h, "nope")).StatusCode);
        Assert.Equal(500, (await Post(h, """{"channel":"broken"}""")).StatusCode);
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public async Task SharedPollSignatureEndpoint()
    {
        var endpoints = new EndpointHandlers();
        var h = endpoints.SharedPollSignature((channel, keys, _) =>
        {
            var allowed = keys.Where(k => k != "SECRET").ToList();
            return Task.FromResult(new SignedKeys(SharedPoll.Sign("s", "42", channel, allowed, Tokens.Expiry.Never), allowed));
        });
        var r = await Post(h, """{"channel":"poll:prices","keys":["AAPL","SECRET"]}""");
        var answer = JsonDocument.Parse(r.Body).RootElement;
        Assert.Equal(["AAPL"], answer.GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
        Assert.Matches(@"^\d+:0:[0-9a-f]{64}$", answer.GetProperty("signature").GetString());

        var none = await Post(h, """{"channel":"poll:prices","keys":["SECRET"]}""");
        Assert.Equal("[]", JsonDocument.Parse(none.Body).RootElement.GetProperty("keys").GetRawText());

        var extra = endpoints.SharedPollSignature((_, _, _) => Task.FromResult(new SignedKeys("sig", ["NOT-ASKED"])));
        Assert.Equal(500, (await Post(extra, """{"channel":"c","keys":["A"]}""")).StatusCode);
        Assert.Equal(400, (await Post(h, """{"keys":["A"]}""")).StatusCode);
    }

    [Fact]
    public async Task HandlersAreServedByAspNetCore()
    {
        var proxy = new ProxyHandlers();
        var endpoints = new EndpointHandlers();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapCentrifugo("/connect", proxy.Connect((request, context) =>
            Task.FromResult(new ConnectResult { User = context.Request.GetHttpContext().Request.Headers["X-User"].ToString() })));
        app.MapCentrifugo("/token", endpoints.SubscriptionToken((channel, _) => Task.FromResult(channel)));
        app.MapCentrifugo("/jwks", Tokens.Jwks.Endpoint([Tokens.SigningKey.ECDsa(System.Security.Cryptography.ECDsa.Create(), "k")]));
        app.MapCentrifugo("/small", new ProxyHandlers(new ProxyHandlerOptions { MaxRequestBytes = 10 }).Connect((_, _) => Task.FromResult(new ConnectResult())));
        await app.StartAsync(Ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address) };

        using var connect = new HttpRequestMessage(HttpMethod.Post, "/connect") { Content = new StringContent("""{"client":"c"}""") };
        connect.Headers.Add("X-User", "42");
        var r = await http.SendAsync(connect, Ct);
        Assert.Equal("""{"result":{"user":"42"}}""", await r.Content.ReadAsStringAsync(Ct));
        Assert.Equal("application/json", r.Content.Headers.ContentType!.MediaType);

        Assert.Equal("""{"token":"news"}""", await http.GetStringAsync("/token?channel=news", Ct));
        var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/jwks"), Ct);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal("public, max-age=300", head.Headers.CacheControl!.ToString());
        var wrong = await http.DeleteAsync("/connect", Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrong.StatusCode);
        Assert.Equal("POST", wrong.Content.Headers.Allow.Single());
        var big = await http.PostAsync("/small", new StringContent(new string('x', 1_000_000)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, big.StatusCode);
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task TheClientIsRegisteredAndClosedByDependencyInjection()
    {
        var services = new ServiceCollection();
        services.AddCentClient(o => o.ApiEndpoint = "http://localhost:8000/api");
        CentClient client;
        await using (var provider = services.BuildServiceProvider())
        {
            client = provider.GetRequiredService<CentClient>();
            Assert.Same(client, provider.GetRequiredService<CentClient>());
        }
        await Assert.ThrowsAsync<CentClosedException>(() => client.InfoAsync(new InfoRequest(), Ct));
    }
}
