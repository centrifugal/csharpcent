# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0]

Implements the [Centrifugo server SDK
specification](https://github.com/centrifugal/gocent/blob/master/spec.md) 0.3.

The protos are synced from Centrifugo's master, which has the 0.3 changes
(centrifugal/centrifugo#1308, #1309, #1310) but no release tag yet: they are
synced again from the tag before this version is released.

### Changed (breaking)

- A request leaving a field the protos mark "Required." `null`, empty or zero -
  which goes out as the field left unset - now fails before sending with
  `CentInvalidRequestException` naming its path, nested fields included
  (`LimitStrategy.RateLimit.Policies[0].Rate is required`), from a call, a
  batch or a command; Centrifugo refused it with code 107 before. A field whose
  empty value has a meaning - `InvalidateUserTokensRequest.User`, the device
  updates' fields - is not checked. The messages of the publish and broadcast
  checks are now `Channel is required` and `Channels is required`.
- A message in a request whose fields are all empty is left out, as an empty
  field is: an empty `PushLimitStrategy`, say, is no longer sent as `{}`.
- `CentHttpException`'s message for a redirect says what redirects: Centrifugo's
  router, for a path not in canonical form.
- The `User-Agent` is `csharpcent/0.2`.
- `EndpointUnauthorizedException`, which an endpoint function throws to answer
  403, is renamed `EndpointForbiddenException`: it read like the API client's
  `CentUnauthorizedException`, Centrifugo refusing this server's credentials,
  with the opposite meaning. Client SDKs report the 403 as their
  `UnauthorizedError`.
- A call can no longer be left unbounded: `Timeout.InfiniteTimeSpan`, which 0.1.0
  took as no timeout, is refused, as are negative timeouts and ones longer than
  24 days. `RequestTimeout` fails creating the client with
  `CentInvalidConfigException`, `WithTimeout` with `ArgumentOutOfRangeException`,
  and `AttemptTimeout` the call with `CentInvalidRequestException` before any
  attempt. `RequestTimeout` zero still means the default, 10 seconds; zero is
  refused where it differs from unset, for `WithTimeout` and `AttemptTimeout`.
- An exception thrown by `ApiEndpointFunc` or `BearerTokenFunc` now fails the
  call as a `CentFunctionException` naming the function (`BearerTokenFunc: ...`),
  with the exception as its `InnerException`, where it escaped as it was
  before; `Retry.IsRetryable` judges it by that exception. An endpoint from
  `ApiEndpointFunc` failing `ApiEndpoint`'s rules, or an empty or malformed
  token from `BearerTokenFunc`, fails the call with
  `CentInvalidConfigException` before sending, as before; its docs now say a
  call can throw it, not only creating the client.
- `BroadcastRetryOptions.AttemptTimeout` is each attempt's own timeout,
  replacing `RequestTimeout` for it - longer or shorter - where it was capped by
  `RequestTimeout` before. Without it, `RequestTimeout` bounds each attempt. The
  call as a whole is bounded by its `CancellationToken`, and by the timeout of a
  client from `WithTimeout`, rather than by `RequestTimeout`. Every other call
  keeps its rule, now said in the docs: `RequestTimeout`, or `WithTimeout`'s
  replacing it, bounds every call, and a `CancellationToken` only shortens it.

### Added

- `device_update`'s `UserUpdate`, `TimezoneUpdate`, `LocaleUpdate` and
  `MetaUpdate` are sent whenever set, even empty - an empty one detaches the
  devices from their user or clears the setting - and left out when `null`.
- Constants for the fields with a fixed set of values, generated from the
  protos under the names spec 0.3 pins for every SDK: `ProviderValues`,
  `PlatformValues`, `OpValues`, `StatusValues`, `KeyModeValues`,
  `FilterNodeOpValues`, `CmpValues`, and in `Centrifugal.Cent.Proxy`
  `FilterNodeOpValues`, `CmpValues`, `ProtocolValues`, `EncodingValues`,
  `AllowValues`, `CapabilityMatchValues`, `KeyModeValues`,
  `ChannelEventTypeValues`. The fields stay strings.
- `HistoryAllAsync`, `DeviceListAllAsync`, `DeviceTopicListAllAsync`,
  `UserTopicListAllAsync` and `MapReadStateAllAsync`: every item of a paged
  method as an `IAsyncEnumerable`, a page read when the iteration reaches it.
  `HistoryAllAsync` yields `HistoryItem`s - the `Publication` with its
  `Position`, to resume from - and `MapReadStateAllAsync` `MapStateItem`s - the
  `Entry` with `State`, the position the state was read at. `MapReadStateAllAsync`
  reads every page after the first at the first one's revision, unless the
  request sets one, so a map state reset between pages fails with code 112
  rather than mixing two states.
- `Position` on every result holding a stream position - `PublishResult`,
  `HistoryResult`, `MapPublishResult`, `MapRemoveResult`, `MapReadStateResult`
  and `MapReadStreamResult`: the `StreamPosition` which `HistoryRequest.Since`
  takes. It is not part of a result's JSON.
- `ConnectionClaims.Profile`, Centrifugo PRO's compression profile.
- `ConnectResult.Profile` in the connect proxy's result (Centrifugo PRO).

### Fixed

- The docs: a consumer drops a string payload where the method goes apart from
  it; what Centrifugo makes of a proxy response with none of result, error and
  disconnect.

## [0.1.0]

The first release, implementing the [Centrifugo server SDK
specification](https://github.com/centrifugal/gocent/blob/master/spec.md) 0.2
for Centrifugo v6.

### Added

- `CentClient`: every API method of Centrifugo v6.10.2 but `rpc`, Centrifugo
  PRO's included, with request and result types generated from `api.proto`;
  required fields as `required` members.
- Payloads as any JSON value - an application's objects, anonymous types,
  `JsonElement`, `JsonNode`, `RawJson` - carried exactly, and `JsonElement` in
  replies, with `Decode<T>()`; one deadline per call; the error model with
  hints, the transport error mode, `Retry.IsRetryable`; every exception
  constructible by applications' test doubles.
- `BroadcastAsync` with every channel's outcome, `BroadcastWithRetryAsync`,
  batches with typed pending results, automatic batching, `CloseAsync`.
- Commands for async consumers.
- Connection, subscription and Centrifugo PRO API tokens (HMAC, RSA, ECDSA, and
  Ed25519 through a signing function of the application's choice), JWKS, shared
  poll signatures.
- Proxy handlers, the endpoints client SDKs call and the JWKS endpoint, as
  framework-free `CentHandler`s; `Centrifugal.Cent.AspNetCore` serves them with
  `MapCentrifugo`, logging the requests they fail, and registers the client
  with `AddCentClient`, configured from the application's configuration, its
  `HttpClient` made by `IHttpClientFactory`.
- `PublishAsync(channel, data)` and `BroadcastAsync(channels, data)`, the short
  forms of the request forms.
- `CentClient`'s and `CentBatch`'s methods are virtual, for mocking libraries;
  `Pending.FromResult` and `Pending.FromException` make commands' outcomes.
- .NET 8 and newer; trimming and native AOT.

[0.2.0]: https://github.com/centrifugal/csharpcent/releases/tag/v0.2.0
[0.1.0]: https://github.com/centrifugal/csharpcent/releases/tag/v0.1.0
