# Contributing

Thanks for helping. Please read the [code of conduct](CODE_OF_CONDUCT.md); report security problems privately as described in [SECURITY.md](SECURITY.md), never as a public issue. This project aims to stay small, fast and boring to use, so changes are judged by how much complexity they take off the people who use the library.

## Build and test

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~Mailgun"     # one area
dotnet run -c Release --project benchmarks/UniversalNotifier.NET.Benchmarks -- --filter "*"
```

The libraries and the test project target `net10.0` and `net11.0`, so the whole suite runs on every supported runtime. Warnings are errors.

## Ground rules

- **No real credentials in tests.** Provider tests use a stub `HttpMessageHandler`, a fake in-process server (see the SMTP tests) or an internal seam (see the FCM gateway).
- **Every behavior has a test**, including the failure paths: an error response, an empty body, a timeout, cancellation.
- **Never log or return secrets, recipient addresses or message content.** Error messages say what failed, not who it was for.
- **Failures are results.** Providers return `NotifierResponse.Failed(...)` with an honest `NotifierErrorKind`; they do not throw for provider errors.
- **No new dependency in the core.** A provider that needs an SDK gets its own package.
- **Keep the common path simple.** New options need a reason a real user hit.
- **The public API is tracked.** `PublicAPI.Unshipped.txt` in each project lists the public surface; an unrecorded public change fails the build (RS0016). Add the new symbols to `PublicAPI.Unshipped.txt` on purpose. When a version ships, move its lines to `PublicAPI.Shipped.txt`; after that, removing or changing a shipped symbol is a breaking change (RS0017) and needs a major version.
- **Record big decisions** as an ADR in `docs/decisions/` (context, problem, alternatives, decision, consequences, trade-offs).

## Adding a provider

1. Create `src/UniversalNotifier.NET.<Channel>.<Provider>` (or `UniversalNotifier.NET.<Provider>` when channel and provider are the same thing, like Telegram).
2. Derive from `NotifierBase<TMessage>` and implement `SendCoreAsync`. See [Custom providers and channels](docs/custom-provider.md).
3. Add an options class with a `Validate` method, an options validator, and an `AddX` extension in the `Microsoft.Extensions.DependencyInjection` namespace.
4. Add tests next to the others in `tests/UniversalNotifier.NET.Tests`: request construction, every response branch, per-recipient results for bulk, classification of every error, option validation, DI registration.
5. Add a fixture for it to `tests/UniversalNotifier.NET.Tests/ProviderContractTests.cs`: the shared contract (accepts, validates before I/O, cancellation, classified failures, no leaked secrets) must pass.
6. Add it to the table in the README and to `docs/delivery-guarantees.md`.

## Releasing

Maintainers only. Releases are tagged and published by `.github/workflows/release.yml` using NuGet trusted publishing, so no API key is stored.

1. Update `CHANGELOG.md` and write `docs/releases/v<version>.md` (the GitHub release body).
2. Set `<Version>` in `src/Directory.Build.props`.
3. Make sure CI is green on `main`, then push the tag: `git tag v2.0.0 && git push origin v2.0.0`.
4. After the first stable release: move each project's `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` and set `PackageValidationBaselineVersion` to the released version, so later releases are checked for breaking changes.

One-time setup: create a trusted publishing policy on nuget.org for this repository and `release.yml`, create a GitHub environment named `release`, and set the `NUGET_USER` secret to the nuget.org profile name.

## Commits and pull requests

Conventional Commits (`feat(sms): ...`, `fix(core): ...`, `docs: ...`), one logical change per commit, a branch per change and a pull request into `main`.
