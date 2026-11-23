using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace UniversalNotifier.NET.Receipts;

/// <summary>
/// Verifies and parses Twilio message status callbacks (SMS and WhatsApp). Twilio signs the full callback URL plus the
/// form fields with the account's Auth Token (<c>X-Twilio-Signature</c>), so <see cref="WebhookRequest.Url"/> must be the exact public
/// URL configured at Twilio: behind a proxy, rebuild it from the forwarded scheme and host.
/// </summary>
public sealed class TwilioDeliveryWebhook : IDeliveryWebhook
{
    /// <summary>The provider name (the same as the Twilio notifiers).</summary>
    public const string Name = "twilio";

    private readonly byte[] _key;
    private readonly TimeProvider _time;

    /// <summary>Creates the webhook with the system clock.</summary>
    public TwilioDeliveryWebhook(string authToken)
        : this(authToken, TimeProvider.System)
    {
    }

    /// <summary>Creates the webhook with a given clock (for tests).</summary>
    public TwilioDeliveryWebhook(string authToken, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authToken);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _key = Encoding.UTF8.GetBytes(authToken);
        _time = timeProvider;
    }

    /// <inheritdoc />
    public string Provider => Name;

    /// <inheritdoc />
    public ReceiptResult Process(WebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fields = ParseForm(request.Body.Span);
        if (!IsAuthentic(request, fields))
        {
            return ReceiptResult.Rejected;
        }

        var sid = Field(fields, "MessageSid") ?? Field(fields, "SmsSid");
        var status = Field(fields, "MessageStatus") ?? Field(fields, "SmsStatus");
        if (sid is null || status is null)
        {
            return new ReceiptResult(true, []);
        }

        var outcome = status switch
        {
            "delivered" => DeliveryOutcome.Delivered,
            "read" => DeliveryOutcome.Read,
            "undelivered" or "failed" => DeliveryOutcome.Failed,
            _ => DeliveryOutcome.Other,
        };

        var to = Field(fields, "To");
        const string WhatsAppPrefix = "whatsapp:";
        if (to is not null && to.StartsWith(WhatsAppPrefix, StringComparison.OrdinalIgnoreCase))
        {
            to = to[WhatsAppPrefix.Length..];
        }

        // Twilio sends no timestamp, so the time the report arrived is the best available.
        return new ReceiptResult(true, [new DeliveryEvent(Name, sid, outcome, _time.GetUtcNow(), to, Field(fields, "ErrorCode"))]);
    }

    // Twilio's webhook signature is defined as HMAC-SHA1 (X-Twilio-Signature); it is a keyed MAC checked in constant time, not a password hash.
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Mandated by Twilio's signature protocol.")]
    private bool IsAuthentic(WebhookRequest request, List<KeyValuePair<string, string>> fields)
    {
        if (request.Header("X-Twilio-Signature") is not { } signature)
        {
            return false;
        }

        // The signed text is the URL followed by every field name and value, sorted by name.
        var signed = new StringBuilder(request.Url.AbsoluteUri);
        foreach (var (key, value) in fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            signed.Append(key).Append(value);
        }

        var expected = HMACSHA1.HashData(_key, Encoding.UTF8.GetBytes(signed.ToString()));
        byte[] actual;
        try
        {
            actual = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static List<KeyValuePair<string, string>> ParseForm(ReadOnlySpan<byte> body)
    {
        var fields = new List<KeyValuePair<string, string>>();
        foreach (var pair in Encoding.UTF8.GetString(body).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            fields.Add(new(Decode(name), Decode(value)));
        }

        return fields;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static string? Field(List<KeyValuePair<string, string>> fields, string name) =>
        fields.FirstOrDefault(f => f.Key == name).Value;
}
