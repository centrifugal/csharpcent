using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Centrifugal.Cent.Tokens;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Centrifugal.Cent.Tests;

/// <summary>
/// Every method against a real Centrifugo, set up as testdata/ says: run with
/// <c>CENTRIFUGO_TESTS=1</c>, and <c>CSHARPCENT_TEST_PRO=1</c> to require Centrifugo PRO.
/// </summary>
public sealed class IntegrationTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("CENTRIFUGO_TESTS") is { Length: > 0 };
    private static readonly string Address = Environment.GetEnvironmentVariable("CSHARPCENT_TEST_ADDR") ?? "http://localhost:8100/api";
    private static int _counter;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CentClientOptions Options() => new()
    {
        ApiEndpoint = Address,
        ApiKey = Environment.GetEnvironmentVariable("CSHARPCENT_TEST_API_KEY") ?? "api_key",
    };

    private static async Task<CentClient> Client()
    {
        Assert.SkipUnless(Enabled, "set CENTRIFUGO_TESTS=1 with Centrifugo running");
        var client = new CentClient(Options());
        try
        {
            await client.InfoAsync(new InfoRequest(), Ct);
        }
        catch (CentException e)
        {
            Assert.Fail($"Centrifugo not reachable at {Address}: {e.Message}");
        }
        return client;
    }

    /// <summary>The client when the server is Centrifugo PRO; skips otherwise.</summary>
    private static async Task<CentClient> Pro()
    {
        var client = await Client();
        try
        {
            await client.ConnectionsAsync(new ConnectionsRequest { User = "nobody" }, Ct);
        }
        catch (CentHttpException e) when (e.StatusCode == 404)
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("CSHARPCENT_TEST_PRO") is null, "Centrifugo OSS: PRO methods skipped");
            Assert.Fail($"CSHARPCENT_TEST_PRO is set, but the server is not Centrifugo PRO: {e.Message}");
        }
        return client;
    }

    private static string Unique(string prefix = "") =>
        $"{prefix}csharpcent-{DateTime.UtcNow.Ticks:x}-{Interlocked.Increment(ref _counter)}";

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>A client connection over Centrifugo's unidirectional HTTP stream: one JSON message per line.</summary>
    private sealed class Stream : IAsyncDisposable
    {
        private readonly Channel<JsonNode?> _messages = System.Threading.Channels.Channel.CreateUnbounded<JsonNode?>();
        private readonly CancellationTokenSource _stop = new();
        private readonly HttpClient _http = new();
        private readonly Task _read;

        public Stream(JsonNode body)
        {
            var url = Address[..^"/api".Length] + "/connection/uni_http_stream";
            _read = Task.Run(async () =>
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString()) };
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stop.Token);
                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(_stop.Token));
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        line = line.Trim();
                        if (line.Length > 0 && line != "{}")
                        {
                            await _messages.Writer.WriteAsync(JsonNode.Parse(line), _stop.Token);
                        }
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or IOException or HttpRequestException)
                {
                }
                finally
                {
                    _messages.Writer.TryComplete();
                }
            });
        }

        /// <summary>The next message; null once the connection is closed.</summary>
        public async Task<JsonNode?> NextAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await _messages.Reader.WaitToReadAsync(timeout.Token) && _messages.Reader.TryRead(out var m) ? m : null;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _read;
            _http.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>Serves handlers on all interfaces at a port, where Centrifugo reaches them as host.docker.internal.</summary>
    private static async Task<WebApplication> Serve(int port, params (string Path, CentHandler Handler)[] routes)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        foreach (var (path, handler) in routes)
        {
            app.MapCentrifugo(path, handler);
        }
        await app.StartAsync(Ct);
        return app;
    }

    private static int Port(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out var p) ? p : fallback;

    /// <summary>An Ed25519 key from a seed, through BouncyCastle as the docs show.</summary>
    private static SigningKey Ed25519(byte seed, string? keyId = null) =>
        TokenTests.Ed25519(keyId, Enumerable.Repeat(seed, 32).ToArray()).Key;

    [Fact]
    public async Task PublishesAndReadsHistoryPayloadsExactly()
    {
        await using var c = await Client();
        var ch = Unique();
        const string text = """{"n":1.50,"big":9007199254740993,"empty":{},"keys":{"0":"a"}}""";
        var res = await c.PublishAsync(new PublishRequest { Channel = ch, Data = RawJson.Parse(text), Tags = new Dictionary<string, string> { ["k"] = "v" } }, Ct);
        Assert.Equal(1UL, res.Offset);
        Assert.NotEmpty(res.Epoch);
        var pubs = (await c.HistoryAsync(new HistoryRequest { Channel = ch, Limit = 10 }, Ct)).Publications;
        var pub = Assert.Single(pubs);
        Assert.Equal("v", pub.Tags["k"]);
        // Sent back unchanged, the payload is the same JSON value - the integer past 2^53 included.
        Assert.Equal("9007199254740993", pub.Data!.Value.GetProperty("big").GetRawText());
        await c.PublishAsync(new PublishRequest { Channel = ch, Data = pub.Data }, Ct);
        var again = (await c.HistoryAsync(new HistoryRequest { Channel = ch, Limit = 10, Reverse = true }, Ct)).Publications;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(again[0].Data!.Value.GetRawText())));
        Assert.Equal("1.50", again[0].Data!.Value.GetProperty("n").GetRawText());
        await c.HistoryRemoveAsync(new HistoryRemoveRequest { Channel = ch }, Ct);
        Assert.Empty((await c.HistoryAsync(new HistoryRequest { Channel = ch, Limit = 10 }, Ct)).Publications);
    }

    [Fact]
    public async Task PublishesOncePerIdempotencyKey()
    {
        await using var c = await Client();
        var ch = Unique();
        var first = await c.PublishAsync(new PublishRequest { Channel = ch, Data = 1, IdempotencyKey = "k1" }, Ct);
        var second = await c.PublishAsync(new PublishRequest { Channel = ch, Data = 2, IdempotencyKey = "k1" }, Ct);
        Assert.Equal(first.Offset, second.Offset);
        Assert.Single((await c.HistoryAsync(new HistoryRequest { Channel = ch, Limit = 10 }, Ct)).Publications);
    }

    [Fact]
    public async Task Broadcasts()
    {
        await using var c = await Client();
        var (a, b) = (Unique("a-"), Unique("b-"));
        var res = await c.BroadcastAsync(new BroadcastRequest { Channels = [a, b], Data = new { x = 1 } }, Ct);
        Assert.Equal([1UL, 1UL], res.Channels.Select(r => r.Result!.Offset));
        var e = await Assert.ThrowsAsync<BroadcastFailedChannelsException>(() => c.BroadcastAsync(new BroadcastRequest { Channels = [a, "unknown:x"], Data = 2 }, Ct));
        var failed = Assert.Single(e.Failed);
        Assert.Equal("unknown:x", failed.Channel);
        Assert.Equal(ErrorCodes.UnknownChannel, Assert.IsType<CentApiException>(failed.Error).Code);
        Assert.NotNull(e.Result.Channels[0].Result);
        Assert.False(Retry.IsRetryable(e));

        // With retry, under one key: sent again with it, nothing is published again.
        var retried = await c.BroadcastWithRetryAsync(new BroadcastRequest { Channels = [a], Data = 1 }, null, Ct);
        Assert.StartsWith("csharpcent-", retried.IdempotencyKey, StringComparison.Ordinal);
        var before = (await c.HistoryAsync(new HistoryRequest { Channel = a, Limit = 10 }, Ct)).Publications.Count;
        await c.BroadcastAsync(new BroadcastRequest { Channels = [a], Data = 1, IdempotencyKey = retried.IdempotencyKey }, Ct);
        Assert.Equal(before, (await c.HistoryAsync(new HistoryRequest { Channel = a, Limit = 10 }, Ct)).Publications.Count);
    }

    [Fact]
    public async Task Batches()
    {
        await using var c = await Client();
        var ch = Unique();
        var batch = c.NewBatch();
        var publication = batch.Publish(new PublishRequest { Channel = ch, Data = 1 });
        var history = batch.History(new HistoryRequest { Channel = ch, Limit = 1 });
        var bad = batch.Publish(new PublishRequest { Channel = "unknown:x", Data = 1 });
        var e = await Assert.ThrowsAsync<BatchFailedCommandsException>(() => batch.SendAsync(Ct));
        Assert.Equal([2], e.Failed.Select(f => f.Index));
        Assert.Equal(1UL, publication.Result.Offset);
        Assert.Single(history.Result.Publications);
        Assert.Equal(ErrorCodes.UnknownChannel, Assert.IsType<CentApiException>(bad.Error).Code);
    }

    [Fact]
    public async Task RefusedRequestsAndProHints()
    {
        await using var c = await Client();
        var e = await Assert.ThrowsAsync<CentApiException>(() => c.HistoryAsync(new HistoryRequest { Channel = "unknown:x" }, Ct));
        Assert.False(Retry.IsRetryable(e));
        try
        {
            await c.ConnectionsAsync(new ConnectionsRequest { User = "nobody" }, Ct);
            return; // Centrifugo PRO
        }
        catch (CentHttpException http)
        {
            Assert.Equal(404, http.StatusCode);
            Assert.Contains("likely Centrifugo OSS", http.Message, StringComparison.Ordinal);
        }
        var batch = c.NewBatch();
        var conns = batch.Connections(new ConnectionsRequest { User = "nobody" });
        await Assert.ThrowsAsync<BatchFailedCommandsException>(() => batch.SendAsync(Ct));
        var notFound = Assert.IsType<CentApiException>(conns.Error);
        Assert.Equal(ErrorCodes.NotFound, notFound.Code);
        Assert.Contains("may be Centrifugo OSS", notFound.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InfoAndChannels()
    {
        await using var c = await Client();
        var info = await c.InfoAsync(new InfoRequest(), Ct);
        Assert.NotEmpty(info.Nodes);
        Assert.NotEmpty(info.Nodes[0].Version);
        await c.ChannelsAsync(new ChannelsRequest { Pattern = "csharpcent-*" }, Ct);
    }

    [Fact]
    public async Task MapsAndSharedPolls()
    {
        await using var c = await Client();
        var ch = "map:" + Unique();
        await c.MapPublishAsync(new MapPublishRequest { Channel = ch, Key = "k1", Data = new { v = 1 } }, Ct);
        await c.MapPublishAsync(new MapPublishRequest { Channel = ch, Key = "k2", Data = new { v = 2 } }, Ct);
        var state = await c.MapReadStateAsync(new MapReadStateRequest { Channel = ch, Limit = 10 }, Ct);
        Assert.Equal(["k1", "k2"], state.Entries.Select(e => e.Key).Order());
        await c.MapReadStreamAsync(new MapReadStreamRequest { Channel = ch, Limit = 10 }, Ct);
        await c.MapRemoveAsync(new MapRemoveRequest { Channel = ch, Key = "k1" }, Ct);
        Assert.Equal(1, (await c.MapStatsAsync(new MapStatsRequest { Channel = ch }, Ct)).NumKeys);
        await c.MapClearAsync(new MapClearRequest { Channel = ch }, Ct);
        await c.SharedPollPublishAsync(new SharedPollPublishRequest { Channel = "poll:" + Unique(), Key = "k", Version = 1, Data = new { v = 1 } }, Ct);
    }

    [Fact]
    public async Task BatchesConcurrentCallsAutomaticallyInOrderPerCaller()
    {
        await using var plain = await Client();
        var batched = 0;
        var options = Options();
        options.AutoBatch = new AutoBatchOptions { MaxInFlight = 2 };
        options.OnRequest = i =>
        {
            if (i.AutoBatch)
            {
                Interlocked.Increment(ref batched);
            }
        };
        var client = new CentClient(options);
        var ch = Unique();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(w => Task.Run(async () =>
        {
            for (var n = 0; n < 20; n++)
            {
                await client.PublishAsync(new PublishRequest { Channel = $"{ch}-{w}", Data = new { n } }, Ct);
            }
        }, Ct)));
        await client.CloseAsync(Ct);
        Assert.True(batched > 0);
        for (var w = 0; w < 16; w++)
        {
            var pubs = (await plain.HistoryAsync(new HistoryRequest { Channel = $"{ch}-{w}", Limit = 100 }, Ct)).Publications;
            Assert.Equal(Enumerable.Range(0, 20), pubs.Select(p => p.Data!.Value.GetProperty("n").GetInt32()));
        }
    }

    [Fact]
    public async Task TokensConnectARealClient()
    {
        await using var c = await Client();
        var exp = Expiry.After(TimeSpan.FromHours(1));
        // HMAC, and Ed25519 verified by client.token.ed25519_public_key: the public key of the
        // seed of 32 bytes of 2.
        foreach (var (name, key) in new[] { ("hmac", SigningKey.Hmac("client_secret")), ("ed25519", Ed25519(2)) })
        {
            var user = Unique(name + "-");
            var ch = user + "-news";
            var token = Token.Connection(key, new ConnectionClaims { User = user, Expiry = exp, Info = new { name = "Alice" }, Channels = [ch] });
            await using var s = new Stream(new JsonObject { ["token"] = token });
            var msg = await s.NextAsync();
            Assert.True(msg?["connect"] is not null, $"{name}: {msg}");
            Assert.Contains(ch, msg!.ToJsonString(), StringComparison.Ordinal);
            var presence = (await c.PresenceAsync(new PresenceRequest { Channel = ch }, Ct)).Presence.Values.ToList();
            var client = Assert.Single(presence);
            Assert.Equal(user, client.User);
            Assert.Equal("Alice", client.ConnInfo!.Value.GetProperty("name").GetString());
            Assert.Equal(1U, (await c.PresenceStatsAsync(new PresenceStatsRequest { Channel = ch }, Ct)).NumUsers);
            await c.PublishAsync(new PublishRequest { Channel = ch, Data = new { text = "hello" } }, Ct);
            Assert.Contains("hello", (await s.NextAsync())!.ToJsonString(), StringComparison.Ordinal);
            var other = ch + "-2";
            await c.SubscribeAsync(new SubscribeRequest { Channel = other, User = user }, Ct);
            Assert.Contains(other, (await s.NextAsync())!.ToJsonString(), StringComparison.Ordinal);
            await c.UnsubscribeAsync(new UnsubscribeRequest { Channel = other, User = user }, Ct);
            await c.RefreshAsync(new RefreshRequest { User = user, ExpireAt = Now() + 3600 }, Ct);
            await c.DisconnectAsync(new DisconnectRequest { User = user }, Ct);
            while (await s.NextAsync() is not null)
            {
            }
        }
        // A token signed with another key is refused.
        var bad = Token.Connection(Ed25519(9), new ConnectionClaims { User = "u", Expiry = exp });
        await using var refused = new Stream(new JsonObject { ["token"] = bad });
        var answer = await refused.NextAsync();
        Assert.True(answer is null || answer["disconnect"] is not null, answer?.ToJsonString());
    }

    [Fact]
    public async Task ProxyHandlersCalledByCentrifugo()
    {
        await using var c = await Client();
        var user = Unique("proxy-");
        var ch = user + "-news";
        var connects = new List<Proxy.ConnectRequest>();
        var refreshes = new List<Proxy.RefreshRequest>();
        var errors = new List<Exception>();
        var handlers = new Proxy.ProxyHandlers(new Proxy.ProxyHandlerOptions { OnError = (_, e) => errors.Add(e) });
        var connect = handlers.Connect((request, _) =>
        {
            lock (connects)
            {
                connects.Add(request);
            }
            if (request.Data is not { ValueKind: JsonValueKind.Object } data || !data.GetProperty("allow").GetBoolean())
            {
                throw new Proxy.ProxyDisconnectException(4501, "unauthorized");
            }
            return Task.FromResult(new Proxy.ConnectResult
            {
                User = user,
                ExpireAt = Now() + 2,
                Info = new { name = "Alice" },
                Data = new { welcome = true },
                Channels = [ch],
            });
        });
        var refresh = handlers.Refresh((request, _) =>
        {
            lock (refreshes)
            {
                refreshes.Add(request);
            }
            return Task.FromResult(new Proxy.RefreshResult { Expired = true });
        });
        await using var server = await Serve(Port("CSHARPCENT_TEST_PROXY_PORT", 3100), ("/connect", connect), ("/refresh", refresh));

        // Refused: the connection is closed with the disconnect's code.
        await using (var refused = new Stream(new JsonObject { ["data"] = new JsonObject { ["allow"] = false } }))
        {
            var msg = await refused.NextAsync();
            Assert.Equal(4501, msg?["disconnect"]?["code"]?.GetValue<int>());
            var first = connects[0];
            Assert.Equal(("uni_http_stream", "json", "json"), (first.Transport, first.Protocol, first.Encoding));
            Assert.NotEmpty(first.Client);
        }

        // Accepted: the result's data and server-side channel reach the client, and so does a
        // publication into that channel.
        await using var s = new Stream(new JsonObject { ["data"] = new JsonObject { ["allow"] = true } });
        var reply = await s.NextAsync();
        Assert.Equal("""{"welcome":true}""", reply?["connect"]?["data"]?.ToJsonString());
        Assert.NotNull(reply!["connect"]!["subs"]![ch]);
        await c.PublishAsync(new PublishRequest { Channel = ch, Data = new { text = "hello" } }, Ct);
        Assert.Contains("hello", (await s.NextAsync())!.ToJsonString(), StringComparison.Ordinal);

        // It expires: Centrifugo asks the refresh proxy, which ends it.
        while (await s.NextAsync() is not null)
        {
        }
        var refreshed = refreshes[0];
        Assert.Equal(user, refreshed.User);
        Assert.Equal(reply["connect"]!["client"]!.GetValue<string>(), refreshed.Client);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ProUserStatusAndControl()
    {
        await using var c = await Pro();
        var user = Unique();
        await c.UpdateUserStatusAsync(new UpdateUserStatusRequest { Users = [user], State = "away" }, Ct);
        var status = (await c.GetUserStatusAsync(new GetUserStatusRequest { Users = [user] }, Ct)).Statuses[0];
        Assert.Equal((user, "away"), (status.User, status.State));
        Assert.True(status.Active > 0);
        await c.DeleteUserStatusAsync(new DeleteUserStatusRequest { Users = [user] }, Ct);
        Assert.Equal(0, (await c.GetUserStatusAsync(new GetUserStatusRequest { Users = [user] }, Ct)).Statuses[0].Active);

        var expireAt = Now() + 60;
        await c.BlockUserAsync(new BlockUserRequest { User = user, ExpireAt = expireAt }, Ct);
        await c.UnblockUserAsync(new UnblockUserRequest { User = user }, Ct);
        await c.RevokeTokenAsync(new RevokeTokenRequest { Uid = user + "-jti", ExpireAt = expireAt }, Ct);
        await c.InvalidateUserTokensAsync(new InvalidateUserTokensRequest { User = user, IssuedBefore = Now(), ExpireAt = expireAt }, Ct);
        await c.ConnectionsAsync(new ConnectionsRequest { User = user }, Ct);
    }

    /// <summary>A browser <c>PushSubscription</c>, as webpush devices are registered with.</summary>
    private static string WebpushToken()
    {
        using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = ec.ExportParameters(false).Q;
        byte[] point = [4, .. q.X!, .. q.Y!];
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new JsonObject
        {
            ["endpoint"] = $"https://fcm.googleapis.com/fcm/send/{B64(point[..8])}",
            ["keys"] = new JsonObject { ["p256dh"] = B64(point), ["auth"] = B64(new byte[16]) },
        }.ToJsonString();
    }

    [Fact]
    public async Task ProDevicesAndPush()
    {
        await using var c = await Pro();
        var user = Unique();
        var reg = await c.DeviceRegisterAsync(new DeviceRegisterRequest
        {
            Provider = "webpush",
            Token = WebpushToken(),
            Platform = "web",
            User = user,
            Meta = new Dictionary<string, string> { ["app"] = "csharpcent" },
            Topics = ["news"],
        }, Ct);
        Assert.NotEmpty(reg.Id);
        DeviceFilter ByUser() => new() { Users = [user] };
        var list = await c.DeviceListAsync(new DeviceListRequest { Filter = ByUser(), IncludeMeta = true, IncludeTopics = true, IncludeTotalCount = true }, Ct);
        Assert.Equal(1, list.TotalCount);
        Assert.Equal(reg.Id, list.Items[0].Id);
        Assert.Equal("csharpcent", list.Items[0].Meta["app"]);
        Assert.Contains("news", list.Items[0].Topics);
        await c.DeviceTopicUpdateAsync(new DeviceTopicUpdateRequest { DeviceId = reg.Id, Op = "add", Topics = ["sport"] }, Ct);
        var topics = await c.DeviceTopicListAsync(new DeviceTopicListRequest { Filter = new DeviceTopicFilter { DeviceIds = [reg.Id] }, IncludeTotalCount = true }, Ct);
        Assert.Equal(2, topics.TotalCount);
        await c.DeviceUpdateAsync(new DeviceUpdateRequest { Ids = [reg.Id], MetaUpdate = new DeviceMetaUpdate { Meta = new Dictionary<string, string> { ["app"] = "updated" } } }, Ct);
        list = await c.DeviceListAsync(new DeviceListRequest { Filter = ByUser(), IncludeMeta = true }, Ct);
        Assert.Equal("updated", list.Items[0].Meta["app"]);
        await c.UserTopicUpdateAsync(new UserTopicUpdateRequest { User = user, Op = "add", Topics = ["alerts"] }, Ct);
        var userTopics = await c.UserTopicListAsync(new UserTopicListRequest { Filter = new UserTopicFilter { Users = [user] } }, Ct);
        Assert.Equal(["alerts"], userTopics.Items.Select(i => i.Topic));
        var push = await c.SendPushNotificationAsync(new SendPushNotificationRequest
        {
            Recipient = new PushRecipient { Filter = ByUser() },
            Notification = new PushNotification { Webpush = new WebPushPushNotification { Payload = new { title = "hi" } } },
            SendAt = Now() + 3600,
        }, Ct);
        Assert.NotEmpty(push.Uid);
        await c.CancelPushAsync(new CancelPushRequest { Uid = push.Uid }, Ct);
        // Push status needs push analytics in ClickHouse: without it, the method is not available.
        try
        {
            await c.UpdatePushStatusAsync(new UpdatePushStatusRequest { AnalyticsUid = push.Uid, Status = "delivered", DeviceId = reg.Id }, Ct);
        }
        catch (CentApiException e)
        {
            Assert.Equal(ErrorCodes.NotAvailable, e.Code);
        }
        await c.DeviceRemoveAsync(new DeviceRemoveRequest { Ids = [reg.Id] }, Ct);
        Assert.Empty((await c.DeviceListAsync(new DeviceListRequest { Filter = ByUser() }, Ct)).Items);
    }

    [Fact]
    public async Task ProMethodsInABatch()
    {
        await using var c = await Pro();
        var user = Unique();
        var batch = c.NewBatch();
        batch.UpdateUserStatus(new UpdateUserStatusRequest { Users = [user], State = "busy" });
        var status = batch.GetUserStatus(new GetUserStatusRequest { Users = [user] });
        var reg = batch.DeviceRegister(new DeviceRegisterRequest { Provider = "webpush", Token = WebpushToken(), Platform = "web", User = user });
        var conns = batch.Connections(new ConnectionsRequest { User = user });
        await batch.SendAsync(Ct);
        Assert.Equal("busy", status.Result.Statuses[0].State);
        Assert.Empty(conns.Result.Connections);
        await c.DeviceRemoveAsync(new DeviceRemoveRequest { Ids = [reg.Result.Id] }, Ct);
    }

    [Fact]
    public async Task ProApiTokens()
    {
        await using var _ = await Pro();
        // The same key on every run, from a fixed seed: Centrifugo caches keys by ID, so a new
        // key under a known ID would be refused. The other SDKs' tests use it too.
        var key = Ed25519(1, "gocent-api-test");
        await using var server = await Serve(Port("CSHARPCENT_TEST_API_JWKS_PORT", 3101), ("/api_jwks", Jwks.Endpoint([key])));

        async Task Publish(Func<CancellationToken, ValueTask<string>> bearer)
        {
            await using var client = new CentClient(new CentClientOptions { ApiEndpoint = Address, BearerTokenFunc = bearer });
            await client.PublishAsync(new PublishRequest { Channel = Unique(), Data = new { } }, Ct);
        }

        var tokens = new ApiTokenSource(key, new ApiTokenClaims { Audience = "centrifugo-api", Scopes = ["centrifugo:api"] });
        await Publish(tokens.GetTokenAsync);

        var expires = Expiry.After(TimeSpan.FromMinutes(1));
        string[] refused =
        [
            Token.Api(key, new ApiTokenClaims { Audience = "centrifugo-api", Expiry = expires }),
            Token.Api(key, new ApiTokenClaims { Scopes = ["centrifugo:api"], Expiry = expires }),
            Token.Connection(key, new ConnectionClaims { User = "42", Expiry = expires }),
            Token.Api(Ed25519(2, "gocent-unpublished"), new ApiTokenClaims { Audience = "centrifugo-api", Expiry = expires }),
        ];
        foreach (var token in refused)
        {
            await Assert.ThrowsAsync<CentUnauthorizedException>(() => Publish(_ => new(token)));
        }
    }

    [Fact]
    public async Task ProCommandsConsumedFromARedisStream()
    {
        await using var c = await Pro();
        var ch = Unique();
        var apart = Command.Publish(new PublishRequest { Channel = ch, Data = new { n = 1 } });
        await Xadd("csharpcent_commands", "method", apart.Method, "payload", apart.Payload.GetRawText());
        var whole = Command.Publish(new PublishRequest { Channel = ch, Data = new { n = 2 } });
        await Xadd("csharpcent_commands", "payload", whole.ToJson());
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var pubs = (await c.HistoryAsync(new HistoryRequest { Channel = ch, Limit = 10 }, Ct)).Publications;
            if (pubs.Count == 2)
            {
                Assert.Equal([1, 2], pubs.Select(p => p.Data!.Value.GetProperty("n").GetInt32()));
                return;
            }
            Assert.True(DateTime.UtcNow < deadline, $"{pubs.Count} of 2 commands consumed after 15s");
            await Task.Delay(100, Ct);
        }
    }

    /// <summary><c>XADD stream * fields...</c>, over RESP.</summary>
    private static async Task Xadd(string stream, params string[] fields)
    {
        var redis = (Environment.GetEnvironmentVariable("CSHARPCENT_TEST_REDIS") ?? "localhost:46379").Split(':');
        string[] args = ["XADD", stream, "*", .. fields];
        var command = new StringBuilder($"*{args.Length}\r\n");
        foreach (var a in args)
        {
            command.Append('$').Append(Encoding.UTF8.GetByteCount(a)).Append("\r\n").Append(a).Append("\r\n");
        }
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(redis[0], int.Parse(redis[1], System.Globalization.CultureInfo.InvariantCulture), Ct);
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.UTF8.GetBytes(command.ToString()), Ct);
        var reply = new byte[64];
        var n = await s.ReadAsync(reply, Ct);
        Assert.True(reply[0] == '$', Encoding.UTF8.GetString(reply, 0, n));
    }
}
