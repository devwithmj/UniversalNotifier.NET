using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using UniversalNotifier.NET.Receipts;

namespace UniversalNotifier.NET.Tests;

internal static class SesWebhookTestExtensions
{
    // The stub handler completes synchronously, so waiting here cannot deadlock; production code uses ProcessAsync.
    public static ReceiptResult Process(this SesDeliveryWebhook webhook, WebhookRequest request) =>
        webhook.ProcessAsync(request, CancellationToken.None).AsTask().GetAwaiter().GetResult();
}

[TestFixture]
public class SesReceiptTests
{
    private const string Topic = "arn:aws:sns:us-east-1:123456789012:ses-events";
    private const string CertUrl = "https://sns.us-east-1.amazonaws.com/SimpleNotificationService-abc123.pem";
    private const string ConfirmUrl = "https://sns.us-east-1.amazonaws.com/?Action=ConfirmSubscription&Token=t";

    private static readonly RSA Key = RSA.Create(2048);
    private static readonly string CertificatePem = CreateCertificatePem(Key);

    private static string CreateCertificatePem(RSA key)
    {
        var request = new CertificateRequest("CN=sns.amazonaws.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.ExportCertificatePem();
    }

    private static StubHttpHandler AmazonStub() => new(r =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(r.Uri.AbsoluteUri == CertUrl ? CertificatePem : "confirmed") });

    // The string SNS signs, written out by hand from the SNS spec (not by the code under test).
    private static string NotificationToSign(string message, string messageId, string? subject, string timestamp, string topic) =>
        "Message\n" + message + "\nMessageId\n" + messageId + "\n"
        + (subject is null ? string.Empty : "Subject\n" + subject + "\n")
        + "Timestamp\n" + timestamp + "\nTopicArn\n" + topic + "\nType\nNotification\n";

