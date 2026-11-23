# Live testing

The unit and contract tests use stubs and an in-process SMTP server, so they never touch a real service. `tests/UniversalNotifier.NET.LiveTests` sends one real message per provider so you can confirm each integration against the real thing.

Every test is opt-in: it runs only when its environment variables are set and is reported as skipped otherwise. Running the project with nothing set is safe.

```bash
# set the variables for the providers you have, then:
dotnet test tests/UniversalNotifier.NET.LiveTests --filter "Category=Live" --logger "console;verbosity=normal"
```

Each test asserts the provider **accepted** the message (see [delivery guarantees](delivery-guarantees.md)) and prints the provider's message id. Check the destination to confirm it arrived.

| Test | Variables |
|---|---|
| SMTP | `LIVE_SMTP_HOST`, `LIVE_EMAIL_FROM`, `LIVE_EMAIL_TO`; optional `LIVE_SMTP_PORT` (587), `LIVE_SMTP_SECURITY` (`StartTls`, `SslOnConnect`, `None`), `LIVE_SMTP_USER`, `LIVE_SMTP_PASSWORD` |
| Mailgun | `LIVE_MAILGUN_KEY`, `LIVE_MAILGUN_DOMAIN`, `LIVE_EMAIL_FROM`, `LIVE_EMAIL_TO`; optional `LIVE_MAILGUN_REGION` (`eu`) |
| SendGrid | `LIVE_SENDGRID_KEY`, `LIVE_EMAIL_FROM`, `LIVE_EMAIL_TO` |
| Twilio SMS | `LIVE_TWILIO_SID`, `LIVE_TWILIO_TOKEN`, `LIVE_TWILIO_FROM`, `LIVE_SMS_TO` |
| Twilio WhatsApp | `LIVE_TWILIO_SID`, `LIVE_TWILIO_TOKEN`, `LIVE_WHATSAPP_TO`; optional `LIVE_WHATSAPP_FROM` (defaults to Twilio's sandbox number `+14155238886`) |
| FCM push | `LIVE_FCM_CREDENTIAL_PATH` (service account JSON file), `LIVE_FCM_TOKEN` (a real device registration token) |
| Telegram | `LIVE_TELEGRAM_TOKEN`, `LIVE_TELEGRAM_CHAT` (message the bot once, then read your chat id from `getUpdates`) |
| Web Push | `LIVE_WEBPUSH_SUBSCRIPTION_JSON` (the browser's `subscription.toJSON()`), `LIVE_VAPID_PUBLIC`, `LIVE_VAPID_PRIVATE`, `LIVE_VAPID_SUBJECT` (generate keys with `VapidKeys.Generate()`) |
| Slack | `LIVE_SLACK_WEBHOOK` |
| Teams | `LIVE_TEAMS_WEBHOOK` (a Workflows webhook URL) |
| Dispatcher | the SMTP and Telegram variables together |

## Sandbox tests (nothing reaches a real person)

These use each platform's own safe-testing mode. They are the ones to run first, and the ones to run in CI against a test account.

| Platform | What the test uses | Variables |
|---|---|---|
| Mailgun | `TestMode = true` (`o:testmode`): accepted and processed, not delivered | the Mailgun variables above |
| SendGrid | `SandboxMode = true`: the request is validated, nothing is sent | the SendGrid variables above |
| Twilio SMS | test credentials and magic numbers: never sends or bills; `+15005550006` is the valid sender, `+15005550001` an invalid recipient | `LIVE_TWILIO_TEST_SID`, `LIVE_TWILIO_TEST_TOKEN`; optional `LIVE_TWILIO_TEST_TO` |
| Twilio WhatsApp | the WhatsApp sandbox (shared number `+14155238886`); the recipient must first join the sandbox by sending Twilio's join code from their WhatsApp | `LIVE_TWILIO_SID`, `LIVE_TWILIO_TOKEN`, `LIVE_WHATSAPP_TO`; optional `LIVE_WHATSAPP_FROM` |
| FCM | `DryRun = true`: FCM validates the message and the token, nothing reaches the device | the FCM variables above |
| Telegram | `UseTestEnvironment = true`: Telegram's separate test servers and test accounts | `LIVE_TELEGRAM_TEST_TOKEN`, `LIVE_TELEGRAM_TEST_CHAT` |
| SMTP | a local Mailpit or smtp4dev server | the SMTP variables with `LIVE_SMTP_HOST=localhost`, `LIVE_SMTP_PORT=1025`, `LIVE_SMTP_SECURITY=None` |
| Web Push | no sandbox exists; use a subscription from a browser you control | the Web Push variables above |
| Slack, Teams | no sandbox exists; use a private test channel | the webhook variables above |

Telegram's test environment needs its own account and bot: log in to a Telegram test server (the "test account" option in the apps), message @BotFather there to create a bot, and use that bot's token. Tokens from the normal environment do not work in it.

Two things to know:

- **WhatsApp:** free-form text only reaches a user inside WhatsApp's 24-hour window or the Twilio sandbox. In production use an approved template: `WhatsAppMessage.FromTemplate(number, contentSid, variables)`.
- **SendGrid** answers a sandbox request with 200 (not 202); both count as accepted.

## Free ways to test

- **SMTP:** run [Mailpit](https://github.com/axllent/mailpit) or smtp4dev locally (`LIVE_SMTP_HOST=localhost`, `LIVE_SMTP_PORT=1025`, `LIVE_SMTP_SECURITY=None`).
- **Mailgun:** a sandbox domain, sending only to authorized recipients.
- **Twilio:** your trial account, or the magic test numbers with the test credentials.
- **Telegram:** a bot from @BotFather and your own chat.
- **Slack and Teams:** a private test channel and an incoming webhook or workflow.

Credentials stay in your environment (or `dotnet user-secrets` exported to variables); nothing here writes them to disk or logs. If the Teams test fails, the Adaptive Card payload shape is the first thing to check, because it is the one format verified from documentation only.
