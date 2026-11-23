namespace UniversalNotifier.NET;

/// <summary>The outcome for one recipient of a bulk send.</summary>
/// <param name="Recipient">The recipient as supplied (address, phone number, token or chat id).</param>
/// <param name="Accepted">Whether the provider accepted the message for this recipient.</param>
/// <param name="ProviderMessageId">The provider's id for this recipient's message, when it gives one.</param>
/// <param name="Error">Why the recipient was rejected; <see langword="null"/> when accepted.</param>
public readonly record struct RecipientResult(
    string Recipient,
    bool Accepted,
    string? ProviderMessageId = null,
    NotifierError? Error = null);
