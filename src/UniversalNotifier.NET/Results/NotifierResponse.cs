namespace UniversalNotifier.NET;

/// <summary>How far a send got. "Accepted" means the provider took the message, not that it was delivered.</summary>
public enum DeliveryStatus
{
    /// <summary>The provider accepted the message for every recipient.</summary>
    Accepted,

    /// <summary>The provider accepted the message for some recipients and rejected others.</summary>
    PartiallyAccepted,

    /// <summary>Nothing was accepted. See <see cref="NotifierResponse.Errors"/>.</summary>
    Failed,
}

/// <summary>The immutable outcome of a send. Failures are reported here instead of being thrown.</summary>
public sealed class NotifierResponse
{
    private NotifierResponse(
        DeliveryStatus status,
        string? providerMessageId,
        int? httpStatusCode,
        IReadOnlyList<NotifierError> errors,
        IReadOnlyList<RecipientResult> recipients,
        TimeSpan? retryAfter)
    {
        Status = status;
        ProviderMessageId = providerMessageId;
        HttpStatusCode = httpStatusCode;
        Errors = errors;
        Recipients = recipients;
        RetryAfter = retryAfter;
    }

    /// <summary>How far the send got.</summary>
    public DeliveryStatus Status { get; }

    /// <summary><see langword="true"/> when the provider accepted the message for every recipient.</summary>
    public bool Success => Status == DeliveryStatus.Accepted;

    /// <summary>The provider's message id, when it gives one.</summary>
    public string? ProviderMessageId { get; }

    /// <summary>The HTTP status code of the provider call, when the provider is an HTTP API.</summary>
    public int? HttpStatusCode { get; }

    /// <summary>Why the send failed. Empty when <see cref="Success"/> is <see langword="true"/>.</summary>
    public IReadOnlyList<NotifierError> Errors { get; }

    /// <summary>Per-recipient outcomes. Empty when the provider does not report them.</summary>
    public IReadOnlyList<RecipientResult> Recipients { get; }

    /// <summary>How long the provider asked callers to wait before retrying, when it said so.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Creates a response for a message the provider accepted.</summary>
    public static NotifierResponse Accepted(
        string? providerMessageId = null,
        int? httpStatusCode = null,
        IReadOnlyList<RecipientResult>? recipients = null) =>
        new(DeliveryStatus.Accepted, providerMessageId, httpStatusCode, [], recipients ?? [], null);

    /// <summary>Creates a failed response with one error.</summary>
    public static NotifierResponse Failed(NotifierError error, int? httpStatusCode = null, TimeSpan? retryAfter = null) =>
        new(DeliveryStatus.Failed, null, httpStatusCode, [error], [], retryAfter);

    /// <summary>Creates a failed response with several errors.</summary>
    public static NotifierResponse Failed(IReadOnlyList<NotifierError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new(DeliveryStatus.Failed, null, null, errors, [], null);
    }

    /// <summary>Creates a response from per-recipient outcomes, deriving the overall status.</summary>
    public static NotifierResponse FromRecipients(
        IReadOnlyList<RecipientResult> recipients,
        string? providerMessageId = null,
        int? httpStatusCode = null,
        TimeSpan? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        var accepted = 0;
        List<NotifierError>? errors = null;
        foreach (var recipient in recipients)
        {
            if (recipient.Accepted)
            {
                accepted++;
            }
            else if (recipient.Error is { } error)
            {
                (errors ??= []).Add(error);
            }
        }

        var status = accepted == recipients.Count && recipients.Count > 0
            ? DeliveryStatus.Accepted
            : accepted > 0 ? DeliveryStatus.PartiallyAccepted : DeliveryStatus.Failed;

        return new(status, providerMessageId, httpStatusCode, (IReadOnlyList<NotifierError>?)errors ?? [], recipients, retryAfter);
    }
}
