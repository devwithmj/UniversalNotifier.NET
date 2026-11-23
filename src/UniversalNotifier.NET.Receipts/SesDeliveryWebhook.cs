using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UniversalNotifier.NET.Receipts;

/// <summary>
/// Verifies and parses Amazon SES events delivered through an Amazon SNS HTTPS subscription (SES event publishing or
/// bounce/complaint/delivery notifications). Every SNS message is signed with an AWS certificate; this class checks that signature
/// and, because anyone can create an SNS topic and post validly signed messages, <b>also requires the message to come from your own topic</b>
/// (<c>topicArn</c>). It answers the one-time subscription confirmation for you.
/// </summary>
/// <remarks>
/// The first call that needs an AWS signing certificate downloads it (cached afterwards). SNS retries
/// deliveries for hours, so there is no replay window: make your handler idempotent (a status update is).
/// </remarks>
public sealed partial class SesDeliveryWebhook : IDeliveryWebhook
{
    /// <summary>The provider name (the same as the SES notifier).</summary>
    public const string Name = "ses";

    private readonly string _topicArn;
    private readonly HttpClient _http;
    private readonly bool _confirmSubscriptions;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Task<X509Certificate2>> _certificates = new(StringComparer.Ordinal);

    /// <summary>Creates the webhook for one SNS topic. Subscriptions are confirmed automatically.</summary>
    /// <param name="topicArn">The ARN of the SNS topic SES publishes to. Messages from any other topic are rejected.</param>
    /// <param name="httpClient">Used to download AWS's signing certificate and to confirm the subscription.</param>
    public SesDeliveryWebhook(string topicArn, HttpClient httpClient)
        : this(topicArn, httpClient, true, TimeProvider.System)
    {
    }

