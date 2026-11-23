# Architecture

## The shape

```
your code
   |  depends on
   v
INotificationService ----------------------------+   (several channels at once)
   |                                              |
   v                                              v
INotificationChannel handlers          IEmailNotifier  ISmsNotifier  IPushNotifier  ITelegramNotifier  IWebPushNotifier
   |  render templates, pick the address,         ^
   |  pick the provider                           | implemented by
   +--------------------------------------------> providers: Smtp, Mailgun, Twilio, Fcm, Telegram, WebPush, ...
                                                   (all derive from NotifierBase<TMessage>)
```

- **Channel interface** (`IEmailNotifier`, ...): what application code depends on. It exists only where the message shape differs between channels.
- **`INotifier<in TMessage>`**: the one-method base (`SendAsync(message, ct)`), a single type parameter because a message carries its own recipients and content.
- **Provider**: one class per vendor, derived from `NotifierBase<TMessage>`. It implements `SendCoreAsync` and nothing else.
- **Dispatcher** (`INotificationService`): turns one `Notification` into one message per channel, concurrently, with per-channel results.

## Decisions and trade-offs

Each major decision has a record with context, alternatives and trade-offs in [`docs/decisions`](decisions/). The summary:

**Failures are results, not exceptions.** A notification system is called from request paths and workers where a rejected phone number must not become a 500. `NotifierBase` converts everything a provider can throw into a classified `NotifierResponse`. Only programmer errors (`null`) and cancellation throw. *Trade-off:* callers must check `Success`; the result type makes that obvious and the classification tells them what to do next.

**Template method for providers.** `NotifierBase.SendAsync` is sealed and owns validation, logging, exception mapping, cancellation and retries. Providers cannot forget them or implement them differently. *Trade-off:* providers are a base class, not just an interface; the interface is still the public contract.

**Per-recipient results.** Bulk sends return a `RecipientResult` per address, number, token, chat or subscription. This is what makes "remove this expired device token" possible. *Trade-off:* a few more allocations on bulk sends, only where there are several recipients.

**Retries are opt-in and conservative.** Only `Transient` errors are retried, never after a partial success, and `Unknown` (the provider may have sent it) only when the caller accepts duplicates. One policy lives in `NotifierBase`; the Web Push library's own retry is disabled so there is exactly one.

**Notifications are data.** `Notification` is an immutable record of strings, lists and a data dictionary. It can be queued, stored and serialized (`NotificationJson`). Template data is snapshotted when set, so a queued notification cannot change underneath the queue. *Trade-off:* `WithData(object)` uses reflection once, at construction; a dictionary avoids it.

**Providers own their HTTP client only when a URL is a secret.** Mailgun and Twilio use `IHttpClientFactory` (configurable, mockable). Telegram (bot token in the path) and Web Push (the endpoint identifies a browser) create their own client so factory logging never records those URLs.

**The core has no provider SDKs and no infrastructure.** It depends on `Microsoft.Extensions.DependencyInjection(.Abstractions)`, `Options` and `Logging.Abstractions`. Providers that need an SDK (MailKit, Firebase Admin, Lib.Net.Http.WebPush) are separate packages. REST providers (Mailgun, Twilio, Telegram) use plain `HttpClient` and System.Text.Json.

**Provider registration convention.** An unnamed `AddX()` becomes the channel's default and is also reachable by the provider's own name; `AddX(name: "...")` is reachable only by that name. The last unnamed registration wins, as with any DI service. Options are named by the registration key, so `services.Configure<MailgunOptions>("mailgun", section)` binds configuration.

## Native AOT and trimming

The libraries are not annotated as AOT-compatible. The JSON in the REST providers, Telegram, Web Push and Webhook is source-generated, and dictionary template data is reflection-free, but `WithData(object)`, the built-in renderer's object access and the Scriban data model read public properties by reflection (and `NotificationJson` serializes arbitrary data). An AOT application can use dictionaries for template data and its own source-generated context for queue payloads.

## Project layout

| Project | Package | Role |
|---|---|---|
| `UniversalNotifier.NET` | yes | contracts, messages, results, templates, dispatcher, DI |
| `UniversalNotifier.NET.Email.Smtp` | yes | SMTP via MailKit |
| `UniversalNotifier.NET.Email.Mailgun` | yes | Mailgun REST |
| `UniversalNotifier.NET.Sms.Twilio` | yes | Twilio REST |
| `UniversalNotifier.NET.Push.Fcm` | yes | Firebase Admin SDK |
| `UniversalNotifier.NET.Telegram` | yes | Telegram Bot API |
| `UniversalNotifier.NET.WebPush` | yes | Lib.Net.Http.WebPush |
| `UniversalNotifier.NET.Email.SendGrid` | yes | SendGrid v3 REST |
| `UniversalNotifier.NET.InApp` | yes | per-user inbox, in-memory store, `IInAppStore` |
| `UniversalNotifier.NET.Webhook` | yes | Slack, Teams, generic JSON webhooks |
| `UniversalNotifier.NET.Templates.Scriban` | yes | Scriban renderer |
| `UniversalNotifier.NET.Templates.Fluid` | yes | Fluid (Liquid) renderer |
| `UniversalNotifier.NET.Background` | yes | in-memory queue, worker, result handlers |
| `UniversalNotifier.NET.Testing` | yes | fake notifiers |
| `src/Shared/ProviderHttp.cs` | no | small helpers compiled into the REST providers (not public API) |

## Performance notes

- Templates are parsed once and cached; rendering is a single pass over precomputed segments and a reused `StringBuilder`. A template without tokens returns the same string instance.
- Validation allocates nothing for a valid message.
- Logging uses source-generated `LoggerMessage` methods and never formats unless the level is enabled.
- The dispatcher sends a single channel inline and several with `Task.WhenAll`.
- The queue is a bounded `Channel<T>` with synchronous continuations disabled, so a producer never runs a worker's code.
