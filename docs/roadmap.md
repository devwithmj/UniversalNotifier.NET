# Roadmap and known gaps

An honest list of what is not done, ordered by how much it matters. "Spec" refers to the section of the v2 concept the item comes from.

## Before a stable 2.0

1. **Live verification of every provider.** Run `tests/UniversalNotifier.NET.LiveTests` ([guide](live-testing.md)). Least certain: the Microsoft Teams Adaptive Card payload (Workflows webhook), Mailgun batch sending with `recipient-variables`, FCM behavior with a real token.
2. **First release.** Merge the PR, publish with trusted publishing (see CONTRIBUTING, "Releasing"), then set `PackageValidationBaselineVersion` and move each `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`.
3. **Observability** is built into the core (`System.Diagnostics` traces and metrics, see [Observability](observability.md)). A separate `UniversalNotifier.NET.Observability` package, as the spec sketches, was judged unnecessary: OpenTelemetry needs only `AddSource` and `AddMeter` with the names the core exposes. Still open: an `IHealthCheck` per provider.
4. **Samples and guides (spec 13).** Four samples (`QuickStart`, `WorkerService`, `CustomExtensions`, `MultiChannel`) and guides for configuration, templates, error handling and each provider exist. Missing from the spec list: a generated API reference and a sample per provider.
5. **Package polish.** A package icon, a generated API reference (DocFX from the XML docs), README badges once published.

## Templates (spec 6)

- Done: template validation at startup (`TemplateValidator`, also run by `NotifierBuilder.Build()`) and an embedded-resource store.
- **Localization**: culture is per renderer today, not per recipient. Add a recipient culture and per-culture template lookup.
- Template **versioning**.

## Delivery (spec 7)

- **Provider fallback / failover** (spec 4.5). Deliberately deferred: failover can double-send. Needs the same care as retries.
- **Delivery receipts and bounces.** Built for Mailgun, Twilio and SendGrid (`UniversalNotifier.NET.Receipts`: signature verification, replay protection, one `DeliveryEvent`). Still open: Amazon SES (SNS), an ASP.NET Core adapter that maps the endpoint for you, and a live check against real webhooks.
- **Durable queue adapters** (database, RabbitMQ, Azure Service Bus) behind `INotificationQueue`.
- **Shared rate limit and deduplication** across instances (today per process).
- **SMTP connection pooling.**
- **Streaming attachments** (attachments are `ReadOnlyMemory<byte>`, so large files are fully buffered).

## Channels and providers

- Not built: **Handlebars** renderer. Amazon SES is built (REST with built-in signing; credentials must be passed in, there is no default credential chain). WhatsApp is built (through Twilio); other WhatsApp providers and inbound replies are not.
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
