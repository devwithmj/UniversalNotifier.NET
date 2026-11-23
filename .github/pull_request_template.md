## What and why

<!-- What does this change, and what problem does it solve? Link the issue. -->

## Checklist

- [ ] Tests cover the behavior, including failure paths (error response, empty body, timeout, cancellation)
- [ ] `dotnet build` and `dotnet test` pass locally (warnings are errors)
- [ ] New public API is recorded in `PublicAPI.Unshipped.txt`
- [ ] A new provider has a fixture in `ProviderContractTests.cs`
- [ ] No secrets, recipient addresses or message content in logs, errors or `ToString()`
- [ ] Docs, `CHANGELOG.md` and (for big decisions) an ADR in `docs/decisions` are updated
- [ ] Commits follow Conventional Commits
