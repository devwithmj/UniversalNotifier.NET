# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/) and the project uses [Semantic Versioning](https://semver.org/).

## [2.0.0-preview.1] - unreleased

Release notes: [v2.0.0-preview.1](docs/releases/v2.0.0-preview.1.md).

A ground-up redesign. See [Migrating from 1.x](docs/migration-from-1.x.md).

### Added

- Channel interfaces (`IEmailNotifier`, `ISmsNotifier`, `IPushNotifier`, `ITelegramNotifier`, `IWebPushNotifier`) over a single-parameter `INotifier<TMessage>`.
- `NotifierBase<TMessage>`: validation, one error model, cancellation, logging and opt-in retries for every provider.
- Immutable `NotifierResponse` with delivery status, per-recipient results and classified errors.
- `INotificationService`: one notification, several channels, templates, named providers.
- `{{Token}}` template engine with nested values, HTML encoding, caching and pluggable renderer and stores.
- Providers: SMTP (MailKit), Mailgun, Twilio SMS, Firebase Cloud Messaging, Telegram, Web Push (VAPID).
- `UniversalNotifier.NET.Background`: bounded in-memory queue with backpressure, concurrent worker, graceful shutdown, result handlers.
- `NotificationJson` for storing notifications in durable queues.
- `UniversalNotifier.NET.Testing` fake notifiers.
- WhatsApp channel (`IWhatsAppNotifier`, `Notification.WhatsApp`, `WhatsAppMessage` with approved-template support) served by Twilio. The Twilio package is now `UniversalNotifier.NET.Twilio` and `AddTwilio` registers SMS and WhatsApp.
- Sandbox switches that validate a live setup without delivering: Mailgun `TestMode`, SendGrid `SandboxMode`, FCM `DryRun`, Telegram `UseTestEnvironment`; sandbox variants in the live-test project.
- `UniversalNotifier.NET.Email.Ses`: Amazon SES v2 provider with built-in request signing (no AWS SDK), one individual message per recipient, attachments and inline images.
- The core package is trim and Native AOT compatible: source-generated `NotificationJson`, trimming annotations on the generic registrations, and `WithData(object)` marked as reflection-based (use the dictionary overload under trimming or AOT).
- `UniversalNotifier.NET.Receipts`: delivery reports for Mailgun events, SendGrid signed events and Twilio (SMS and WhatsApp) status callbacks. Verifies the provider signature (Mailgun also rejects replays) and returns one provider-neutral `DeliveryEvent`; no web framework dependency.
- `SesDeliveryWebhook`: Amazon SES events through SNS, with signature verification against the AWS certificate, topic pinning, automatic subscription confirmation and refusal to fetch any non-Amazon address. `IDeliveryWebhook` is asynchronous.
- `UniversalNotifier.NET.Receipts.AspNetCore`: `MapDeliveryReceipts` hosts a verified delivery webhook and hands each event to your `IDeliveryReceiptHandler`s (401 on a bad signature, 413 above 1 MiB, handler failures surface so the provider retries).
- `UniversalNotifier.NET.Email.SendGrid`: SendGrid v3 provider, one personalization per recipient.
- `UniversalNotifier.NET.InApp`: per-user inbox channel with unread counts, mark as read and a replaceable store.
- `UniversalNotifier.NET.Webhook`: Slack, Microsoft Teams (Adaptive Card) and generic JSON webhooks by endpoint name, optional HMAC signing.
- `UniversalNotifier.NET.Templates.Fluid`: Fluid (Liquid) renderer with the same safety model as the Scriban adapter.
- `UniversalNotifier.NET.Templates.Scriban`: Scriban renderer with execution limits, no `include` and a sanitized data model.
- `WithRateLimit(perSecond, burst)`: timer-free per-notifier rate limiting.
- `Notification.IdempotencyKey` and `BackgroundDeliveryOptions.DeduplicationWindow`: repeated submissions are queued once.
- Traces and metrics through `System.Diagnostics` (`NotifierTelemetry`): a span per send and per notification, message, retry and duration metrics, and queue depth, rejected and deduplicated metrics. No recipients or content in tags.
- Template validation at startup (`TemplateValidator`) for the Scriban and Fluid renderers, and an embedded-resource template store (`AddEmbeddedTemplates`).
- A shared provider contract test suite and public API tracking (`PublicApiAnalyzers`).
- Targets .NET 10 and .NET 11.

### Fixed (relative to 1.x)

- Sending email no longer throws `NotImplementedException`.
- Provider errors are no longer reported as success.
- Empty or non-JSON provider responses no longer throw.
- Bulk email no longer reveals all recipients to each other.
- Display names are quoted so they cannot split or inject recipients.
- Template values are HTML-encoded and never re-scanned for tokens.
- Mailgun EU region is supported.

### Changed

- The Mailgun package is now `UniversalNotifier.NET.Email.Mailgun`.
- The sync `Send`/`SendMany` methods are gone; everything is async with a `CancellationToken`.
