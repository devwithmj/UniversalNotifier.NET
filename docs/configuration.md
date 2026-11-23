# Configuration

Every provider has a plain options class. Set it in code, bind it from `appsettings.json`, or mix both. Options are **validated at startup** in a host (and when you call `Build()` without one), so a missing key fails when the app starts, not on the first send.

## In code

```csharp
builder.Services.AddUniversalNotifier(n => n
    .AddMailgun(o =>
    {
        o.ApiKey = builder.Configuration["Mailgun:ApiKey"]!;
        o.Domain = "mg.example.com";
        o.From = new EmailAddress("noreply@example.com", "Example");
    }));
```

## From appsettings.json

Options are named by the registration key (`"mailgun"`, `"smtp"`, `"twilio"`, `"fcm"`, `"telegram"`, `"webpush"`, `"sendgrid"`, `"webhook"`), so bind a section to that name and call the `Add` method without a delegate:

```json
{
  "Notifications": {
    "Smtp": {
      "Host": "smtp.example.com",
      "Port": 465,
      "Security": "SslOnConnect",
      "UserName": "mailer",
      "From": { "Address": "noreply@example.com", "Name": "Example" }
    },
    "Mailgun": {
      "Domain": "mg.example.com",
      "Region": "Eu",
      "From": { "Address": "noreply@mg.example.com" }
    }
  }
}
```

```csharp
builder.Services.Configure<SmtpOptions>("smtp", builder.Configuration.GetSection("Notifications:Smtp"));
builder.Services.Configure<MailgunOptions>("mailgun", builder.Configuration.GetSection("Notifications:Mailgun"));

builder.Services.AddUniversalNotifier(n => n.AddMailgun().AddSmtp(name: "backup"));
```

This binding is covered by a test (`Provider_options_bind_from_an_appsettings_json_section...`). Enum values are written by name; `EmailAddress` is an object with `Address` and `Name`.

## Secrets

Never put keys in `appsettings.json` in source control. Use user secrets in development and environment variables or a vault in production. ASP.NET Core reads them into the same configuration keys:

```bash
dotnet user-secrets set "Notifications:Mailgun:ApiKey" "key-..."
# production: environment variable Notifications__Mailgun__ApiKey
```

Options classes print without their secrets, and secrets never appear in logs, errors or telemetry.

## Several providers, several accounts

```csharp
n.AddMailgun(o => ...)                       // default email provider
 .AddSendGrid(o => ..., name: "marketing")   // used only when asked for
```

```csharp
Notification.To(user).Email("Offer", html: "...", provider: "marketing")
```

An unnamed registration becomes the channel's default (the last one wins) and can also be selected by its own name (`"mailgun"`). A named registration is only used when selected by that name.

## Shared settings

```csharp
n.WithRetries(maxAttempts: 3)                       // opt-in, only safe failures
 .WithRateLimit(permitsPerSecond: 10, burst: 5)     // calls wait their turn
 .ConfigureTemplates(o => o.MissingToken = MissingTokenBehavior.KeepToken)
 .AddBackgroundDelivery(o => { o.Capacity = 1000; o.MaxConcurrency = 8; o.DeduplicationWindow = TimeSpan.FromMinutes(5); });
```

## Good to know

- Provider options are read when the notifier is first created; changing configuration at runtime does not rebuild it. Restart to apply new settings.
- Without a host (`NotifierBuilder`), call `Build()` once at startup; it validates options and templates and throws if anything is wrong.
- In a host, validate templates at startup too: `TemplateValidator.ThrowIfInvalid(app.Services);`
