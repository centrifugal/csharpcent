# csharpcent

[![NuGet](https://img.shields.io/nuget/v/Centrifugal.Cent.svg)](https://www.nuget.org/packages/Centrifugal.Cent/)
[![CI](https://github.com/centrifugal/csharpcent/actions/workflows/ci.yml/badge.svg)](https://github.com/centrifugal/csharpcent/actions/workflows/ci.yml)

.NET SDK for the [HTTP server API](https://centrifugal.dev/docs/server/server_api)
of [Centrifugo](https://github.com/centrifugal/centrifugo): every API method,
including those of Centrifugo PRO; broadcasts, batches and automatic batching;
connection, subscription and server API tokens with JWKS; the proxy handlers and
the endpoints client SDKs call. For a real-time client over WebSocket, see
[centrifuge-csharp](https://github.com/centrifugal/centrifuge-csharp).

```sh
dotnet add package Centrifugal.Cent
dotnet add package Centrifugal.Cent.AspNetCore   # serving handlers with ASP.NET Core, AddCentClient
```

| | Version |
|---|---|
| .NET | 8 or newer; trimming and native AOT supported |
| Centrifugo | v6, any release |
| Map and shared poll methods | Centrifugo v6.8.0 or newer |
| `CentApiException.Temporary`, code 111 (too many requests) | Centrifugo v6.10.0 or newer |
| Ed25519 tokens verified without JWKS, by `client.token.ed25519_public_key` | Centrifugo v6.10.2 or newer |
| Methods marked "Centrifugo PRO only" | Centrifugo PRO v6 |

| Package | What | Depends on |
|---|---|---|
| `Centrifugal.Cent` | The client, tokens, JWKS, shared poll signatures, commands, and the HTTP handlers' framework-free core | nothing but .NET |
| `Centrifugal.Cent.AspNetCore` | `MapCentrifugo` to serve the handlers, `AddCentClient` for dependency injection | ASP.NET Core |

csharpcent implements the [Centrifugo server SDK
specification](https://github.com/centrifugal/gocent/blob/master/spec.md),
version 0.2, whose reference implementation is
[gocent](https://github.com/centrifugal/gocent).

## Example

```csharp
using Centrifugal.Cent;

await using var client = new CentClient(new CentClientOptions
{
    ApiEndpoint = "http://localhost:8000/api",
    ApiKey = apiKey,
});
var result = await client.PublishAsync("news", new { text = "hello" });
Console.WriteLine(result.Offset);
```

Create one client per process and share it: it is safe for concurrent use and
keeps its connections alive. Every API method is a method of the client taking
its request - `PublishAsync`, `PresenceAsync`, `HistoryAsync`... - with all the
method's fields; a request's fields the protos mark required are `required`
members, so the compiler says when one is missing. Publish and broadcast also
have short forms taking the channel and data:

```csharp
await client.PublishAsync(new PublishRequest { Channel = "news", Data = data, IdempotencyKey = key, SkipHistory = true });
await client.BroadcastAsync(["news", "sport"], data);
```

In ASP.NET Core, register the client with `AddCentClient`, configured from
`appsettings.json` - it is closed when the application stops:

```csharp
builder.Services.AddCentClient(builder.Configuration.GetSection("Centrifugo"));
```

```json
{
  "Centrifugo": {
    "ApiEndpoint": "http://localhost:8000/api",
    "ApiKey": "...",
    "RequestTimeout": "00:00:05",
    "AutoBatch": { "MaxInFlight": 32 }
  }
}
```

What configuration cannot hold - a bearer token function, say - goes in a
second argument: `AddCentClient(section, (services, options) => ...)`. The
client's `HttpClient` comes from `IHttpClientFactory`, with the SDK's default
handler; `AddCentClient` returns its builder, for handlers of your own:
`.AddHttpMessageHandler<MyHandler>()`.

## Payloads

A payload - `Data`, `Info`, `Meta` and the like - is any JSON value:

- an object of the application's, or an anonymous type, encoded as ASP.NET Core
  and SignalR encode what goes to browsers - camelCase names (`JsonSerializerDefaults.Web`);
- a `JsonElement` or `JsonNode`;
- a string, number or boolean - a string is a JSON string, never JSON text;
- `RawJson`: JSON text sent as the same value, number text kept - JSON the
  application already has, or encoded with options of its own.

```csharp
new PublishRequest { Channel = "news", Data = new Message("hello") };
new PublishRequest { Channel = "news", Data = RawJson.Parse(jsonFromDatabase) };
new PublishRequest { Channel = "news", Data = RawJson.Serialize(message, MyJsonContext.Default.Message) }; // native AOT
```

Replies give payloads as `JsonElement?`: `null` when absent, an element of kind
`Null` for JSON `null`. A payload which came back goes out again as the same JSON
value - republishing a publication from history, or answering a proxy with the
client's data, changes nothing. `Decode<T>()` reads one into an application's
type, as the SDK encodes it:

```csharp
var history = await client.HistoryAsync(new HistoryRequest { Channel = "news", Limit = 10 });
var message = history.Publications[0].Data?.Decode<Message>();
```

Binary data goes base64-encoded in the `B64` fields (`B64Data`, `B64Info`):
bytes given as a JSON payload are refused, as are NaN, infinities and invalid
JSON - before anything is sent. JSON `null` as a payload is `RawJson.Null`.

JavaScript - where most Centrifugo payloads end up - reads JSON numbers as
doubles, which hold integers exactly only up to 2^53. Send integers which may
pass it - 64-bit IDs - as strings: `new { id = id.ToString() }`, or
`[JsonNumberHandling(JsonNumberHandling.WriteAsString)]` on the property.

## Timeouts

Every call has one deadline: `RequestTimeout` (10 seconds by default), or the
timeout of a client from `WithTimeout`, sharing everything else with it. It
bounds the whole call - connecting, sending, reading the reply, a reply trickling
in included - and its end throws `CentTimeoutException`. A call's
`CancellationToken` cancels it sooner, throwing `OperationCanceledException`.

```csharp
var history = await client.WithTimeout(TimeSpan.FromSeconds(30)).HistoryAsync(request, ct);
```

## Errors

A call returns its result only when it did everything asked, and throws a
`CentException` otherwise:

| Exception | When |
|---|---|
| `CentApiException` | Centrifugo refused the request or failed to carry it out: match `Code` against `ErrorCodes`, never the message |
| `CentHttpException` | The request did not reach the API method: `StatusCode`, the start of `Body`, `Headers`. `CentUnauthorizedException` for 401 |
| `CentTimeoutException` | The call's deadline ended |
| `CentNetworkException` | The connection was refused, reset or not made |
| `CentDecodeException` | The reply is not Centrifugo's - usually a wrong endpoint |
| `CentInvalidRequestException` | Not sent: the request cannot be, as it is |
| `CentInvalidConfigException` | The client was not created: the message names the option and the fix |
| `CentClosedException` | A call after `CloseAsync` |
| `BroadcastFailedChannelsException`, `BatchFailedCommandsException` | Some channels or commands failed, the others succeeded: below |

```csharp
try
{
    await client.PublishAsync(request, ct);
}
catch (CentApiException e) when (e.Code == ErrorCodes.UnknownChannel)
{
    // the channel's namespace does not exist
}
```

Error messages carry hints: a 404 says whether the endpoint is wrong or the
server is Centrifugo OSS answering a PRO method; a redirect says to check http
or https. With `http_api.error_mode: transport`, Centrifugo's errors are the same
`CentApiException`s.

## Retries

`Retry.IsRetryable(e)` tells whether another attempt, with time left, may
succeed: a temporary Centrifugo error, a network failure, a timeout, an HTTP 429
or 5xx. A retryable error does not mean nothing was done: a publication may have
been published before its reply was lost. So retry a publication only with the
same `IdempotencyKey`:

```csharp
var delay = Retry.ExponentialBackoff(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
var request = new PublishRequest { Channel = "orders", Data = order, IdempotencyKey = $"order-{order.Id}-paid" };
for (var attempt = 1; ; attempt++)
{
    try
    {
        await client.PublishAsync(request, ct);
        break;
    }
    catch (CentException e) when (attempt < 3 && Retry.IsRetryable(e))
    {
        await Task.Delay(delay(attempt), ct);
    }
}
```

Idempotency keys:

- each publication or broadcast gets its own key, reused only to retry that same
  one; a broadcast needs one key, not one per channel;
- Centrifugo remembers a key per channel, five minutes by default: a second
  publication under the same key is not published, so a key derived from an
  entity must also name the event - `order-42-paid`, not `order-42`;
- the memory, Redis and PostgreSQL brokers keep keys; the NATS broker ignores
  them, and a retry publishes again.

## Broadcast and batch

A broadcast publishes into many channels, with every channel's outcome in the
result, in order. When some channels fail it throws
`BroadcastFailedChannelsException` holding every outcome:

```csharp
try
{
    await client.BroadcastAsync(new BroadcastRequest { Channels = channels, Data = data }, ct);
}
catch (BroadcastFailedChannelsException e)
{
    logger.LogWarning("broadcast failed in {Channels}", e.Failed.Select(c => c.Channel));
}
```

`e.Result.Channels` holds every channel in the request's order, so the outcome
of the n-th channel you asked for is `e.Result.Channels[n]` - the way back from a
channel to the user or entity it was made for.

Never retry a whole broadcast: the other channels got the publication. Retry
only `e.Result.RetryableChannels()`, with the same idempotency key - or use
`BroadcastWithRetryAsync`, which does that, with one key on every attempt (the
request's, or one it makes and returns in the result):

```csharp
var result = await client.BroadcastWithRetryAsync(request, new BroadcastRetryOptions
{
    MaxAttempts = 3,
    OnAttemptError = attempt =>
    {
        logger.LogWarning(attempt.Error, "attempt {N}, retrying {Channels}", attempt.Attempt, attempt.RetryChannels);
        return null; // the default backoff; a TimeSpan to wait; throw to stop
    },
}, ct);
```

A batch sends commands in one request. A method per API method adds one and
returns its `Pending<T>`, holding its outcome once the batch is sent:

```csharp
var batch = client.NewBatch();
var publish = batch.Publish(new PublishRequest { Channel = "news", Data = data });
var presence = batch.Presence(new PresenceRequest { Channel = "chat" });
await batch.SendAsync(ct); // BatchFailedCommandsException when some failed
Console.WriteLine(publish.Result.Offset);
```

A batch is sent once. By default Centrifugo runs its commands in order;
`NewBatch(parallel: true)` runs them concurrently. Centrifugo PRO sends a
sequential batch's publications to its broker together: each channel's take
effect in order, while publications into different channels may not, unless the
namespace sets `publication_grouping_disabled`. A PRO method in a batch sent to
Centrifugo OSS fails with code 104 (not found), with a hint.

## Automatic batching

For processes making many calls at once - a web application publishing on every
request, a worker draining a queue - `AutoBatch` sends concurrent calls together,
cutting the requests Centrifugo handles under load:

```csharp
var client = new CentClient(new CentClientOptions
{
    ApiEndpoint = endpoint,
    ApiKey = apiKey,
    AutoBatch = new AutoBatchOptions { MaxInFlight = 32 },
});
```

Below `MaxInFlight` requests in flight every call is sent at once, alone: nothing
ever waits for a batch to fill. At the cap, calls queue, and when a request
finishes those queued go out as one batch (at most `MaxBatchSize`, 1000). Every
call gets exactly its own result or error, and calls made one after another keep
their order. Choose `MaxInFlight` at about half the calls the process makes at
once: above that it never batches. A proxy in front of Centrifugo may limit a
request's size where Centrifugo does not - nginx: 1 MB by default - failing a
batch with a 413: lower `MaxBatchSize`, or raise the proxy's limit.

## Authentication

`ApiKey` sends `X-API-Key`. Centrifugo PRO also takes [API
tokens](https://centrifugal.dev/docs/pro/http_api_jwks): JWTs signed with keys
Centrifugo reads from a JWKS endpoint. `ApiTokenSource` signs them, reusing each
for half its lifetime:

```csharp
var tokens = new ApiTokenSource(apiKey, new ApiTokenClaims { Audience = "centrifugo-api", Subject = "billing" });
var client = new CentClient(new CentClientOptions { ApiEndpoint = endpoint, BearerTokenFunc = tokens.GetTokenAsync });
```

Centrifugo PRO accepts a token signed with any key of its JWKS endpoint, and
checks audience, issuer and scope only when configured to. So sign API tokens
with keys of their own, publish them at a JWKS endpoint other than the client
tokens' one, and configure Centrifugo PRO to require an audience or scope only
API tokens carry: otherwise any user can call the server API with their
connection token.

## HTTP client, tracing and metrics

The default HTTP client keeps up to 256 connections to Centrifugo alive, all of
them while idle, closes idle ones after 50 seconds, drains reply bodies so
connections are reused, never follows redirects, and uses no proxy. It sets no
timeouts of its own: each call's deadline bounds it. `AddCentClient` makes it
with `IHttpClientFactory`, under the name `Centrifugal.Cent`, and returns its
builder; elsewhere, give `HttpClient` to change any of that. The SDK never
changes a client it is given: credentials and headers go on each request.

Mind a resilience handler which retries requests: it would publish again a
publication whose reply was lost - twice on the NATS broker, which ignores
idempotency keys - and its timeouts would cut the SDK's deadline short. Disable
its retries of `POST`, and retry with `Retry` and idempotency keys instead:

```csharp
builder.Services.AddCentClient(builder.Configuration.GetSection("Centrifugo"))
    .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());
```

Tracing and metrics come from .NET's HTTP instrumentation: OpenTelemetry's
`AddHttpClientInstrumentation()` traces every request, and `System.Net.Http`'s
metrics count them. `OnRequest` reports each request - method, commands, failed
ones, status, duration, error - for logs and metrics of your own. It is called
synchronously and must not throw: what it throws is the call's error.

An automatic batch runs in an empty execution context, never a caller's. To give
it a tracing span linking its callers', use `BatchContext`:

```csharp
options.BatchContext = callers =>
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
};
```

## Testing your code

The client's methods are virtual, so code using it is tested with a mocking
library, or a class of your own deriving from `CentClient`:

```csharp
var client = Substitute.For<CentClient>(); // NSubstitute; new Mock<CentClient>() in Moq
client.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
    .Returns(new PublishResult { Offset = 1 });
client.BroadcastAsync(Arg.Any<BroadcastRequest>(), Arg.Any<CancellationToken>())
    .Returns<BroadcastResult>(_ => throw new BroadcastFailedChannelsException(new BroadcastResult(
        [new ChannelResult("a", new PublishResult()), new ChannelResult("b", new CentApiException(102, "unknown channel"))])));
```

The short forms call the request forms, so mocking `PublishAsync(PublishRequest, ...)`
answers `PublishAsync(channel, data)` too. Results, errors and the
partial-failure exceptions are all made with `new`:

```csharp
new CentApiException(ErrorCodes.TooManyRequests, "too many requests", temporary: true);
new CentNetworkException("publish", new HttpRequestException("connection reset"));
new CentTimeoutException("publish", TimeSpan.FromSeconds(10));
```

`BroadcastWithRetryAsync` sends its attempts itself, not through the virtual
`BroadcastAsync`: mock it directly, or test its retries against a fake
`HttpMessageHandler`, below. Batches are mocked the same
way, their commands returning `Pending.FromResult(...)` or `Pending.FromException<T>(...)`:

```csharp
var batch = Substitute.For<CentBatch>();
batch.Publish(Arg.Any<PublishRequest>()).Returns(Pending.FromResult(new PublishResult { Offset = 1 }));
client.NewBatch(Arg.Any<bool>()).Returns(batch);
```

To test against Centrifugo's replies instead - batches included - give the
client an `HttpClient` whose handler answers as Centrifugo does: a method's reply is `{"result": {...}}` or
`{"error": {"code": ..., "message": ...}}`; a broadcast's,
`{"result": {"responses": [...]}}` with one response per channel; a batch's,
`{"replies": [...]}` with one reply per command, `{"<method>": {...}}` or
`{"error": {...}}`.

```csharp
// Publishes succeed; a broadcast fails in the channels starting with "missing:".
sealed class FakeCentrifugo : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var reply = """{"result":{"offset":1,"epoch":"e"}}""";
        if (request.RequestUri!.AbsolutePath.EndsWith("/broadcast"))
        {
            // One response per requested channel, in order.
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var responses = body["channels"]!.AsArray().Select(c => c!.GetValue<string>().StartsWith("missing:")
                ? """{"error":{"code":102,"message":"unknown channel"}}"""
                : """{"result":{"offset":1}}""");
            reply = $$$"""{"result":{"responses":[{{{string.Join(",", responses)}}}]}}""";
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply) };
    }
}

var client = new CentClient(new CentClientOptions
{
    ApiEndpoint = "http://centrifugo.test/api",
    HttpClient = new HttpClient(new FakeCentrifugo()),
});
```

## Shutdown

`CloseAsync` refuses new calls with `CentClosedException`, waits for those in
progress - automatic batches whose callers left included - and then closes the
idle connections. Its `CancellationToken` ends the wait, leaving the calls to go
on; it may be called again. `DisposeAsync` closes the client too, and a client of
`AddCentClient` is closed when the application stops.

## HTTP handlers

The proxy handlers, the endpoints for client SDKs and the JWKS endpoint are
`CentHandler`s, free of any framework: `HandleAsync` takes a `HandlerRequest`
and returns a `HandlerResponse`. `Centrifugal.Cent.AspNetCore` serves them:

```csharp
app.MapCentrifugo("/centrifugo/connect", proxy.Connect(Connect));
```

`MapCentrifugo` returns the endpoint's builder, for its conventions:
`.RequireHost(...)`, authorization. A handler's function reaches ASP.NET Core's
`HttpContext` - the user, cookies, services - with
`context.Request.GetHttpContext()`. A request a handler fails is logged through
the application's logging, under the category `Centrifugal.Cent`: a malformed
one as a warning, a failure of its function as an error, with the exception.
Other frameworks call `HandleAsync`; a response's `Error` says why it failed.

## Proxy

`ProxyHandlers` makes a handler per type of Centrifugo's [HTTP
proxy](https://centrifugal.dev/docs/server/proxy), from a function taking the
decoded request:

```csharp
using Proxy = Centrifugal.Cent.Proxy;

var proxy = new Proxy.ProxyHandlers();
app.MapCentrifugo("/centrifugo/connect", proxy.Connect(async (request, context) =>
{
    var http = context.Request.GetHttpContext();
    var user = await sessions.UserAsync(http.Request.Cookies["session"], context.CancellationToken)
        // 4501: closed for good - a code in [4500, 4999] tells the client not to reconnect.
        ?? throw new Proxy.ProxyDisconnectException(4501, "unauthorized");
    return new Proxy.ConnectResult { User = user.Id };
}));
var subscribe = proxy.Subscribe(async (request, context) =>
{
    if (!await permissions.CanReadAsync(request.User, request.Channel, context.CancellationToken))
    {
        throw new Proxy.ProxyErrorException(403, "permission denied");
    }
    return new Proxy.SubscribeResult();
});
```

The function returns a result to accept the request; throws
`ProxyErrorException` to reject it with an error the client receives - a code in
[400, 1999]; or throws `ProxyDisconnectException` to close the client's
connection - a code in [4000, 4999], those in [4500, 4999] telling the client
not to reconnect, and a reason of at most 32 bytes. Refresh and sub refresh
answer only with a result, ending through its `Expired`. Any other exception
fails the request with HTTP status 500, logged - and reported to
`ProxyHandlerOptions.OnError`, when set. Payloads arrive
as `JsonElement?` - in the `B64` fields in binary mode - and go back as they are.

The proxy's types live in `Centrifugal.Cent.Proxy`: some share names with the
API's (`PublishRequest`, `SubscribeResult`), so a file using both refers to them
as `Proxy.PublishRequest`, with `using Proxy = Centrifugal.Cent.Proxy;`.

Keep proxy endpoints reachable by Centrifugo only: whoever can call them can
claim to be any user.

## Tokens

```csharp
using Centrifugal.Cent.Tokens;

var key = SigningKey.Hmac(builder.Configuration["Centrifugo:TokenSecret"]!);
var token = Token.Connection(key, new ConnectionClaims
{
    User = "42",
    Expiry = Expiry.After(TimeSpan.FromHours(1)),
    Info = new { name = "Alice" },
});
var subscription = Token.Subscription(key, new SubscriptionClaims { User = "42", Channel = "news", Expiry = Expiry.At(end) });
```

Keys: `SigningKey.Hmac` (HS256), `Rsa` (RS256), `ECDsa` on P-256, P-384 or P-521
(ES256, ES384, ES512), `FromPem` for RSA and ECDSA private keys, and `Ed25519`
(EdDSA). A token's `Expiry` is required - a time, or `Expiry.Never` chosen
explicitly. `ConnectionExpiry` and `SubscriptionExpiry` set when the connection
or subscription expires, if not at the token's expiry. A connection token's
`Meta` is not visible to other clients, but the user owning the token can decode
it: never put in it what that user must not see.

.NET has no Ed25519, so an Ed25519 key takes the signing function of a library
of your choice, and the SDK depends on none - with BouncyCastle:

```csharp
// dotnet add package BouncyCastle.Cryptography
var privateKey = new Ed25519PrivateKeyParameters(seed);
var key = SigningKey.Ed25519(privateKey.GeneratePublicKey().GetEncoded(), data =>
{
    var signer = new Ed25519Signer();
    signer.Init(true, privateKey);
    signer.BlockUpdate(data, 0, data.Length);
    return signer.GenerateSignature();
}, keyId: "2026-10");
```

`Jwks.Endpoint(keys)` serves public keys for `client.token.jwks_public_endpoint`
and PRO's `http_api.jwks.endpoint`. Rotate keys so: publish a new key next to
the old one; sign with it only once every instance of the application publishes
it - from an activation time set ahead, or in a second deploy; drop the old key
once its tokens have expired. Never give a key ID to another key: Centrifugo
caches keys by ID.

Make the key once, at startup, and use it in the handlers built then:

```csharp
var endpoints = new EndpointHandlers();
app.MapCentrifugo("/centrifugo/connection_token", endpoints.ConnectionToken(context =>
{
    var user = context.Request.GetHttpContext().User.Identity?.Name
        ?? throw new EndpointUnauthorizedException();
    return Task.FromResult(Token.Connection(key, new ConnectionClaims { User = user, Expiry = Expiry.After(TimeSpan.FromMinutes(15)) }));
}));
```

`EndpointHandlers` makes the endpoints the `getToken` and `getSignature`
callbacks of client SDKs call: `ConnectionToken`, `SubscriptionToken` and
`SharedPollSignature`. The function throws `EndpointUnauthorizedException` to
answer 403 - the client stops asking; any other exception answers 500, and the
client retries.

## Shared poll signatures

```csharp
var signature = SharedPoll.Sign(secret, user, "poll:prices", keys, Expiry.After(TimeSpan.FromMinutes(5)));
```

The signature covers exactly `keys`, in order. Behind the signature endpoint,
keys the function leaves out are revoked: the client stops tracking them.

## Commands for async consumers

```csharp
var command = Command.Publish(new PublishRequest { Channel = "news", Data = data });
await producer.ProduceAsync("centrifugo", new Message<string, string> { Key = "news", Value = command.ToJson() });
```

A command is `{"method": ..., "payload": {...}}`, the message Centrifugo's
[async consumers](https://centrifugal.dev/docs/server/consumers) take - Kafka,
NATS JetStream, Redis Streams, a PostgreSQL outbox table, where `command.Payload`
goes into a `jsonb` column. It validates the request as a call would, so a
command Centrifugo could not run fails when written. A consumer keeps commands
in order only within a partition: a channel's commands keep their order only
when every writer derives their partition from the channel the same way.

## Streaming AI output

Publish a model's output into a channel of its own, in a namespace with history
and recovery, from one task - each publish waiting for the previous one - in
chunks of what arrived every 50 ms or so, not a publication per token. See
[Centrifugo for AI apps](https://centrifugal.dev/docs/getting-started/ai_apps),
which also covers sizing history for a whole response.

## More

- The [examples](examples): publishing, an ASP.NET Core backend, native AOT.
- Docs for agents and LLMs: [centrifugal.dev/llms.txt](https://centrifugal.dev/llms.txt),
  an index of the current (v6) documentation.
- Development: see [CONTRIBUTING.md](CONTRIBUTING.md) and [AGENTS.md](AGENTS.md).

MIT license. Centrifugo PRO, which the methods marked "Centrifugo PRO only"
call, is commercial software under its own
[license](https://centrifugal.dev/license).