    private static string ConfirmationToSign(string message, string messageId, string subscribeUrl, string timestamp, string token, string topic) =>
        "Message\n" + message + "\nMessageId\n" + messageId + "\nSubscribeURL\n" + subscribeUrl + "\nTimestamp\n" + timestamp
        + "\nToken\n" + token + "\nTopicArn\n" + topic + "\nType\nSubscriptionConfirmation\n";

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "SNS SignatureVersion 1 is RSA with SHA-1; the test signs the same way.")]
    private static string Sign(string toSign, string version) =>
        Convert.ToBase64String(Key.SignData(Encoding.UTF8.GetBytes(toSign), version == "2" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));

    private static WebhookRequest Envelope(Dictionary<string, string?> fields) =>
        new(new Uri("https://example.com/hooks/ses"), new Dictionary<string, string>(), JsonSerializer.SerializeToUtf8Bytes(fields));

    private static WebhookRequest Notification(string sesJson, string version = "2", string topic = Topic, string certUrl = CertUrl, string? signature = null, string? signedMessage = null)
    {
        const string Id = "11111111-2222-3333-4444-555555555555";
        const string Time = "2024-01-01T12:00:00.000Z";
        var toSign = NotificationToSign(signedMessage ?? sesJson, Id, null, Time, topic);
        return Envelope(new()
        {
            ["Type"] = "Notification",
            ["MessageId"] = Id,
            ["TopicArn"] = topic,
            ["Message"] = sesJson,
            ["Timestamp"] = Time,
            ["SignatureVersion"] = version,
            ["Signature"] = signature ?? Sign(toSign, version),
            ["SigningCertURL"] = certUrl,
        });
    }

    private static SesDeliveryWebhook Webhook(StubHttpHandler handler, bool confirm = true) =>
        new(Topic, new HttpClient(handler), confirm, TimeProvider.System);

    private const string Delivery = """{"eventType":"Delivery","mail":{"messageId":"ses-id-1","destination":["a@example.com","b@example.com"]},"delivery":{"timestamp":"2024-01-01T12:00:01.000Z","recipients":["a@example.com","b@example.com"],"smtpResponse":"250 OK for a@example.com"}}""";

    [Test]
    public void A_signed_delivery_event_becomes_one_event_per_recipient_and_the_certificate_is_downloaded_once()
    {
        var handler = AmazonStub();
        var webhook = Webhook(handler);

        var first = webhook.Process(Notification(Delivery));
        var second = webhook.Process(Notification(Delivery));

        Assert.That(first.Authentic, Is.True);
        Assert.That(first.Events.Select(e => (e.Recipient, e.Outcome, e.ProviderMessageId)), Is.EqualTo(new[]
        {
            ("a@example.com", DeliveryOutcome.Delivered, (string?)"ses-id-1"),
            ("b@example.com", DeliveryOutcome.Delivered, (string?)"ses-id-1"),
        }));
        Assert.That(first.Events[0].OccurredAt, Is.EqualTo(DateTimeOffset.Parse("2024-01-01T12:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.That(second.Authentic, Is.True);
        Assert.That(handler.Requests.Count(r => r.Uri.AbsoluteUri == CertUrl), Is.EqualTo(1), "the certificate is cached");
        Assert.That(first.Events[0].Reason, Is.Null, "never the smtpResponse, which quotes the address");
    }

    [Test]
    public void Signature_version_1_with_sha1_is_accepted_too()
    {
        Assert.That(Webhook(AmazonStub()).Process(Notification(Delivery, version: "1")).Authentic, Is.True);
    }

    [Test]
    public void Bounces_complaints_and_delays_map_to_outcomes_with_only_a_subtype_as_the_reason()
    {
        var webhook = Webhook(AmazonStub());

        ReceiptResult Send(string json) => webhook.Process(Notification(json));

        var permanent = Send("""{"notificationType":"Bounce","mail":{"messageId":"m1"},"bounce":{"bounceType":"Permanent","bounceSubType":"General","bouncedRecipients":[{"emailAddress":"x@example.com","diagnosticCode":"smtp; 550 5.1.1 user unknown x@example.com"}],"timestamp":"2024-01-01T12:00:00Z"}}""").Events.Single();
        Assert.That((permanent.Outcome, permanent.Recipient, permanent.Reason), Is.EqualTo((DeliveryOutcome.Failed, (string?)"x@example.com", (string?)"General")));

        var transient = Send("""{"eventType":"Bounce","mail":{"messageId":"m2"},"bounce":{"bounceType":"Transient","bounceSubType":"MailboxFull","bouncedRecipients":[{"emailAddress":"y@example.com"}]}}""").Events.Single();
        Assert.That(transient.Outcome, Is.EqualTo(DeliveryOutcome.Deferred));

        var complaint = Send("""{"eventType":"Complaint","mail":{"messageId":"m3"},"complaint":{"complaintFeedbackType":"abuse","complainedRecipients":[{"emailAddress":"z@example.com"}]}}""").Events.Single();
        Assert.That((complaint.Outcome, complaint.Reason), Is.EqualTo((DeliveryOutcome.Complained, (string?)"abuse")));

        var delay = Send("""{"eventType":"DeliveryDelay","mail":{"messageId":"m4"},"deliveryDelay":{"delayType":"MailboxFull","delayedRecipients":[{"emailAddress":"w@example.com"}]}}""").Events.Single();
        Assert.That(delay.Outcome, Is.EqualTo(DeliveryOutcome.Deferred));

        Assert.That(Send("""{"eventType":"Open","mail":{"messageId":"m5","destination":["o@example.com"]},"open":{}}""").Events.Single().Outcome, Is.EqualTo(DeliveryOutcome.Opened));
        Assert.That(Send("""{"eventType":"Click","mail":{"messageId":"m6","destination":["c@example.com"]},"click":{}}""").Events.Single().Outcome, Is.EqualTo(DeliveryOutcome.Clicked));
        Assert.That(Send("""{"eventType":"Send","mail":{"messageId":"m7"},"send":{}}""").Events.Single().Outcome, Is.EqualTo(DeliveryOutcome.Other));
        Assert.That(Send("""{"eventType":"SomethingNew","mail":{"messageId":"m8"}}""").Events, Is.Empty, "unknown event types are ignored, not errors");
    }

    [Test]
    public void A_validly_signed_message_from_somebody_elses_topic_is_rejected()
    {
        var other = "arn:aws:sns:us-east-1:999999999999:attacker-topic";

        Assert.That(Webhook(AmazonStub()).Process(Notification(Delivery, topic: other)).Authentic, Is.False);
    }

    [Test]
    public void Tampered_messages_bad_signatures_and_unknown_versions_are_rejected()
    {
        var webhook = Webhook(AmazonStub());
        var tampered = Delivery.Replace("ses-id-1", "ses-id-2", StringComparison.Ordinal);

        Assert.That(webhook.Process(Notification(tampered, signedMessage: Delivery)).Authentic, Is.False, "body changed after signing");
        Assert.That(webhook.Process(Notification(Delivery, signature: Convert.ToBase64String(new byte[256]))).Authentic, Is.False, "wrong signature");
        Assert.That(webhook.Process(Notification(Delivery, signature: "not base64!")).Authentic, Is.False);
        Assert.That(webhook.Process(Notification(Delivery, version: "3")).Authentic, Is.False, "unknown signature version");
        Assert.That(webhook.Process(new WebhookRequest(new Uri("https://example.com"), new Dictionary<string, string>(), "nope"u8.ToArray())).Authentic, Is.False);
        Assert.That(webhook.Process(Envelope(new() { ["Type"] = "Notification" })).Authentic, Is.False, "missing fields");
    }

    [Test]
    public void A_certificate_address_that_is_not_amazons_is_never_fetched()
    {
        var handler = AmazonStub();
        var webhook = Webhook(handler);

        foreach (var url in new[]
        {
            "https://evil.example.com/cert.pem",
            "http://sns.us-east-1.amazonaws.com/cert.pem",
            "https://sns.us-east-1.amazonaws.com.evil.example/cert.pem",
            "https://sns.us-east-1.amazonaws.com/cert.txt",
            "https://169.254.169.254/latest/meta-data/x.pem",
        })
        {
            Assert.That(webhook.Process(Notification(Delivery, certUrl: url)).Authentic, Is.False, url);
        }

        Assert.That(handler.Requests, Is.Empty, "no request may be made to an address a forged message chose");
    }

    [Test]
    public void The_subscription_confirmation_is_followed_once_for_a_genuine_request_and_never_for_a_forged_one()
    {
        const string Id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const string Time = "2024-01-01T12:00:00.000Z";

        WebhookRequest Confirmation(string subscribeUrl, string topic = Topic) => Envelope(new()
        {
            ["Type"] = "SubscriptionConfirmation",
            ["MessageId"] = Id,
            ["TopicArn"] = topic,
            ["Message"] = "You have chosen to subscribe",
            ["Timestamp"] = Time,
            ["Token"] = "t",
            ["SubscribeURL"] = subscribeUrl,
            ["SignatureVersion"] = "2",
            ["Signature"] = Sign(ConfirmationToSign("You have chosen to subscribe", Id, subscribeUrl, Time, "t", topic), "2"),
            ["SigningCertURL"] = CertUrl,
        });

        var handler = AmazonStub();
        var result = Webhook(handler).Process(Confirmation(ConfirmUrl));
        Assert.That(result.Authentic, Is.True);
        Assert.That(result.Events, Is.Empty);
        Assert.That(handler.Requests.Count(r => r.Uri.AbsoluteUri == ConfirmUrl), Is.EqualTo(1));

        var manual = AmazonStub();
        Assert.That(Webhook(manual, confirm: false).Process(Confirmation(ConfirmUrl)).Authentic, Is.True);
        Assert.That(manual.Requests.Any(r => r.Uri.AbsoluteUri == ConfirmUrl), Is.False, "automatic confirmation is off");

        var forged = AmazonStub();
        Assert.That(Webhook(forged).Process(Confirmation("https://evil.example.com/confirm")).Authentic, Is.False, "a confirmation link that is not Amazon's");
        Assert.That(forged.Requests.Any(r => r.Uri.Host == "evil.example.com"), Is.False);
        Assert.That(Webhook(AmazonStub()).Process(Confirmation(ConfirmUrl, topic: "arn:aws:sns:us-east-1:999999999999:other")).Authentic, Is.False);
    }

    [Test]
    public void A_certificate_download_failure_rejects_the_call_instead_of_throwing()
    {
        var down = new StubHttpHandler(_ => throw new HttpRequestException("network down"));

        Assert.That(Webhook(down).Process(Notification(Delivery)).Authentic, Is.False);
    }

    [Test]
    public void The_webhook_validates_its_arguments()
    {
        Assert.Throws<ArgumentException>(() => new SesDeliveryWebhook(" ", new HttpClient()));
        Assert.Throws<ArgumentNullException>(() => new SesDeliveryWebhook(Topic, null!));
        Assert.Throws<ArgumentNullException>(() => Webhook(AmazonStub()).Process(null!));
        Assert.That(new SesDeliveryWebhook(Topic, new HttpClient()).Provider, Is.EqualTo("ses"));
    }
}
