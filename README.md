# UniversalNotifier.NET

**One API for every notification channel.** Send email, SMS, mobile push, web push and Telegram from .NET through the same small API, without writing or learning each provider's integration. Switch providers by changing configuration, not code.

```csharp
await notifications.SendAsync(
    Notification.To(new Recipient { Email = "ada@example.com", Phone = "+14165551234" })
        .Email("Welcome, {{Name}}!", html: "<p>Hi <b>{{Name}}</b></p>", text: "Hi {{Name}}")
        .Sms("Hi {{Name}}, welcome aboard!")
        .WithData(new { Name = "Ada" }));
```

- **Provider independent.** Your code depends on `IEmailNotifier`, `ISmsNotifier`, ... never on a vendor SDK.
- **Honest results.** Failures are returned, not thrown, and say whether repeating is safe. "Accepted" never pretends to mean "delivered".
- **Safe by default.** HTML is encoded, bulk sends never reveal other recipients, secrets and message content stay out of logs, retries cannot silently duplicate messages.
- **Light and fast.** The core depends only on `Microsoft.Extensions.*` abstractions. Dispatching a notification takes about a microsecond.
- **Queue friendly.** Notifications are plain immutable data that round-trip through JSON. A bounded in-memory queue with backpressure is included; plug in a durable one when you need it.

## Install

Install the core package and one package per provider you use.

```bash
dotnet add package UniversalNotifier.NET
dotnet add package UniversalNotifier.NET.Email.Smtp
```

| Channel | Package | Provider |
|---|---|---|
| Email | `UniversalNotifier.NET.Email.Smtp` | any SMTP server (MailKit) |
| Email | `UniversalNotifier.NET.Email.Mailgun` | Mailgun (US and EU) |
| SMS | `UniversalNotifier.NET.Sms.Twilio` | Twilio |
| Mobile push | `UniversalNotifier.NET.Push.Fcm` | Firebase Cloud Messaging |
| Browser push | `UniversalNotifier.NET.WebPush` | Web Push / VAPID |
| Telegram | `UniversalNotifier.NET.Telegram` | Telegram bots |
| Background delivery | `UniversalNotifier.NET.Background` | in-memory queue and worker |
| Tests | `UniversalNotifier.NET.Testing` | fake notifiers |

Targets .NET 10 (LTS) and .NET 11. .NET 8 reached end of support on 10 November 2026.

## Five-minute quick start (ASP.NET Core, Worker Service, any host)

```csharp
// Program.cs
builder.Services.AddUniversalNotifier(n => n
    .AddSmtp(o =>
    {
        o.Host = "smtp.example.com";
        o.UserName = builder.Configuration["Smtp:User"];
        o.Password = builder.Configuration["Smtp:Password"];   // user secrets / environment variables / a vault
        o.From = new EmailAddress("noreply@example.com", "Example");
    })
    .AddTwilioSms(o =>
    {
        o.AccountSid = builder.Configuration["Twilio:AccountSid"]!;
        o.AuthToken = builder.Configuration["Twilio:AuthToken"];
        o.From = "+15005550006";
    }));
```

```csharp
// anywhere: inject INotificationService
app.MapPost("/signup", async (INotificationService notifications) =>
{
    var result = await notifications.SendAsync(
        Notification.To(new Recipient { Email = "ada@example.com", Phone = "+14165551234" })
            .Email("Welcome, {{Name}}!", html: "<p>Hi <b>{{Name}}</b></p>", text: "Hi {{Name}}")
            .Sms("Hi {{Name}}, welcome aboard!")
            .WithData(new { Name = "Ada" }));

    return result.Success ? Results.Ok() : Results.Problem("Could not send the welcome message.");
});
```

Each channel in the result has its own outcome: `result[NotificationChannel.Email]`, `result.Channels`. The provider extension methods (`AddSmtp`, `AddMailgun`, ...) live in the `Microsoft.Extensions.DependencyInjection` namespace, which ASP.NET Core and the Worker template import for you.

Settings can come from configuration like any options type:

```csharp
builder.Services.Configure<SmtpOptions>("smtp", builder.Configuration.GetSection("Notifications:Smtp"));
builder.Services.AddUniversalNotifier(n => n.AddSmtp());   // options are validated at startup
```

### Without a host or dependency injection

```csharp
using Microsoft.Extensions.DependencyInjection;   // for AddSmtp, AddMailgun, ...
using UniversalNotifier.NET;

await using var notifier = new NotifierBuilder()
    .AddMailgun(o => { o.ApiKey = key; o.Domain = "mg.example.com"; o.From = "noreply@mg.example.com"; })
    .Build();

await notifier.Email.SendAsync(EmailMessage.Create("ada@example.com", "Hello", "Plain text body"));
```

## Several channels, several providers

A notification can target any channel the recipient has an address for. Missing addresses and missing providers are reported per channel and never block the others.

```csharp
var ada = new Recipient
{
    Email = "ada@example.com",
    Phone = "+14165551234",
    DeviceTokens = ["fcm-registration-token"],
    TelegramChatId = "123456789",
    WebPushSubscriptions = [new WebPushSubscription(endpoint, p256dh, auth)],
};

await notifications.SendAsync(
    Notification.To(ada)
        .Email().Sms().Push().Telegram().WebPush()   // content comes from the template
        .UseTemplate("appointment-reminder")
        .WithData(new { Name = "Ada", Time = "3 pm" }));
```

