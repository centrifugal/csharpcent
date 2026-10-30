# Working on csharpcent

csharpcent - the packages `Centrifugal.Cent` and `Centrifugal.Cent.AspNetCore` -
is the .NET SDK for Centrifugo's HTTP server API, written to the [Centrifugo
server SDK specification](https://github.com/centrifugal/gocent/blob/master/spec.md),
version 0.2, whose reference implementation is gocent. Applications call it on
their hot paths, from many requests at once, so it has to be correct under
concurrency and hard to misuse. Read this before changing code.

## Layout

| Path | What |
|---|---|
| `proto/api.proto`, `proto/proxy.proto` | Copies of Centrifugo's protos, from a released tag (v6.10.2) |
| `tools/Generator/` | The generator: `Proto.cs` parses the protos, `Emit.cs` writes the code |
| `tools/Generator/required_fields.txt` | The fields the protos mark "Required.", made `required` members - the reviewed list the other SDKs share. Generation fails when the protos' list differs |
| `src/Centrifugal.Cent/Generated/` | Generated - never edited: request and result types, the client's, batches' and commands' methods, the JSON contexts, the proxy types and handlers |
| `tests/Centrifugal.Cent.Tests/Generated/` | Generated - never edited: every method reaches its endpoint, batch command and consumer command |
| `src/Centrifugal.Cent/CentClient.cs`, `ClientCore.cs` | The client, its options' validation, the request path, close |
| `src/Centrifugal.Cent/Exceptions.cs` | The exceptions, `ErrorCodes`, `Retry` |
| `src/Centrifugal.Cent/Broadcast.cs`, `Batch.cs`, `AutoBatcher.cs`, `Command.cs` | Broadcasts and their retries, batches, automatic batching, commands |
| `src/Centrifugal.Cent/Json/` | The wire options and converters, payloads, `RawJson` |
| `src/Centrifugal.Cent/Handlers/` | `CentHandler`, the framework-free HTTP handlers' core; proxy and endpoint handlers |
| `src/Centrifugal.Cent/Tokens/`, `SharedPoll.cs` | Keys, tokens, JWKS, API tokens, shared poll signatures |
| `src/Centrifugal.Cent.AspNetCore/` | `MapCentrifugo`, `AddCentClient` |
| `tests/Centrifugal.Cent.Tests/` | Tests against a fake Centrifugo, local servers, ASP.NET Core, and a real Centrifugo (`IntegrationTests.cs`) |
| `testdata/` | The Centrifugo config and the Centrifugo PRO compose file the integration tests use |

## Invariants

These are not preferences. A change that breaks one is wrong even if every test
passes; if you find yourself about to break one, stop and say so instead.

1. **The generated files are never edited.** Change `proto/` or
   `tools/Generator`, then `dotnet run --project tools/Generator`;
   `-- --check` fails when they are stale.
2. **A call throws unless it did everything asked.** A broadcast or batch in
   which some parts failed throws `BroadcastFailedChannelsException` or
   `BatchFailedCommandsException`, holding every part's outcome. A partial
   error is never a `CentApiException` - also with one channel.
3. **Errors follow Centrifugo's model.** A Centrifugo error is a
   `CentApiException`, matched by code alone; an unknown code is still one. The
   transport error mode gives the same error as a 200 reply. A reply which is
   not a JSON object of the reply's shape is a `CentDecodeException`.
4. **One deadline per call.** `RequestTimeout`, or `WithTimeout`'s, bounds the
   whole call - the endpoint and token functions, the connection, the reply
   read - through one `CancellationToken`, so a reply trickling in stops at the
   deadline. Every timeout is a `CentTimeoutException`, an `HttpClient`'s own
   included; a caller's cancellation is an `OperationCanceledException`.
5. **A payload is carried exactly.** A reply's payloads are `JsonElement?`,
   which go back as the same JSON value, number text kept. JSON `null` is an
   element of kind `Null`; C# `null` is no payload.
6. **A caller's `HttpClient` is never changed**: credentials and headers go on
   each request.
7. **Automatic batching never delays a call below `MaxInFlight`**, never gives
   a caller a reply which is not its own, never sends a call whose caller gave
   up while it queued, and `CloseAsync` waits for batches whose callers left.
   Batches run on the thread pool in an empty execution context.
8. **A handler answers with exactly one of result, error and disconnect,** as
   its proxy type can carry them; anything else is a 500 reported to
   `OnError`.
9. **`Centrifugal.Cent` depends on nothing but .NET**, and is trimming and
   native AOT safe: the `examples/Aot` publish in CI fails on any warning.

## Running things

```sh
dotnet format Centrifugal.Cent.slnx --verify-no-changes
dotnet build Centrifugal.Cent.slnx -c Release
dotnet run --project tools/Generator -- --check
dotnet test --project tests/Centrifugal.Cent.Tests
```

The integration tests need a Centrifugo set up as `testdata/` says:

```sh
docker compose -f testdata/centrifugo-pro.compose.yml up -d --wait
CENTRIFUGO_TESTS=1 CSHARPCENT_TEST_PRO=1 dotnet test --project tests/Centrifugal.Cent.Tests --max-parallel-test-modules 1
```

They serve the proxy handlers on port 3100 and an API JWKS on 3101 of the host,
which Centrifugo reaches as `host.docker.internal`. CONTRIBUTING.md lists the
rest of what CI runs.

## Updating to a new Centrifugo API

1. Copy `api.proto` and `proxy.proto` from a released Centrifugo tag into
   `proto/` - always a released tag.
2. Read the diff of `proto/`: a removed field is a breaking change. Keep it as
   a deprecated field until the next breaking release (spec 16).
3. If generation stops on `required_fields.txt`, review the change, then
   `dotnet run --project tools/Generator -- --accept-required`. A new proxy
   method stops it too: decide which answers it takes in `Emit.cs`.
4. The checks, then the integration tests against the new release.
5. Bump the Centrifugo image in `.github/workflows/ci.yml` and
   `testdata/centrifugo-pro.compose.yml`, and the versions table in the README
   when a feature needs the new release.

## Things that look wrong but are deliberate

- **Requests are classes with `required` members**, not constructors: a field
  the protos add is not a breaking change, and the compiler says when a field
  marked "Required." is missing (spec 3.3) - by name, so a reordering of the
  protos cannot swap two arguments.
- **A request's payload is `object?`**, a reply's `JsonElement?`: the spec's C#
  row - native values in, the JSON type out. An application's object is encoded
  with `JsonSerializerDefaults.Web`, as ASP.NET Core and SignalR encode what
  goes to browsers, and `Decode<T>()` reads with the same options. There is no
  option to change them: other rules go through `RawJson.Serialize` (spec 3.4,
  rule 2).
- **Received strings, lists and maps are never null**: their `init` accessors
  turn a `null` read into the empty value, since System.Text.Json passes
  init-only values the way it passes constructor arguments, past any setter
  hook.
- **Zero values are left out of the protos' types only**, by a resolver
  modifier on the generated JSON contexts. The SDK's own types write what they
  say: a shared poll signature's `keys` is `[]` when empty.
- **Strings with unpaired surrogates are refused** rather than sent with U+FFFD
  in their place, which System.Text.Json would do without a word.
- **Ed25519 takes a signing function**: .NET has no Ed25519, and the core
  package takes no dependency for it. The tests use BouncyCastle, as the docs
  show.
- **The proxy's types live in `Centrifugal.Cent.Proxy`**, with names the API's
  share - `PublishRequest`, `SubscribeResult` - as gocent's `proxy` package and
  rustcent's `proxy` module have them: a file using both qualifies them.
  `Error` is `ProxyError` there, since `Error` alone would read as any error.
- **Error codes are `int`**, as .NET's own codes are - an HTTP status included -
  though the protos write them `uint32`: Centrifugo's codes fit, and an `int`
  compares with constants and numbers without a cast. Generated fields keep the
  protos' types.
- **`PublishWithRetryAsync` does not exist**: spec 6 leaves retrying a single
  call to the application - the loop the README shows - as every SDK does.
- **Proxy errors and disconnects are exceptions** - `ProxyErrorException`,
  `ProxyDisconnectException` - which the handler finds also wrapped: the
  function's result type stays the proxy's result.
- **`CentClient` and `CentBatch` are not sealed, and their methods are virtual**, with a protected
  constructor for mocks, as the Azure SDK guidelines have it: .NET applications
  test code using a client with NSubstitute or Moq. Adding a virtual method
  breaks no one, as adding an interface member would. The short forms -
  `PublishAsync(channel, data)` - are not virtual: they call the request forms,
  so mocking those answers both.
- **No synchronous overloads of the proxy handlers** (`Func<TRequest, HandlerContext, TResult>`):
  a lambda which only throws - `(r, c) => throw new ProxyErrorException(...)` -
  converts to both, and the call no longer compiles. A synchronous function
  returns `Task.FromResult(...)`.
- **`AddCentClient`'s `HttpClient` lives as long as the application**: the client
  is a singleton holding one, so its handler's lifetime is infinite, and it is
  the SDK's default handler - connections kept alive, no redirects.
- **`AddCentClient` reads its configuration keys one by one**, not through the
  configuration binder: the binder is reflection, which trimming and AOT
  forbid, and its source generator cannot bind the options' functions,
  `HttpClient` and `TimeProvider`. A malformed value fails naming its key.
- **Per-call timeouts by client view**: `client.WithTimeout(t).PublishAsync(...)`.
  .NET has no deadline object a `CancellationToken` could carry, as Python has
  none; a view keeps every method's signature `(request, cancellationToken)`.
- **`OnRequest` is called outside a `finally`**: a hook throwing from a
  `finally` of an async method escaped as an unhandled exception under xunit's
  synchronization context. It is reported once on success and once on failure.
- **No blocking client**: .NET code is asynchronous end to end, and blocking
  on a task is the application's choice to make.
- **No `rpc`**: it calls server API extensions, which Centrifugo does not
  provide.
- **An oversized handler body is a 400**, as in gocent, not a 413.
