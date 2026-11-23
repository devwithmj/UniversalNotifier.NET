namespace UniversalNotifier.NET.Receipts;

/// <summary>What a provider reported about a message after accepting it.</summary>
public enum DeliveryOutcome
{
    /// <summary>The message reached the recipient's server or device.</summary>
    Delivered,

    /// <summary>The message will not be delivered (a bounce or a permanent failure).</summary>
    Failed,

    /// <summary>Delivery failed for now and the provider will retry.</summary>
    Deferred,

    /// <summary>The recipient marked the message as spam.</summary>
    Complained,

    /// <summary>The recipient unsubscribed.</summary>
    Unsubscribed,

    /// <summary>The recipient opened the message.</summary>
    Opened,

    /// <summary>The recipient clicked a link.</summary>
    Clicked,

    /// <summary>The recipient read the message (WhatsApp).</summary>
    Read,

    /// <summary>A progress update that is not a final outcome (queued, sent, ...).</summary>
    Other,
}

/// <summary>
/// One delivery report, the same shape for every provider. Match it to what you sent through <see cref="ProviderMessageId"/>,
/// which is the <c>ProviderMessageId</c> of the original <see cref="NotifierResponse"/>.
/// </summary>
/// <param name="Provider">The provider name, for example <c>mailgun</c>.</param>
/// <param name="ProviderMessageId">The id the provider gave the message when it accepted it.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="OccurredAt">When the provider says it happened (or when the report was received if it does not say).</param>
/// <param name="Recipient">The recipient address or number. Personal data: do not log it.</param>
/// <param name="Reason">The provider's error code or short reason, when there is one. Never message content.</param>
public sealed record DeliveryEvent(
    string Provider,
    string? ProviderMessageId,
    DeliveryOutcome Outcome,
    DateTimeOffset OccurredAt,
    string? Recipient,
    string? Reason);

/// <summary>A webhook call as received by your web host: only what verification and parsing need.</summary>
/// <param name="Url">The full public URL the provider called (scheme, host and query as the provider saw them).</param>
/// <param name="Headers">The request headers.</param>
/// <param name="Body">The raw request body, byte for byte.</param>
public sealed record WebhookRequest(Uri Url, IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body)
{
    /// <summary>Reads a header ignoring case; <see langword="null"/> when absent.</summary>
    public string? Header(string name)
    {
        foreach (var (key, value) in Headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>The outcome of checking and parsing one webhook call.</summary>
/// <param name="Authentic">Whether the provider's signature checked out. When <see langword="false"/> ignore the call (answer 401 or 403).</param>
/// <param name="Events">The delivery reports in the call; empty when it is not authentic or has nothing to report.</param>
public sealed record ReceiptResult(bool Authentic, IReadOnlyList<DeliveryEvent> Events)
{
    /// <summary>A call that failed verification.</summary>
    public static ReceiptResult Rejected { get; } = new(false, []);
}

/// <summary>Verifies and parses one provider's delivery webhook. Implementations never throw for bad input: they return <see cref="ReceiptResult.Rejected"/>.</summary>
public interface IDeliveryWebhook
{
    /// <summary>The provider name, matching the notifier's provider name (for example <c>mailgun</c>).</summary>
    string Provider { get; }

    /// <summary>Checks the provider's signature and turns the call into delivery events.</summary>
    ReceiptResult Process(WebhookRequest request);
}
