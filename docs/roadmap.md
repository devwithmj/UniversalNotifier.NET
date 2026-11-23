# Roadmap and known gaps

An honest list of what is not done, ordered by how much it matters. "Spec" refers to the section of the v2 concept the item comes from.

## Before a stable 2.0

1. **Live verification of every provider.** Run `tests/UniversalNotifier.NET.LiveTests` ([guide](live-testing.md)). Least certain: the Microsoft Teams Adaptive Card payload (Workflows webhook), Mailgun batch sending with `recipient-variables`, FCM behavior with a real token.
2. **First release.** Merge the PR, publish with trusted publishing (see CONTRIBUTING, "Releasing"), then set `PackageValidationBaselineVersion` and move each `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`.
3. **Observability (spec 5.1, 5.14).** Nothing emits traces or metrics yet, only logs. Add an `ActivitySource` and a `Meter` (`System.Diagnostics`, no dependency) in the core: a span per send and counters for accepted, failed, retried and queued, tagged with channel, provider and error kind, never with recipients or content. The spec's `UniversalNotifier.NET.Observability` package would hold the OpenTelemetry wiring.
4. **Per-provider setup docs and more samples (spec 13).** The spec lists 11 samples and guides for configuration, templates, error handling and each provider. There are two samples and the README plus four guides. Missing: provider setup pages, a templates guide, an error-handling guide, an `appsettings.json` configuration example, custom-provider and custom-renderer samples.
5. **Package polish.** A package icon, a generated API reference (DocFX from the XML docs), README badges once published.

## Templates (spec 6)

- Template **validation at startup** (parse every registered template and fail fast).
- **Embedded-resource template store** (spec 6.6 lists embedded; in-memory and directory exist).
- **Localization**: culture is per renderer today, not per recipient. Add a recipient culture and per-culture template lookup.
- Template **versioning**.

## Delivery (spec 7)

- **Provider fallback / failover** (spec 4.5). Deliberately deferred: failover can double-send. Needs the same care as retries.
- **Delivery receipts and bounces.** Needs an inbound endpoint; likely a separate ASP.NET Core package that maps provider webhooks (Mailgun events, Twilio status callbacks) to a common event.
- **Durable queue adapters** (database, RabbitMQ, Azure Service Bus) behind `INotificationQueue`.
- **Shared rate limit and deduplication** across instances (today per process).
- **SMTP connection pooling.**
- **Streaming attachments** (attachments are `ReadOnlyMemory<byte>`, so large files are fully buffered).

## Channels and providers

- Not built: **WhatsApp** (via Twilio, needs sender onboarding to test), **Amazon SES**, **Handlebars** renderer.
- Spec 8 "evaluate" items not built: scheduling, priorities, user preferences, notification history, health checks, provider capability discovery, an all-in-one `UniversalNotifier.NET.All` package.

## Engineering

- **Native AOT / trimming.** Object data paths use reflection, so the libraries are not marked AOT-compatible. A reflection-free `WithData<T>` with a source generator would fix it.
- **Code coverage** is not collected in CI.
- **Option reload.** Providers read options when first created; `IOptionsMonitor` changes are not applied.
- **International email.** Validation rejects quoted local parts and does not handle SMTPUTF8.
- **Release automation.** The version is set by hand in `src/Directory.Build.props`; consider MinVer from tags.

## Supply chain and repository

- Trusted publishing replaces long-lived API keys (done in `release.yml`; needs the nuget.org policy).
- Package signing and an SBOM are not done.
- Branch protection with required status checks (`build`, `pack`, CodeQL) is a GitHub setting to turn on.
- Dependabot and CodeQL are configured (`.github`).
