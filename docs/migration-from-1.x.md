# Migrating from 1.x

2.0 is a redesign, not an update. The 1.x packages could not send a message (`MailgunNotifier.FromAccount` threw `NotImplementedException`) and reported most provider errors as successes, so there is no compatibility layer. Moving is a rewrite of a few lines.

## Packages

| 1.x | 2.0 |
|---|---|
| `UniversalNotifier.NET` (interfaces only) | `UniversalNotifier.NET` (core, dispatcher, templates) |
| `UniversalNotifier.NET.Mailgun` | `UniversalNotifier.NET.Email.Mailgun` |

The old `UniversalNotifier.NET.Mailgun` package id is not used any more; install the new one.

## Concepts

| 1.x | 2.0 |
|---|---|
| `INotifier<TReceiver, TContent>` | `INotifier<TMessage>` plus channel interfaces such as `IEmailNotifier` |
| `EmailNotifierReceiver` + abstract `EmailNotifierContent` | `EmailMessage` (recipients and content together; no subclassing) |
| `NotifierContent.ParseContent(template, values)` | `{{Token}}` templates rendered by the dispatcher; `ITemplateRenderer` |
| `FromAccount` abstract property | `From` option |
| `NotifierResponse.Errors` (settable list) | immutable `NotifierResponse` with classified errors and per-recipient results |
| `Send` / `SendMany` (sync and async) | `SendAsync` with a `CancellationToken`; one message may have many recipients |
| `new MailgunNotifier(key, domain)` | `AddMailgun(o => ...)` or `new NotifierBuilder().AddMailgun(...)` |

## Before and after

```csharp
// 1.x (did not work: FromAccount threw)
var notifier = new MailgunNotifier("key", "mg.example.com");
await notifier.SendAsync(content, new EmailNotifierReceiver("ada@example.com", "Ada"));
```

```csharp
// 2.0
services.AddUniversalNotifier(n => n.AddMailgun(o =>
{
    o.ApiKey = "key";
    o.Domain = "mg.example.com";
    o.From = "noreply@mg.example.com";
}));

var result = await notifications.SendAsync(
    Notification.To(new Recipient { Email = new EmailAddress("ada@example.com", "Ada") })
        .Email("Hello {{Name}}", text: "Hi {{Name}}")
        .WithData(new { Name = "Ada" }));
```

## Behavior changes to be aware of

- Delivery failures are returned in the result; they are no longer reported as `Success`.
- Bulk email goes out as individual messages, so recipients no longer see each other's addresses. Cc/Bcc need a single `To` recipient.
- Mailgun EU domains work (`o.Region = MailgunRegion.Eu`).
- HTML templates encode token values. Use `{{{Token}}}` to insert trusted HTML.
- Only `{{Token}}` placeholders are supported; `%token%` and `{token}` are gone because they collided with CSS and JavaScript braces.
- Missing tokens are an error by default (`ConfigureTemplates(o => o.MissingToken = MissingTokenBehavior.KeepToken)` restores the old leniency).
