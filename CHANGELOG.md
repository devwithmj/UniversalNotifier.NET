# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/) and the project uses [Semantic Versioning](https://semver.org/).

## [2.0.0-preview.1] - unreleased

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