Register more than one provider for a channel and pick one per call:

```csharp
n.AddMailgun(o => ...)                               // the default email provider (the last unnamed one wins)
 .AddSmtp(o => ..., name: "backup")                  // only used when asked for
// ...
Notification.To(ada).Email("Hi", text: "...", provider: "backup")
```

## Templates

Write `{{Token}}` anywhere in a subject or body. Nested values (`{{User.Name}}`) work, HTML bodies are encoded automatically (use `{{{Raw}}}` to opt out), values are never re-scanned for tokens, and a missing token is an error instead of a silently broken email.

```csharp
n.AddTemplate("welcome", NotificationChannel.Email, new ChannelContent
{
    Subject = "Welcome, {{Name}}!",
    Html = "<h1>Welcome, {{Name}}</h1>",
    Text = "Welcome, {{Name}}",
});
n.AddTemplate("welcome", NotificationChannel.Sms, new ChannelContent { Text = "Welcome, {{Name}}!" });
n.AddTemplatesFromDirectory("templates");   // templates/welcome/email.subject.txt, email.html, sms.text.txt, ...
```

The built-in renderer is intentionally small: no expressions, loops or code. To use Scriban, Fluid or anything else, implement `ITemplateRenderer` and call `UseTemplateRenderer<T>()`. To load templates from a database, implement `ITemplateStore`.

## Results and errors

`SendAsync` does not throw for delivery problems. Every result carries classified errors:

| Kind | Meaning | Retried automatically? |
|---|---|---|
| `Validation` | The message is invalid (bad address, missing subject). | No |
| `Configuration` | Credentials, region or provider setup is wrong. | No |
| `Permanent` | The provider refused it for good (unknown number, unregistered device). | No |
| `Transient` | Not accepted, safe to repeat (rate limit, service unavailable). | Yes, when retries are on |
| `Unknown` | Timeout or dropped connection. The provider may have processed it. | Only if you opt in (can duplicate) |

Bulk sends report **every recipient separately** (`response.Recipients`), so one bad address or one expired device token does not hide the rest, and you learn exactly which tokens to remove.

Turn on retries when you want them:

```csharp
n.WithRetries(maxAttempts: 3);   // exponential backoff with jitter; honors Retry-After; never after a partial success
```

See [Delivery guarantees](docs/delivery-guarantees.md) for exactly what "accepted" means per provider.

## Background delivery

Return to your caller immediately and deliver from a hosted worker:

```csharp
builder.Services.AddUniversalNotifier(n => n
    .AddSmtp(o => ...)
    .AddBackgroundDelivery(o => { o.Capacity = 1000; o.MaxConcurrency = 8; }));

// anywhere:
var queued = await queue.EnqueueAsync(notification);   // INotificationQueue; false means the queue refused it
```

The queue is bounded: a full queue slows producers down (or rejects, if you choose) instead of growing memory. Shutdown drains what is queued until the host's timeout. The in-memory queue loses queued notifications if the process crashes, so for guaranteed delivery implement `INotificationQueue` over your database or broker (`NotificationJson` serializes a notification) and register it. Register an `INotificationResultHandler` to store results, alert on failures or dead-letter.

## Testing your code

```csharp
services.AddUniversalNotifier(n => n.AddFakeNotifiers());
// ...
var email = provider.GetRequiredService<FakeEmailNotifier>().Sent.Single();
```

The fakes validate messages exactly like real providers, then record them.

## Security and privacy by default

- API keys, tokens and passwords are never logged and never appear in `ToString()`.
- Message content and recipient addresses are never logged. Models print only their type and id.
- Bulk email and SMS go out as individual messages, so recipients never see each other.
- Display names are quoted and line breaks in headers are rejected, so user data cannot inject recipients or headers.
- Telegram and Web Push use their own HTTP client because their URLs contain secrets that `IHttpClientFactory` logging would otherwise write to your logs.
- SMTP credentials are refused over an unencrypted connection unless you explicitly allow it.

## Write your own provider

Derive from `NotifierBase<TMessage>`, implement one method, register it. Validation, error handling, cancellation, logging and retries come with the base class. See [Custom providers and channels](docs/custom-provider.md).

## Performance

Measured with BenchmarkDotNet on one developer machine (short job; treat as order of magnitude, and run `benchmarks/` yourself):

| Operation | Time | Allocated |
|---|---|---|
| Dispatch one channel (render + validate + send to a no-op provider) | ~1.1 µs | 928 B |
| Dispatch three channels concurrently | ~2.7 µs | 2.3 KB |
| Render a 5-token template | 200-315 ns | 208-240 B |
| Queue 1000 notifications and read them back | ~60 µs | 17 KB |

## Documentation

- [Architecture](docs/architecture.md)
- [Delivery guarantees](docs/delivery-guarantees.md)
- [Custom providers and channels](docs/custom-provider.md)
- [Migrating from 1.x](docs/migration-from-1.x.md)
- [Contributing](CONTRIBUTING.md) and [Changelog](CHANGELOG.md)

## License

MIT. See [LICENSE](LICENSE).
