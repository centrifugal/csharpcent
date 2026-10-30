using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Centrifugal.Cent.Tokens;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Proxy = Centrifugal.Cent.Proxy;

namespace Centrifugal.Cent.Tests;

/// <summary>The README's samples, compiled and run against a fake: the docs never drift from the API.</summary>
public sealed class ReadmeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Message(string Text);

    // Publishes succeed; a broadcast fails in the channels starting with "missing:".
    private sealed class FakeCentrifugo : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var reply = """{"result":{"offset":1,"epoch":"e"}}""";
            if (request.RequestUri!.AbsolutePath.EndsWith("/broadcast", StringComparison.Ordinal))
            {
                // One response per requested channel, in order.
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                var responses = body["channels"]!.AsArray().Select(c => c!.GetValue<string>().StartsWith("missing:", StringComparison.Ordinal)
                    ? """{"error":{"code":102,"message":"unknown channel"}}"""
                    : """{"result":{"offset":1}}""");
                reply = $$$"""{"result":{"responses":[{{{string.Join(",", responses)}}}]}}""";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply) };
        }
    }

    [Fact]
    public async Task TestingYourCode()
    {
        var logger = NullLogger.Instance;
        string[] channels = ["a", "missing:b", "c"];
        var data = new { text = "hi" };
        await using var client = new CentClient(new CentClientOptions
        {
            ApiEndpoint = "http://centrifugo.test/api",
            HttpClient = new HttpClient(new FakeCentrifugo()),
        });
        var result = await client.PublishAsync(new PublishRequest { Channel = "news", Data = new Message("hello") }, Ct);
        Assert.Equal(1UL, result.Offset);
        try
        {
            await client.BroadcastAsync(new BroadcastRequest { Channels = channels, Data = data }, Ct);
            Assert.Fail("partial");
        }
        catch (BroadcastFailedChannelsException e)
        {
#pragma warning disable CA1848, CA1873 // The README's logging, as applications write it.
            logger.LogWarning("broadcast failed in {Channels}", e.Failed.Select(c => c.Channel));
#pragma warning restore CA1848, CA1873
            Assert.Equal(["missing:b"], e.Failed.Select(c => c.Channel));
            Assert.Equal("missing:b", e.Result.Channels[1].Channel);
        }
    }

    [Fact]
    public async Task Retries()
    {
        await using var client = new CentClient(new CentClientOptions { ApiEndpoint = "http://centrifugo.test/api", HttpClient = new HttpClient(new FakeCentrifugo()) });
        var order = new { Id = 42 };
        var delay = Retry.ExponentialBackoff(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
        var request = new PublishRequest { Channel = "orders", Data = order, IdempotencyKey = $"order-{order.Id}-paid" };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await client.PublishAsync(request, Ct);
                break;
            }
            catch (CentException e) when (attempt < 3 && Retry.IsRetryable(e))
            {
                await Task.Delay(delay(attempt), Ct);
            }
        }
        var options = new BroadcastRetryOptions
        {
            MaxAttempts = 3,
            OnAttemptError = attempt => attempt.Attempt > 5 ? TimeSpan.Zero : null,
        };
        Assert.Equal(3, options.MaxAttempts);
    }

    [Fact]
    public void BatchContextLinksTheCallersSpans()
    {
        using var source = new ActivitySource("test");
        var options = new CentClientOptions
        {
            BatchContext = callers =>
            {
                var links = new List<ActivityLink>();
                foreach (var context in callers)
                {
                    ExecutionContext.Run(context, _ =>
                    {
                        if (Activity.Current is { } a)
                        {
                            links.Add(new ActivityLink(a.Context));
                        }
                    }, null);
                }
                return source.StartActivity("centrifugo batch", ActivityKind.Client, default(ActivityContext), links: links);
            },
        };
        Assert.NotNull(options.BatchContext);
    }

    [Fact]
    public async Task ProxyAndTokens()
    {
        var proxy = new Proxy.ProxyHandlers(new Proxy.ProxyHandlerOptions { OnError = (_, _) => { } });
        var subscribe = proxy.Subscribe(async (request, context) =>
        {
            await Task.Yield();
            if (request.Channel != "news")
            {
                throw new Proxy.ProxyErrorException(403, "permission denied");
            }
            return new Proxy.SubscribeResult();
        });
        var answer = await subscribe.HandleAsync(new HandlerRequest("POST", """{"channel":"secret"}"""u8.ToArray()), Ct);
        Assert.Contains("permission denied", System.Text.Encoding.UTF8.GetString(answer.Body.Span), StringComparison.Ordinal);

        var key = SigningKey.Hmac("secret");
        var token = Token.Connection(key, new ConnectionClaims
        {
            User = "42",
            Expiry = Expiry.After(TimeSpan.FromHours(1)),
            Info = new { name = "Alice" },
        });
        var end = DateTimeOffset.UtcNow.AddHours(1);
        var subscription = Token.Subscription(key, new SubscriptionClaims { User = "42", Channel = "news", Expiry = Expiry.At(end) });
        Assert.NotEqual(token, subscription);

        var seed = new byte[32];
        var privateKey = new Ed25519PrivateKeyParameters(seed);
        var ed = SigningKey.Ed25519(privateKey.GeneratePublicKey().GetEncoded(), data =>
        {
            var signer = new Ed25519Signer();
            signer.Init(true, privateKey);
            signer.BlockUpdate(data, 0, data.Length);
            return signer.GenerateSignature();
        }, keyId: "2026-10");
        var tokens = new ApiTokenSource(ed, new ApiTokenClaims { Audience = "centrifugo-api", Subject = "billing" });
        var options = new CentClientOptions { ApiEndpoint = "http://localhost:8000/api", BearerTokenFunc = tokens.GetTokenAsync };
        Assert.NotNull(options.BearerTokenFunc);

        var signature = SharedPoll.Sign("secret", "42", "poll:prices", ["AAPL"], Expiry.After(TimeSpan.FromMinutes(5)));
        Assert.NotEmpty(signature);
        var command = Command.Publish(new PublishRequest { Channel = "news", Data = new { text = "hi" } });
        Assert.StartsWith("{\"method\":\"publish\"", command.ToJson(), StringComparison.Ordinal);
    }
}
