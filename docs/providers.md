# Provider setup

For each provider: what to create, the minimal registration and the things that usually go wrong. Every registration also accepts `name:` for a second account.

## SMTP (`UniversalNotifier.NET.Email.Smtp`)

```csharp
n.AddSmtp(o => { o.Host = "smtp.example.com"; o.Port = 587; o.UserName = "..."; o.Password = "..."; o.From = "noreply@example.com"; });
```

- Port 587 with `Security = StartTls` (default) or port 465 with `SslOnConnect`.
- Credentials over `Security = None` are refused unless `AllowInsecureAuthentication` is set; use it only for a local test server such as Mailpit.
- One connection per send call; several `To` recipients are sent as individual messages with per-recipient results. Server reply text is not surfaced (it echoes addresses).

## Mailgun (`UniversalNotifier.NET.Email.Mailgun`)

```csharp
n.AddMailgun(o => { o.ApiKey = "..."; o.Domain = "mg.example.com"; o.Region = MailgunRegion.Eu; o.From = "noreply@mg.example.com"; });
```

- Use the **private API key** and the **sending domain**. EU domains need `Region = Eu`, otherwise requests fail with an auth error.
- A sandbox domain only sends to authorized recipients.
- At most 3 tags per message; bulk uses batch sending (up to 1000 per request).

## SendGrid (`UniversalNotifier.NET.Email.SendGrid`)

```csharp
n.AddSendGrid(o => { o.ApiKey = "SG...."; o.From = "noreply@example.com"; });
```

- Create an API key with the **Mail Send** permission, and verify the sender or the domain; unverified senders are rejected.
- At most 10 categories (tags); each recipient is its own personalization.

## Twilio SMS and WhatsApp (`UniversalNotifier.NET.Twilio`)

```csharp
n.AddTwilio(o => { o.AccountSid = "AC..."; o.AuthToken = "..."; o.From = "+15005550006"; });
// or an API key: o.ApiKeySid = "SK..."; o.ApiKeySecret = "..."; (preferred: it can be revoked on its own)
```

- `AddTwilio` registers both SMS and WhatsApp. Pass `channels: TwilioChannels.Sms` or `TwilioChannels.WhatsApp` to register one only, so Twilio does not become the default of a channel another provider should serve.
- `From` is the SMS sender and `WhatsAppFrom` the WhatsApp sender; set whichever you use. To try WhatsApp, use Twilio's sandbox number `+14155238886` and have the recipient join the sandbox first.
- WhatsApp free-form text (`.WhatsApp("...")` or `WhatsAppMessage.Create`) only reaches users inside WhatsApp's 24-hour window. For everyone else send an approved template through `IWhatsAppNotifier`: `WhatsAppMessage.FromTemplate("+1416...", "HX...", new Dictionary<string, string> { ["1"] = "Ada" })`. WhatsApp uses the recipient's `Phone`.
- `From` may be a Twilio number, an alphanumeric sender id (where allowed) or a Messaging Service SID (`MG...`).
- Numbers must be E.164 (`+14165551234`). Trial accounts only reach verified numbers.
- Twilio error text repeats phone numbers, so only the error code and docs link are surfaced.

## Firebase Cloud Messaging (`UniversalNotifier.NET.Push.Fcm`)

```csharp
n.AddFcm(o => o.CredentialPath = "service-account.json");   // or CredentialJson, or application default credentials
```

- Create a service account key in the Firebase console (Project settings, Service accounts).
- `PushMessage.Tokens` are registration tokens from the app. `fcm.unregistered` means the app was uninstalled or the token expired; delete it.

## Telegram (`UniversalNotifier.NET.Telegram`)

```csharp
n.AddTelegram(o => o.BotToken = "123456:ABC...");
```

- Create a bot with @BotFather. A user must message the bot first; read their chat id from `getUpdates`. `telegram.blocked` means the user blocked the bot.
- Text only, 4096 characters. `TelegramParseMode.Html` or `MarkdownV2` for formatting (you must escape the text for that format).

## Web Push (`UniversalNotifier.NET.WebPush`)

```csharp
var keys = VapidKeys.Generate();   // once; store the private key as a secret and reuse it
n.AddWebPush(o => { o.PublicKey = keys.PublicKey; o.PrivateKey = keys.PrivateKey; o.Subject = "mailto:ops@example.com"; });
```

- Give the public key to the browser as `applicationServerKey`; store each `subscription.toJSON()` (endpoint, `p256dh`, `auth`) per user.
- `webpush.gone` (404/410) means the subscription expired; delete it. Payloads are limited to 3993 bytes.
- Changing the VAPID keys invalidates every existing subscription.

## Slack, Teams and webhooks (`UniversalNotifier.NET.Webhook`)

```csharp
n.AddWebhook(o => o
    .Add("ops", slackWebhookUrl, WebhookFormat.Slack)
    .Add("oncall", teamsWorkflowUrl, WebhookFormat.Teams)
    .Add("audit", "https://example.com/hooks/audit", WebhookFormat.Json, signingSecret: "..."));
```

- URLs must be https and are secrets; messages name the endpoint (`Recipient.WebhookEndpoint = "ops"`).
- Teams uses a Workflows webhook receiving an Adaptive Card message; that payload shape has not been verified against a live Teams tenant yet.
- A `signingSecret` adds `X-Signature-256: sha256=<HMAC of the body>` for your receiver to verify.

## In-app (`UniversalNotifier.NET.InApp`)

```csharp
n.AddInApp();                       // in memory; register your own IInAppStore first to persist
Notification.To(new Recipient { UserId = "u-42" }).InApp("Order shipped", "Order 1001 is on its way.");
```

- Read inboxes from `IInAppStore`: `ListAsync`, `CountUnreadAsync`, `MarkReadAsync`, `MarkAllReadAsync`.

## Testing each one for real

See [live testing](live-testing.md) for the environment variables and free ways to try every provider.
