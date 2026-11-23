using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using UniversalNotifier.NET.Receipts;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class DeliveryReceiptTests
{
    private static readonly Uri CallbackUrl = new("https://mycompany.com/myapp.php?foo=1&bar=2");

    // Both signatures below were computed with an independent implementation (Python hmac) of the providers' documented algorithms.
    private const string TwilioFormBody = "Digits=1234&To=%2B18005551212&From=%2B14158675310&Caller=%2B14158675310&CallSid=CA1234567890ABCDE";
    private const string TwilioSignature = "GvWf1cFY/Q7PnoempGyD5oXAezc=";
    private const string MailgunSignature = "45b8d354e238e6cbc36e94643e634bc0f6b73b8ed11d50a0cbe90387a5da32c3";

    private static WebhookRequest Twilio(string body, string? signature, Uri? url = null) =>
        new(url ?? CallbackUrl, signature is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["x-twilio-signature"] = signature }, Encoding.UTF8.GetBytes(body));

    private static WebhookRequest Mailgun(string json) =>
        new(new Uri("https://example.com/hooks/mailgun"), new Dictionary<string, string>(), Encoding.UTF8.GetBytes(json));

    private static string MailgunBody(string eventData, string signature = MailgunSignature, string timestamp = "1700000000") =>
        "{\"signature\":{\"timestamp\":\"" + timestamp + "\",\"token\":\"abcdef\",\"signature\":\"" + signature + "\"},\"event-data\":{" + eventData + "}}";

    private static FakeTimeProvider Clock(long unixSeconds) => new(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

    [Test]
    public void Twilio_accepts_the_documented_signature_and_rejects_a_changed_body_url_or_missing_header()
    {
        var webhook = new TwilioDeliveryWebhook("12345");

        Assert.That(webhook.Process(Twilio(TwilioFormBody, TwilioSignature)).Authentic, Is.True);
        Assert.That(webhook.Process(Twilio(TwilioFormBody.Replace("1234", "9999", StringComparison.Ordinal), TwilioSignature)).Authentic, Is.False, "changed field");
        Assert.That(webhook.Process(Twilio(TwilioFormBody, TwilioSignature, new Uri("https://evil.example/myapp.php?foo=1&bar=2"))).Authentic, Is.False, "changed url");
        Assert.That(webhook.Process(Twilio(TwilioFormBody, null)).Authentic, Is.False, "no signature");
        Assert.That(webhook.Process(Twilio(TwilioFormBody, "not base64!")).Authentic, Is.False, "garbage signature");
        Assert.That(new TwilioDeliveryWebhook("wrong-token").Process(Twilio(TwilioFormBody, TwilioSignature)).Authentic, Is.False, "wrong token");
    }

    [Test]
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Twilio signs callbacks with HMAC-SHA1; the test signs the same way.")]
    public void Twilio_maps_status_callbacks_to_events_and_strips_the_whatsapp_prefix()
    {
        var clock = Clock(1_700_000_000);
        var webhook = new TwilioDeliveryWebhook("token", clock);

        ReceiptResult Send(string body)
        {
            var url = new Uri("https://example.com/twilio");
            var signed = new StringBuilder(url.AbsoluteUri);
            foreach (var pair in body.Split('&').Select(p => p.Split('=')).Select(p => (Key: p[0], Value: Uri.UnescapeDataString(p[1]))).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                signed.Append(pair.Key).Append(pair.Value);
            }

            var signature = Convert.ToBase64String(System.Security.Cryptography.HMACSHA1.HashData(Encoding.UTF8.GetBytes("token"), Encoding.UTF8.GetBytes(signed.ToString())));
            return webhook.Process(Twilio(body, signature, url));
        }

        var delivered = Send("MessageSid=SM1&MessageStatus=delivered&To=whatsapp%3A%2B14165551234").Events.Single();
        Assert.That(delivered, Is.EqualTo(new DeliveryEvent("twilio", "SM1", DeliveryOutcome.Delivered, clock.GetUtcNow(), "+14165551234", null)));

        var failed = Send("MessageSid=SM2&MessageStatus=undelivered&To=%2B14165551234&ErrorCode=30003").Events.Single();
        Assert.That(failed.Outcome, Is.EqualTo(DeliveryOutcome.Failed));
        Assert.That(failed.Reason, Is.EqualTo("30003"));

        Assert.That(Send("MessageSid=SM3&MessageStatus=read&To=%2B1").Events.Single().Outcome, Is.EqualTo(DeliveryOutcome.Read));
        Assert.That(Send("MessageSid=SM4&MessageStatus=queued&To=%2B1").Events.Single().Outcome, Is.EqualTo(DeliveryOutcome.Other));
        Assert.That(Send("Other=1").Events, Is.Empty, "a call without a message status reports nothing");
    }

    [Test]
    public void Mailgun_verifies_the_signature_and_maps_the_event()
    {
        var webhook = new MailgunDeliveryWebhook("key-secret", Clock(1_700_000_100));
        var body = MailgunBody("""
            "event":"delivered","timestamp":1700000050.5,"recipient":"ada@example.com",
            "message":{"headers":{"message-id":"20240101@mg.example.com"}},"delivery-status":{"code":250,"description":"OK for ada@example.com"}
            """);

        var result = webhook.Process(Mailgun(body));

        Assert.That(result.Authentic, Is.True);
        var e = result.Events.Single();
        Assert.That(e.Outcome, Is.EqualTo(DeliveryOutcome.Delivered));
        Assert.That(e.ProviderMessageId, Is.EqualTo("<20240101@mg.example.com>"), "matches the id the send returned, which has angle brackets");
        Assert.That(e.Recipient, Is.EqualTo("ada@example.com"));
        Assert.That(e.Reason, Is.EqualTo("250"), "only the code, never the description that can quote addresses");
        Assert.That(e.OccurredAt, Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_050_500)));
    }

    [Test]
    public void Mailgun_distinguishes_temporary_and_permanent_failures_and_other_events()
    {
        var webhook = new MailgunDeliveryWebhook("key-secret", Clock(1_700_000_000));

        DeliveryOutcome Outcome(string data) => webhook.Process(Mailgun(MailgunBody(data))).Events.Single().Outcome;

        Assert.That(Outcome("\"event\":\"failed\",\"severity\":\"temporary\""), Is.EqualTo(DeliveryOutcome.Deferred));
        Assert.That(Outcome("\"event\":\"failed\",\"severity\":\"permanent\""), Is.EqualTo(DeliveryOutcome.Failed));
        Assert.That(Outcome("\"event\":\"rejected\""), Is.EqualTo(DeliveryOutcome.Failed));
        Assert.That(Outcome("\"event\":\"complained\""), Is.EqualTo(DeliveryOutcome.Complained));
        Assert.That(Outcome("\"event\":\"unsubscribed\""), Is.EqualTo(DeliveryOutcome.Unsubscribed));
        Assert.That(Outcome("\"event\":\"opened\""), Is.EqualTo(DeliveryOutcome.Opened));
        Assert.That(Outcome("\"event\":\"clicked\""), Is.EqualTo(DeliveryOutcome.Clicked));
        Assert.That(Outcome("\"event\":\"accepted\""), Is.EqualTo(DeliveryOutcome.Other));
    }

    [Test]
    public void Mailgun_rejects_a_bad_signature_a_stale_timestamp_and_malformed_input()
    {
        var data = "\"event\":\"delivered\"";
        var fresh = new MailgunDeliveryWebhook("key-secret", Clock(1_700_000_000));

        Assert.That(fresh.Process(Mailgun(MailgunBody(data))).Authentic, Is.True);
        Assert.That(fresh.Process(Mailgun(MailgunBody(data, signature: new string('0', 64)))).Authentic, Is.False, "wrong signature");
        Assert.That(fresh.Process(Mailgun(MailgunBody(data, signature: "zz"))).Authentic, Is.False, "not hex");
        Assert.That(new MailgunDeliveryWebhook("other-key", Clock(1_700_000_000)).Process(Mailgun(MailgunBody(data))).Authentic, Is.False, "wrong key");
        Assert.That(new MailgunDeliveryWebhook("key-secret", Clock(1_700_000_000 + 600)).Process(Mailgun(MailgunBody(data))).Authentic, Is.False, "replayed ten minutes later");
        Assert.That(fresh.Process(Mailgun("not json")).Authentic, Is.False);
        Assert.That(fresh.Process(Mailgun("{}")).Authentic, Is.False);
        Assert.That(fresh.Process(Mailgun("""{"signature":{"timestamp":"x","token":"t","signature":"s"}}""")).Authentic, Is.False);
    }

    private static (string PublicKey, Func<string, string, string> Sign) SendGridKeys()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            (timestamp, body) => Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(timestamp + body), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
    }

    private static WebhookRequest SendGrid(string body, string? signature, string timestamp) =>
        new(new Uri("https://example.com/hooks/sendgrid"),
            signature is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["X-Twilio-Email-Event-Webhook-Signature"] = signature, ["X-Twilio-Email-Event-Webhook-Timestamp"] = timestamp },
            Encoding.UTF8.GetBytes(body));

    [Test]
    public void SendGrid_verifies_the_ecdsa_signature_and_maps_every_event_in_the_batch()
    {
        var (publicKey, sign) = SendGridKeys();
        var webhook = new SendGridDeliveryWebhook(publicKey, Clock(1_700_000_000));
        const string Body = """
            [{"email":"ada@example.com","timestamp":1699999990,"event":"delivered","sg_message_id":"abc123.filter0001.16648.5515E0B88.0"},
             {"email":"bob@example.com","timestamp":1699999991,"event":"bounce","type":"bounce","status":"5.1.1","reason":"550 bob@example.com does not exist","sg_message_id":"def456.filter0002.1.2.0"},
             {"email":"cy@example.com","timestamp":1699999992,"event":"deferred","sg_message_id":"ghi789.filter0003.1.2.0"},
             {"email":"di@example.com","timestamp":1699999993,"event":"spamreport","sg_message_id":"jkl.filter1"},
             {"email":"ed@example.com","timestamp":1699999994,"event":"open","sg_message_id":"mno.filter1"},
             {"email":"fy@example.com","timestamp":1699999995,"event":"processed","sg_message_id":"pqr.filter1"}]
            """;

        var result = webhook.Process(SendGrid(Body, sign("1700000000", Body), "1700000000"));

        Assert.That(result.Authentic, Is.True);
        Assert.That(result.Events.Select(e => e.Outcome), Is.EqualTo(new[]
        {
            DeliveryOutcome.Delivered, DeliveryOutcome.Failed, DeliveryOutcome.Deferred, DeliveryOutcome.Complained, DeliveryOutcome.Opened, DeliveryOutcome.Other,
        }));
        Assert.That(result.Events[0].ProviderMessageId, Is.EqualTo("abc123"), "the send id, without the .filter suffix");
        Assert.That(result.Events[0].OccurredAt, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1_699_999_990)));
        Assert.That(result.Events[1].Reason, Is.EqualTo("5.1.1"), "the status code, never the free-text reason that quotes the address");
        Assert.That(result.Events[1].Recipient, Is.EqualTo("bob@example.com"));
    }

    [Test]
    public void SendGrid_rejects_a_tampered_body_a_wrong_key_a_stale_timestamp_and_missing_headers()
    {
        var (publicKey, sign) = SendGridKeys();
        var other = SendGridKeys().PublicKey;
        const string Body = """[{"event":"delivered","sg_message_id":"a.filter1"}]""";
        var signature = sign("1700000000", Body);
        var webhook = new SendGridDeliveryWebhook(publicKey, Clock(1_700_000_000));

        Assert.That(webhook.Process(SendGrid(Body, signature, "1700000000")).Authentic, Is.True);
        Assert.That(webhook.Process(SendGrid(Body.Replace("delivered", "bounce", StringComparison.Ordinal), signature, "1700000000")).Authentic, Is.False, "tampered body");
        Assert.That(webhook.Process(SendGrid(Body, signature, "1700000001")).Authentic, Is.False, "the timestamp is part of the signed text");
        Assert.That(new SendGridDeliveryWebhook(other, Clock(1_700_000_000)).Process(SendGrid(Body, signature, "1700000000")).Authentic, Is.False, "wrong key");
        Assert.That(new SendGridDeliveryWebhook(publicKey, Clock(1_700_000_000 + 600)).Process(SendGrid(Body, signature, "1700000000")).Authentic, Is.False, "replayed later");
        Assert.That(webhook.Process(SendGrid(Body, null, "1700000000")).Authentic, Is.False, "signature verification is off at SendGrid");
        Assert.That(webhook.Process(SendGrid(Body, "not base64!", "1700000000")).Authentic, Is.False);
        Assert.That(webhook.Process(SendGrid(Body, signature, "abc")).Authentic, Is.False);
    }

    [Test]
    public void SendGrid_authentic_but_unreadable_bodies_report_nothing_and_a_bad_key_is_refused_up_front()
    {
        var (publicKey, sign) = SendGridKeys();
        var webhook = new SendGridDeliveryWebhook(publicKey, Clock(1_700_000_000));

        var notJson = webhook.Process(SendGrid("nope", sign("1700000000", "nope"), "1700000000"));
        var notArray = webhook.Process(SendGrid("{}", sign("1700000000", "{}"), "1700000000"));

        Assert.That(notJson, Is.EqualTo(new ReceiptResult(true, [])).Using<ReceiptResult>((a, b) => a.Authentic == b.Authentic && a.Events.Count == b.Events.Count));
        Assert.That(notArray.Authentic && notArray.Events.Count == 0, Is.True);
        Assert.Throws<ArgumentException>(() => new SendGridDeliveryWebhook("not a key"));
        Assert.Throws<ArgumentException>(() => new SendGridDeliveryWebhook(Convert.ToBase64String(new byte[] { 1, 2, 3 })));
        Assert.That(new SendGridDeliveryWebhook(publicKey).Provider, Is.EqualTo("sendgrid"));
    }

    [Test]
    public void Webhooks_validate_their_arguments()
    {
        Assert.Throws<ArgumentException>(() => new MailgunDeliveryWebhook(" "));
        Assert.Throws<ArgumentException>(() => new TwilioDeliveryWebhook(""));
        Assert.Throws<ArgumentNullException>(() => new MailgunDeliveryWebhook("k").Process(null!));
        Assert.That(new TwilioDeliveryWebhook("t").Provider, Is.EqualTo("twilio"));
        Assert.That(new MailgunDeliveryWebhook("k").Provider, Is.EqualTo("mailgun"));
    }
}
