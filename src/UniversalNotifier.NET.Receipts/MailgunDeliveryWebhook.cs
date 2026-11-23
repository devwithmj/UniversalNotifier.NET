using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UniversalNotifier.NET.Receipts;

/// <summary>
/// Verifies and parses Mailgun event webhooks (the JSON format with a <c>signature</c> block). Use the account's
/// <b>HTTP webhook signing key</b>, not the API key. A call whose timestamp is more than five minutes off is rejected
/// so a captured request cannot be replayed later.
/// </summary>
public sealed class MailgunDeliveryWebhook : IDeliveryWebhook
{
    /// <summary>The provider name.</summary>
    public const string Name = "mailgun";

    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private readonly byte[] _key;
    private readonly TimeProvider _time;

    /// <summary>Creates the webhook with the system clock.</summary>
    public MailgunDeliveryWebhook(string signingKey)
        : this(signingKey, TimeProvider.System)
    {
    }

    /// <summary>Creates the webhook with a given clock (for tests).</summary>
    public MailgunDeliveryWebhook(string signingKey, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _key = Encoding.UTF8.GetBytes(signingKey);
        _time = timeProvider;
    }

    /// <inheritdoc />
    public string Provider => Name;

    /// <inheritdoc />
    public ReceiptResult Process(WebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            var root = json.RootElement;
            if (!IsAuthentic(root))
            {
                return ReceiptResult.Rejected;
            }

            return root.TryGetProperty("event-data", out var data) && data.ValueKind == JsonValueKind.Object
                ? new ReceiptResult(true, [Map(data)])
                : new ReceiptResult(true, []);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return ReceiptResult.Rejected;
        }
    }

    private bool IsAuthentic(JsonElement root)
    {
        if (!root.TryGetProperty("signature", out var block) || block.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var timestamp = Text(block, "timestamp");
        var token = Text(block, "token");
        var signature = Text(block, "signature");
        if (timestamp is null || token is null || signature is null
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var age = _time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (age.Duration() > Tolerance)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(timestamp + token));
        byte[] actual;
        try
        {
            actual = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private DeliveryEvent Map(JsonElement data)
    {
        var kind = Text(data, "event");
        var severity = Text(data, "severity");
        var outcome = kind switch
        {
            "delivered" => DeliveryOutcome.Delivered,
            "failed" when severity == "temporary" => DeliveryOutcome.Deferred,
            "failed" or "rejected" => DeliveryOutcome.Failed,
            "complained" => DeliveryOutcome.Complained,
            "unsubscribed" => DeliveryOutcome.Unsubscribed,
            "opened" => DeliveryOutcome.Opened,
            "clicked" => DeliveryOutcome.Clicked,
            _ => DeliveryOutcome.Other,
        };

        // The send response carries the id in angle brackets; the event carries it bare.
        string? messageId = null;
        if (data.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object
            && Text(headers, "message-id") is { } bare)
        {
            messageId = bare.StartsWith('<') ? bare : $"<{bare}>";
        }

        var occurred = data.TryGetProperty("timestamp", out var stamp) && stamp.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(stamp.GetDouble() * 1000))
            : _time.GetUtcNow();

        // Only the machine-readable reason: the delivery-status description can quote the address and server text.
        var reason = data.TryGetProperty("delivery-status", out var status) && status.ValueKind == JsonValueKind.Object && status.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
            ? code.GetInt32().ToString(CultureInfo.InvariantCulture)
            : Text(data, "reason");

        return new DeliveryEvent(Name, messageId, outcome, occurred, Text(data, "recipient"), reason);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
}
