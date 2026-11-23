using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UniversalNotifier.NET.Receipts;

/// <summary>
/// Verifies and parses SendGrid Signed Event Webhook calls. SendGrid signs the timestamp header followed by the raw body with an
/// ECDSA (P-256, SHA-256) private key; give this class the matching <b>verification key</b> from the SendGrid webhook settings
/// (a base64 public key). Turn on "Signature Verification" there, otherwise the headers are missing and every call is rejected.
/// A call whose timestamp is more than five minutes off is rejected so a captured request cannot be replayed.
/// </summary>
public sealed class SendGridDeliveryWebhook : IDeliveryWebhook
{
    /// <summary>The provider name.</summary>
    public const string Name = "sendgrid";

    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private readonly byte[] _publicKey;
    private readonly TimeProvider _time;

    /// <summary>Creates the webhook with the system clock.</summary>
    /// <exception cref="ArgumentException">The key is not a valid base64 ECDSA public key.</exception>
    public SendGridDeliveryWebhook(string verificationKey)
        : this(verificationKey, TimeProvider.System)
    {
    }

    /// <summary>Creates the webhook with a given clock (for tests).</summary>
    /// <exception cref="ArgumentException">The key is not a valid base64 ECDSA public key.</exception>
    public SendGridDeliveryWebhook(string verificationKey, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationKey);
        ArgumentNullException.ThrowIfNull(timeProvider);
        try
        {
            _publicKey = Convert.FromBase64String(verificationKey);
            using var probe = ECDsa.Create();
            probe.ImportSubjectPublicKeyInfo(_publicKey, out _);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new ArgumentException("The SendGrid verification key must be a base64 ECDSA public key.", nameof(verificationKey));
        }

        _time = timeProvider;
    }

    /// <inheritdoc />
    public string Provider => Name;

    /// <inheritdoc />
    public ReceiptResult Process(WebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsAuthentic(request))
        {
            return ReceiptResult.Rejected;
        }

        try
        {
            using var json = JsonDocument.Parse(request.Body);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new ReceiptResult(true, []);
            }

            var events = new List<DeliveryEvent>();
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    events.Add(Map(item));
                }
            }

            return new ReceiptResult(true, events);
        }
        catch (JsonException)
        {
            // Authentic but unreadable: nothing to report.
            return new ReceiptResult(true, []);
        }
    }

    private bool IsAuthentic(WebhookRequest request)
    {
        if (request.Header("X-Twilio-Email-Event-Webhook-Signature") is not { } signature
            || request.Header("X-Twilio-Email-Event-Webhook-Timestamp") is not { } timestamp
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        if ((_time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return false;
        }

        try
        {
            var signed = new byte[Encoding.UTF8.GetByteCount(timestamp) + request.Body.Length];
            var written = Encoding.UTF8.GetBytes(timestamp, signed);
            request.Body.Span.CopyTo(signed.AsSpan(written));

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(_publicKey, out _);
            return key.VerifyData(signed, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private DeliveryEvent Map(JsonElement item)
    {
        var outcome = Text(item, "event") switch
        {
            "delivered" => DeliveryOutcome.Delivered,
            "bounce" or "dropped" or "blocked" => DeliveryOutcome.Failed,
            "deferred" => DeliveryOutcome.Deferred,
            "spamreport" or "spam report" => DeliveryOutcome.Complained,
            "unsubscribe" or "group_unsubscribe" => DeliveryOutcome.Unsubscribed,
            "open" => DeliveryOutcome.Opened,
            "click" => DeliveryOutcome.Clicked,
            _ => DeliveryOutcome.Other,
        };

        // sg_message_id is the id the send returned (X-Message-Id) followed by ".filter..."; the send's id is what callers have.
        var messageId = Text(item, "sg_message_id");
        if (messageId is not null && messageId.IndexOf(".filter", StringComparison.Ordinal) is var cut and > 0)
        {
            messageId = messageId[..cut];
        }

        var occurred = item.TryGetProperty("timestamp", out var stamp) && stamp.ValueKind == JsonValueKind.Number && stamp.TryGetInt64(out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : _time.GetUtcNow();

        // The status code ("5.1.1") or the bounce type, never the free-text reason, which quotes the address and the server's reply.
        return new DeliveryEvent(Name, messageId, outcome, occurred, Text(item, "email"), Text(item, "status") ?? Text(item, "type"));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
