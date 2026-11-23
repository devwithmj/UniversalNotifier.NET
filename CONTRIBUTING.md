# Contributing

Thanks for helping. This project aims to stay small, fast and boring to use, so changes are judged by how much complexity they take off the people who use the library.

## Build and test

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~Mailgun"     # one area
dotnet run -c Release --project benchmarks/UniversalNotifier.NET.Benchmarks -- --filter "*"
```

The libraries and the test project target `net8.0`, `net10.0` and `net11.0`, so the whole suite runs on every supported runtime. Warnings are errors.

## Ground rules

- **No real credentials in tests.** Provider tests use a stub `HttpMessageHandler`, a fake in-process server (see the SMTP tests) or an internal seam (see the FCM gateway).
- **Every behavior has a test**, including the failure paths: an error response, an empty body, a timeout, cancellation.
- **Never log or return secrets, recipient addresses or message content.** Error messages say what failed, not who it was for.
- **Failures are results.** Providers return `NotifierResponse.Failed(...)` with an honest `NotifierErrorKind`; they do not throw for provider errors.
- **No new dependency in the core.** A provider that needs an SDK gets its own package.
- **Keep the common path simple.** New options need a reason a real user hit.

## Adding a provider

1. Create `src/UniversalNotifier.NET.<Channel>.<Provider>` (or `UniversalNotifier.NET.<Provider>` when channel and provider are the same thing, like Telegram).
2. Derive from `NotifierBase<TMessage>` and implement `SendCoreAsync`. See [Custom providers and channels](docs/custom-provider.md).
3. Add an options class with a `Validate` method, an options validator, and an `AddX` extension in the `Microsoft.Extensions.DependencyInjection` namespace.
4. Add tests next to the others in `tests/UniversalNotifier.NET.Tests`: request construction, every response branch, per-recipient results for bulk, classification of every error, option validation, DI registration.
5. Add it to the table in the README and to `docs/delivery-guarantees.md`.

## Commits and pull requests

Conventional Commits (`feat(sms): ...`, `fix(core): ...`, `docs: ...`), one logical change per commit, a branch per change and a pull request into `main`.
