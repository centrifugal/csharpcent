# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.1.0]: https://github.com/centrifugal/csharpcent/releases/tag/v0.1.0
