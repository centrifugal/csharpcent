# Contributing

## Design first

Behaviour is specified in the [Centrifugo server SDK
specification](https://github.com/centrifugal/gocent/blob/master/spec.md),
which every Centrifugal server SDK follows, and the choices particular to .NET
in [AGENTS.md](AGENTS.md). When the code and the spec disagree, one of them is
a bug.

## Local checks

These run in CI on every pull request:

```sh
dotnet format Centrifugal.Cent.slnx --verify-no-changes
dotnet build Centrifugal.Cent.slnx -c Release          # analyzers: warnings are errors
dotnet run --project tools/Generator -- --check         # the generated code is current
dotnet test --project tests/Centrifugal.Cent.Tests      # net8.0 and net10.0
dotnet publish examples/Aot -c Release -p:TreatWarningsAsErrors=true   # trimming and native AOT
dotnet pack src/Centrifugal.Cent -c Release -o artifacts
```

The net8.0 tests run on the newest runtime when .NET 8 is not installed. CI runs
them on .NET 8 too, on Linux, macOS and Windows.

### Test servers

Most tests need no server: they run against a fake Centrifugo
(`tests/Centrifugal.Cent.Tests/FakeCentrifugo.cs`) and small local servers. The
integration tests run against Centrifugo `v6.10.2`, and are skipped unless
enabled:

```sh
docker compose -f testdata/centrifugo-pro.compose.yml up -d --wait   # Centrifugo PRO, Redis, PostgreSQL
CENTRIFUGO_TESTS=1 CSHARPCENT_TEST_PRO=1 dotnet test --project tests/Centrifugal.Cent.Tests --max-parallel-test-modules 1
docker compose -f testdata/centrifugo-pro.compose.yml down
```

Against Centrifugo OSS - as CI's `integration` job runs it - the PRO tests
skip. The net8.0 and net10.0 test apps run one at a time: both serve the proxy
handlers on port 3100 and an API JWKS on 3101 of the host. Centrifugo PRO runs in sandbox mode, without a license key; it is
commercial software under the [Centrifugal Labs License
Agreement](https://centrifugal.dev/license).

## Layout

- `src/Centrifugal.Cent`: the SDK, the package `Centrifugal.Cent`.
- `src/Centrifugal.Cent.AspNetCore`: its ASP.NET Core integration.
- `tools/Generator`: writes `src/Centrifugal.Cent/Generated/` and the generated tests from `proto/`.
- `proto/`: Centrifugo's protos, from a released tag.
- `examples/`: publishing, an ASP.NET Core backend, native AOT.

## Commits and pull requests

- PRs are squash-merged. **The PR title becomes the commit message**, and it must follow [Conventional Commits](https://www.conventionalcommits.org) (checked in CI):
  - `feat: ...` → minor version bump before 1.0 (and a changelog "Added" entry)
  - `fix: ...` → patch bump ("Fixed")
  - `feat!: ...` or a `BREAKING CHANGE:` footer → breaking bump
  - `perf`, `refactor`, `docs`, `revert` → changelog entries; `test`, `ci`, `build`, `chore` → no entry
- Each bug fix comes with a test that fails without the fix.

## Releases

As in [centrifuge-csharp](https://github.com/centrifugal/centrifuge-csharp): bump the version, then tag. A release can include any number of merged PRs.

1. **Bump the version** in a normal PR (or commit) to `main`, titled `chore: bump to X.Y.Z`: `<Version>` in `src/Centrifugal.Cent/Centrifugal.Cent.csproj` and `src/Centrifugal.Cent.AspNetCore/Centrifugal.Cent.AspNetCore.csproj` - both packages share a version - the `User-Agent` in `src/Centrifugal.Cent/ClientCore.cs` when the minor version changes before 1.0 (a test checks it), and the `CHANGELOG.md` entry. Bump by semver: before 1.0, a breaking change bumps the minor version (`0.1.x` → `0.2.0`); anything else bumps the patch.
2. **Wait for CI** to pass on `main`.
3. **Tag it.** Either create the release on GitHub (Releases → Draft a new release → new tag `vX.Y.Z` on `main`, write the notes, publish), or push a tag, `git tag vX.Y.Z && git push origin vX.Y.Z`, and let the workflow create the release with generated notes.
4. The tag runs `.github/workflows/release.yml`. It checks that the tag names the projects' version, builds, tests and packs both packages, pushes them to NuGet, and attaches them to the GitHub release.

A version already on NuGet is skipped, so rerunning the workflow after a failure finishes the release. Nobody runs `dotnet nuget push` by hand.

After the first release, set `<PackageValidationBaselineVersion>` in both projects to the latest released version: `dotnet pack` then fails on a change breaking the public API of a release.

### Release setup (one time, maintainers)

1. **NuGet Trusted Publishing.** On [nuget.org](https://www.nuget.org), signed in as `FZambia` (the `user` in `release.yml`), open Trusted Publishing and add a GitHub Actions policy: owner `centrifugal`, repository `csharpcent`, workflow file `release.yml`, environment `release`. The workflow then exchanges its OIDC token for a one-hour key at each release; no API key is stored. Packages the policy's owner creates are covered too, so the first release needs nothing more.
2. **GitHub environment.** Create an environment named `release` (Settings → Environments); it needs no secrets. Under deployment branches and tags, allow only tags matching `v*`. Optionally add required reviewers: they approve each release.
3. **Tag protection.** Add a tag ruleset for `v*` (Settings → Rules → Rulesets) that only maintainers can bypass, so only they can create, move or delete release tags.
4. **Branch protection on `main`.** Require the `CI success` and `Conventional commit title` checks. Allow squash merges only.
5. **Prefix reservation** (optional). Ask NuGet to reserve the `Centrifugal.` prefix for the account (account@nuget.org): its packages then show as verified, and nobody else can publish under it.
