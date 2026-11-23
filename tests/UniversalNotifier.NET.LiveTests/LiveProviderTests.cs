using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UniversalNotifier.NET.Email.Mailgun;
using UniversalNotifier.NET.Email.SendGrid;
using UniversalNotifier.NET.Email.Smtp;
using UniversalNotifier.NET.Push.Fcm;
using UniversalNotifier.NET.Twilio;
using UniversalNotifier.NET.Telegram;
using UniversalNotifier.NET.Webhook;
using UniversalNotifier.NET.WebPush;

namespace UniversalNotifier.NET.LiveTests;

/// <summary>
/// Sends one real message through each provider. They are opt-in: a test runs only when its environment variables are set
/// and is reported as skipped otherwise, so this project is safe to run anywhere. See docs/live-testing.md for the variables.
/// Each test asserts the provider accepted the message; check the destination to confirm it arrived.
/// </summary>
[TestFixture]
[Category("Live")]
public class LiveProviderTests
{
    private static string Require(params string[] names)
    {
        var values = names.Select(n => Environment.GetEnvironmentVariable(n)).ToArray();
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            Assert.Ignore("Set " + string.Join(", ", names) + " to run this live test.");
        }

        return string.Join('\n', values!);
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

    private static async Task AssertAccepted(NotifierResponse response)
    {
        await Task.CompletedTask;
        Assert.That(response.Success, Is.True, string.Join("; ", response.Errors));
        TestContext.Out.WriteLine($"Accepted. Provider id: {response.ProviderMessageId ?? "(none)"}  HTTP: {response.HttpStatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
    }

    private static EmailMessage LiveEmail(string provider) =>
        EmailMessage.Create(Env("LIVE_EMAIL_TO"), $"UniversalNotifier.NET live test ({provider})", $"Sent by the {provider} live test.", $"<p>Sent by the <b>{provider}</b> live test.</p>");

    [Test]
    public async Task Smtp()
    {
        Require("LIVE_SMTP_HOST", "LIVE_EMAIL_FROM", "LIVE_EMAIL_TO");
        var security = Enum.TryParse<SmtpSecurity>(Env("LIVE_SMTP_SECURITY"), true, out var s) ? s : SmtpSecurity.StartTls;
        var notifier = new SmtpNotifier(
            new SmtpOptions
            {
                Host = Env("LIVE_SMTP_HOST"),
                Port = int.TryParse(Env("LIVE_SMTP_PORT"), out var port) ? port : 587,
                Security = security,
                UserName = string.IsNullOrEmpty(Env("LIVE_SMTP_USER")) ? null : Env("LIVE_SMTP_USER"),
                Password = string.IsNullOrEmpty(Env("LIVE_SMTP_PASSWORD")) ? null : Env("LIVE_SMTP_PASSWORD"),
                AllowInsecureAuthentication = security == SmtpSecurity.None,
                From = Env("LIVE_EMAIL_FROM"),
            },
            new NotifierContext());

        await AssertAccepted(await notifier.SendAsync(LiveEmail("SMTP")));
    }

    [Test]
    public async Task Mailgun()
    {
        Require("LIVE_MAILGUN_KEY", "LIVE_MAILGUN_DOMAIN", "LIVE_EMAIL_FROM", "LIVE_EMAIL_TO");
        using var http = new HttpClient();
        var notifier = new MailgunNotifier(
            new MailgunOptions
            {
                ApiKey = Env("LIVE_MAILGUN_KEY"),
                Domain = Env("LIVE_MAILGUN_DOMAIN"),
                Region = Env("LIVE_MAILGUN_REGION").Equals("eu", StringComparison.OrdinalIgnoreCase) ? MailgunRegion.Eu : MailgunRegion.Us,
                From = Env("LIVE_EMAIL_FROM"),
            },
            http,
            new NotifierContext());

        await AssertAccepted(await notifier.SendAsync(LiveEmail("Mailgun")));
    }

    [Test]
    public async Task SendGrid()
    {
        Require("LIVE_SENDGRID_KEY", "LIVE_EMAIL_FROM", "LIVE_EMAIL_TO");
        using var http = new HttpClient();
        var notifier = new SendGridNotifier(
            new SendGridOptions { ApiKey = Env("LIVE_SENDGRID_KEY"), From = Env("LIVE_EMAIL_FROM") },
            http,
            new NotifierContext());

        await AssertAccepted(await notifier.SendAsync(LiveEmail("SendGrid")));
    }

    [Test]
    public async Task Twilio_sms()
    {
        Require("LIVE_TWILIO_SID", "LIVE_TWILIO_TOKEN", "LIVE_TWILIO_FROM", "LIVE_SMS_TO");
        using var http = new HttpClient();
        var notifier = new TwilioSmsNotifier(
            new TwilioOptions { AccountSid = Env("LIVE_TWILIO_SID"), AuthToken = Env("LIVE_TWILIO_TOKEN"), From = Env("LIVE_TWILIO_FROM") },
            http,
            new NotifierContext());

        await AssertAccepted(await notifier.SendAsync(SmsMessage.Create(Env("LIVE_SMS_TO"), "UniversalNotifier.NET live test")));
    }

    [Test]
    public async Task Fcm_push()
    {
        Require("LIVE_FCM_CREDENTIAL_PATH", "LIVE_FCM_TOKEN");
        using var notifier = new FcmPushNotifier(new FcmPushOptions { CredentialPath = Env("LIVE_FCM_CREDENTIAL_PATH") }, new NotifierContext());

        await AssertAccepted(await notifier.SendAsync(PushMessage.Create(Env("LIVE_FCM_TOKEN"), "UniversalNotifier.NET", "Live test")));
    }

    [Test]
    public async Task Telegram()
    {
        Require("LIVE_TELEGRAM_TOKEN", "LIVE_TELEGRAM_CHAT");
        using var notifier = new TelegramNotifier(new TelegramOptions { BotToken = Env("LIVE_TELEGRAM_TOKEN") }, new NotifierContext());

        await AssertAccepted(await notifier.SendAsync(TelegramMessage.Create(Env("LIVE_TELEGRAM_CHAT"), "UniversalNotifier.NET live test")));
    }

    [Test]
    public async Task Web_push()
    {
        Require("LIVE_WEBPUSH_SUBSCRIPTION_JSON", "LIVE_VAPID_PUBLIC", "LIVE_VAPID_PRIVATE", "LIVE_VAPID_SUBJECT");
        using var subscription = JsonDocument.Parse(Env("LIVE_WEBPUSH_SUBSCRIPTION_JSON"));
        var root = subscription.RootElement;
        var keys = root.GetProperty("keys");
        using var notifier = new WebPushNotifier(
            new WebPushOptions { PublicKey = Env("LIVE_VAPID_PUBLIC"), PrivateKey = Env("LIVE_VAPID_PRIVATE"), Subject = Env("LIVE_VAPID_SUBJECT") },
            new NotifierContext());

        var message = new WebPushMessage
        {
            Subscriptions = [new WebPushSubscription(root.GetProperty("endpoint").GetString()!, keys.GetProperty("p256dh").GetString()!, keys.GetProperty("auth").GetString()!)],
            Title = "UniversalNotifier.NET",
            Body = "Live test",
        };

        await AssertAccepted(await notifier.SendAsync(message));
    }

    [TestCase("LIVE_SLACK_WEBHOOK", WebhookFormat.Slack)]
    [TestCase("LIVE_TEAMS_WEBHOOK", WebhookFormat.Teams)]
    public async Task Webhook(string variable, WebhookFormat format)
    {
        Require(variable);
        using var notifier = new WebhookNotifier(new WebhookOptions().Add("live", Env(variable), format), new NotifierContext());

        await AssertAccepted(await notifier.SendAsync(WebhookMessage.Create("live", "Live test message", "UniversalNotifier.NET")));
    }

    // ---- Sandbox variants: they use each platform's own safe-testing mode, so nothing reaches a real person. ----

    [Test]
    public async Task Mailgun_test_mode()
    {
        Require("LIVE_MAILGUN_KEY", "LIVE_MAILGUN_DOMAIN", "LIVE_EMAIL_FROM", "LIVE_EMAIL_TO");
        using var http = new HttpClient();
        var notifier = new MailgunNotifier(
            new MailgunOptions
            {
                ApiKey = Env("LIVE_MAILGUN_KEY"),
                Domain = Env("LIVE_MAILGUN_DOMAIN"),
                Region = Env("LIVE_MAILGUN_REGION").Equals("eu", StringComparison.OrdinalIgnoreCase) ? MailgunRegion.Eu : MailgunRegion.Us,
                From = Env("LIVE_EMAIL_FROM"),
                TestMode = true, // accepted and processed by Mailgun, never delivered
            },
            http,
            new NotifierContext());

        await AssertAccepted(await notifier.SendAsync(LiveEmail("Mailgun test mode")));
    }

    [Test]
    public async Task SendGrid_sandbox_mode()
    {
        Require("LIVE_SENDGRID_KEY", "LIVE_EMAIL_FROM", "LIVE_EMAIL_TO");
        using var http = new HttpClient();
        var notifier = new SendGridNotifier(
            new SendGridOptions { ApiKey = Env("LIVE_SENDGRID_KEY"), From = Env("LIVE_EMAIL_FROM"), SandboxMode = true }, // validated, never sent
            http,
            new NotifierContext());

        await AssertAccepted(await notifier.SendAsync(LiveEmail("SendGrid sandbox")));
    }

    [Test]
    public async Task Twilio_test_credentials()
    {
        // Twilio's test credentials (Console, Account, API keys and tokens, Test credentials) never send or bill.
        Require("LIVE_TWILIO_TEST_SID", "LIVE_TWILIO_TEST_TOKEN");
        using var http = new HttpClient();
        var notifier = new TwilioSmsNotifier(
            new TwilioOptions { AccountSid = Env("LIVE_TWILIO_TEST_SID"), AuthToken = Env("LIVE_TWILIO_TEST_TOKEN"), From = "+15005550006" }, // the magic "valid" sender
            http,
            new NotifierContext());

        var to = string.IsNullOrEmpty(Env("LIVE_TWILIO_TEST_TO")) ? "+14155551234" : Env("LIVE_TWILIO_TEST_TO");
        await AssertAccepted(await notifier.SendAsync(SmsMessage.Create(to, "UniversalNotifier.NET test credentials")));
    }

    [Test]
    public async Task Twilio_magic_number_for_an_invalid_recipient_is_a_permanent_failure()
    {
        Require("LIVE_TWILIO_TEST_SID", "LIVE_TWILIO_TEST_TOKEN");
        using var http = new HttpClient();
        var notifier = new TwilioSmsNotifier(
            new TwilioOptions { AccountSid = Env("LIVE_TWILIO_TEST_SID"), AuthToken = Env("LIVE_TWILIO_TEST_TOKEN"), From = "+15005550006" },
            http,
            new NotifierContext());

        var response = await notifier.SendAsync(SmsMessage.Create("+15005550001", "invalid number")); // Twilio's magic invalid number

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Permanent));
    }

    [Test]
    public async Task Twilio_whatsapp_sandbox()
    {
        // Join the sandbox first: from the recipient's WhatsApp, send the join code shown in Twilio Console, Messaging, Try it out.
        Require("LIVE_TWILIO_SID", "LIVE_TWILIO_TOKEN", "LIVE_WHATSAPP_TO");
        using var http = new HttpClient();
        var notifier = new TwilioWhatsAppNotifier(
            new TwilioOptions
            {
                AccountSid = Env("LIVE_TWILIO_SID"),
                AuthToken = Env("LIVE_TWILIO_TOKEN"),
                WhatsAppFrom = string.IsNullOrEmpty(Env("LIVE_WHATSAPP_FROM")) ? "+14155238886" : Env("LIVE_WHATSAPP_FROM"), // Twilio's sandbox number
            },
            http,
            new NotifierContext());

        await AssertAccepted(await notifier.SendAsync(WhatsAppMessage.Create(Env("LIVE_WHATSAPP_TO"), "UniversalNotifier.NET WhatsApp sandbox test")));
    }

    [Test]
    public async Task Fcm_dry_run()
    {
        Require("LIVE_FCM_CREDENTIAL_PATH", "LIVE_FCM_TOKEN");
        using var notifier = new FcmPushNotifier(new FcmPushOptions { CredentialPath = Env("LIVE_FCM_CREDENTIAL_PATH"), DryRun = true }, new NotifierContext());

        await AssertAccepted(await notifier.SendAsync(PushMessage.Create(Env("LIVE_FCM_TOKEN"), "UniversalNotifier.NET", "FCM dry run"))); // validated, never delivered
    }

    [Test]
    public async Task Telegram_test_environment()
    {
        // Create the account and the bot in Telegram's test environment (see docs/live-testing.md); normal bot tokens do not work there.
        Require("LIVE_TELEGRAM_TEST_TOKEN", "LIVE_TELEGRAM_TEST_CHAT");
        using var notifier = new TelegramNotifier(new TelegramOptions { BotToken = Env("LIVE_TELEGRAM_TEST_TOKEN"), UseTestEnvironment = true }, new NotifierContext());

        await AssertAccepted(await notifier.SendAsync(TelegramMessage.Create(Env("LIVE_TELEGRAM_TEST_CHAT"), "UniversalNotifier.NET test environment")));
    }

    [Test]
    public async Task The_dispatcher_sends_one_notification_to_every_configured_channel()
    {
        Require("LIVE_SMTP_HOST", "LIVE_EMAIL_FROM", "LIVE_EMAIL_TO", "LIVE_TELEGRAM_TOKEN", "LIVE_TELEGRAM_CHAT");
        await using var notifier = new NotifierBuilder()
            .AddSmtp(o =>
            {
                o.Host = Env("LIVE_SMTP_HOST");
                o.Port = int.TryParse(Env("LIVE_SMTP_PORT"), out var port) ? port : 587;
                o.Security = Enum.TryParse<SmtpSecurity>(Env("LIVE_SMTP_SECURITY"), true, out var s) ? s : SmtpSecurity.StartTls;
                o.AllowInsecureAuthentication = o.Security == SmtpSecurity.None;
                o.UserName = string.IsNullOrEmpty(Env("LIVE_SMTP_USER")) ? null : Env("LIVE_SMTP_USER");
                o.Password = string.IsNullOrEmpty(Env("LIVE_SMTP_PASSWORD")) ? null : Env("LIVE_SMTP_PASSWORD");
                o.From = Env("LIVE_EMAIL_FROM");
            })
            .AddTelegram(o => o.BotToken = Env("LIVE_TELEGRAM_TOKEN"))
            .Build();

        var result = await notifier.SendAsync(
            Notification.To(new Recipient { Email = Env("LIVE_EMAIL_TO"), TelegramChatId = Env("LIVE_TELEGRAM_CHAT") })
                .Email("Dispatcher live test for {{Name}}", text: "Hello {{Name}}")
                .Telegram("Hello {{Name}}, from the dispatcher live test")
                .WithData(new { Name = "tester" }));

        Assert.That(result.Success, Is.True, string.Join("; ", result.Channels.SelectMany(c => c.Response.Errors)));
    }
}