    /// <summary>Creates the webhook with explicit subscription handling and a clock (for tests).</summary>
    /// <param name="topicArn">The ARN of the SNS topic SES publishes to. Messages from any other topic are rejected.</param>
    /// <param name="httpClient">Used to download AWS's signing certificate and to confirm the subscription.</param>
    /// <param name="confirmSubscriptions">Whether to open the confirmation link of a genuine subscription request. Turn off to confirm in the AWS console instead.</param>
    /// <param name="timeProvider">The clock.</param>
    public SesDeliveryWebhook(string topicArn, HttpClient httpClient, bool confirmSubscriptions, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicArn);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _topicArn = topicArn;
        _http = httpClient;
        _confirmSubscriptions = confirmSubscriptions;
        _time = timeProvider;
    }

    /// <inheritdoc />
    public string Provider => Name;

    /// <inheritdoc />
    public async ValueTask<ReceiptResult> ProcessAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Text(root, "TopicArn") != _topicArn
                || !await IsAuthenticAsync(root, cancellationToken).ConfigureAwait(false))
            {
                return ReceiptResult.Rejected;
            }

            switch (Text(root, "Type"))
            {
                case "Notification":
                    return new ReceiptResult(true, Map(Text(root, "Message")));
                case "SubscriptionConfirmation" when _confirmSubscriptions:
                    await ConfirmAsync(Text(root, "SubscribeURL"), cancellationToken).ConfigureAwait(false);
                    return new ReceiptResult(true, []);
                default:
                    return new ReceiptResult(true, []);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or CryptographicException or HttpRequestException)
        {
            return ReceiptResult.Rejected;
        }
    }

    // SNS signs "Name\nValue\n" pairs in a fixed order that depends on the message type.
    private static string? StringToSign(JsonElement root)
    {
        string[] fields = Text(root, "Type") switch
        {
            "Notification" => ["Message", "MessageId", "Subject", "Timestamp", "TopicArn", "Type"],
            "SubscriptionConfirmation" or "UnsubscribeConfirmation" => ["Message", "MessageId", "SubscribeURL", "Timestamp", "Token", "TopicArn", "Type"],
            _ => [],
        };

        if (fields.Length == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var field in fields)
        {
            if (Text(root, field) is { } value)
            {
                text.Append(field).Append('\n').Append(value).Append('\n');
            }
            else if (field != "Subject")
            {
                return null; // every field except Subject is mandatory
            }
        }

        return text.ToString();
    }

    private async Task<bool> IsAuthenticAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var version = Text(root, "SignatureVersion");
        if (StringToSign(root) is not { } signed
            || Text(root, "Signature") is not { } signature
            || (version != "1" && version != "2")
            || Text(root, "SigningCertURL") is not { } certUrl
            || !IsAmazonUrl(certUrl, requirePem: true))
        {
            return false;
        }

        var certificate = await GetCertificateAsync(certUrl, cancellationToken).ConfigureAwait(false);
        using var rsa = certificate.GetRSAPublicKey();
        return rsa is not null && Verify(rsa, Encoding.UTF8.GetBytes(signed), Convert.FromBase64String(signature), version);
    }

    // SignatureVersion 1 (SHA-1) is what SNS uses unless the topic is switched to version 2; both are defined by SNS, so both are accepted.
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "SNS SignatureVersion 1 is defined as RSA with SHA-1; a keyed signature check against an AWS certificate, not a password hash.")]
    private static bool Verify(RSA rsa, byte[] data, byte[] signature, string? version) =>
        rsa.VerifyData(data, signature, version == "2" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);

    private async Task<X509Certificate2> GetCertificateAsync(string url, CancellationToken cancellationToken)
    {
        var download = _certificates.GetOrAdd(url, key => DownloadAsync(key, cancellationToken));
        try
        {
            return await download.ConfigureAwait(false);
        }
        catch
        {
            // A failed download must not be remembered, or one network blip would reject every later call.
            _certificates.TryRemove(new KeyValuePair<string, Task<X509Certificate2>>(url, download));
            throw;
        }
    }

    private async Task<X509Certificate2> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return X509Certificate2.CreateFromPem(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task ConfirmAsync(string? subscribeUrl, CancellationToken cancellationToken)
    {
        if (subscribeUrl is null || !IsAmazonUrl(subscribeUrl, requirePem: false))
        {
            throw new FormatException("The confirmation link is not an Amazon SNS address.");
        }

        using var response = await _http.GetAsync(subscribeUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // Only https links to sns.<region>.amazonaws.com(.cn) are followed, so a forged message cannot make the server fetch an arbitrary address.
    private static bool IsAmazonUrl(string value, bool requirePem) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && SnsHost().IsMatch(uri.Host)
        && (!requirePem || uri.AbsolutePath.EndsWith(".pem", StringComparison.Ordinal));

    [GeneratedRegex(@"^sns\.[a-z0-9-]+\.amazonaws\.com(\.cn)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SnsHost();

    private List<DeliveryEvent> Map(string? message)
    {
        var events = new List<DeliveryEvent>();
        if (message is null)
        {
            return events;
        }

        using var json = JsonDocument.Parse(message);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return events;
        }

        var kind = Text(root, "eventType") ?? Text(root, "notificationType");
        root.TryGetProperty("mail", out var mail);
        var messageId = mail.ValueKind == JsonValueKind.Object ? Text(mail, "messageId") : null;
        var occurred = _time.GetUtcNow();

        void Add(DeliveryOutcome outcome, string? recipient, string? reason, string? timestamp) =>
            events.Add(new DeliveryEvent(
                Name,
                messageId,
                outcome,
                DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : occurred,
                recipient,
                reason));

        switch (kind)
        {
            case "Delivery" when root.TryGetProperty("delivery", out var delivery):
                foreach (var recipient in Strings(delivery, "recipients"))
                {
                    Add(DeliveryOutcome.Delivered, recipient, null, Text(delivery, "timestamp"));
                }

                break;
            case "Bounce" when root.TryGetProperty("bounce", out var bounce):
                // Permanent bounces will not be retried; transient and undetermined ones may still be delivered.
                var outcome = Text(bounce, "bounceType") == "Permanent" ? DeliveryOutcome.Failed : DeliveryOutcome.Deferred;
                foreach (var recipient in Recipients(bounce, "bouncedRecipients"))
                {
                    Add(outcome, recipient, Text(bounce, "bounceSubType"), Text(bounce, "timestamp"));
                }

                break;
            case "Complaint" when root.TryGetProperty("complaint", out var complaint):
                foreach (var recipient in Recipients(complaint, "complainedRecipients"))
                {
                    Add(DeliveryOutcome.Complained, recipient, Text(complaint, "complaintFeedbackType"), Text(complaint, "timestamp"));
                }

                break;
            case "DeliveryDelay" when root.TryGetProperty("deliveryDelay", out var delay):
                foreach (var recipient in Recipients(delay, "delayedRecipients"))
                {
                    Add(DeliveryOutcome.Deferred, recipient, Text(delay, "delayType"), Text(delay, "timestamp"));
                }

                break;
            case "Reject":
                Add(DeliveryOutcome.Failed, null, "reject", null);
                break;
            case "Open" or "Click":
                Add(kind == "Open" ? DeliveryOutcome.Opened : DeliveryOutcome.Clicked, mail.ValueKind == JsonValueKind.Object ? Strings(mail, "destination").FirstOrDefault() : null, null, null);
                break;
            case "Send" or "Rendering Failure" or "Subscription":
                Add(DeliveryOutcome.Other, null, null, null);
                break;
            default:
                break;
        }

        return events;
    }

    private static IEnumerable<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!)
            : [];

    private static IEnumerable<string> Recipients(JsonElement element, string name) =>
        element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(i => Text(i, "emailAddress")).Where(a => a is not null).Select(a => a!)
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
