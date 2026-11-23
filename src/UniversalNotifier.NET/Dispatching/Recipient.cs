namespace UniversalNotifier.NET;

/// <summary>
/// Who a notification is for, with an address per channel. Fill in only the channels you want to reach; a
/// notification that asks for a channel the recipient has no address for reports that channel as failed.
/// </summary>
public sealed record Recipient
{
    /// <summary>The email address.</summary>
    public EmailAddress? Email { get; init; }

    /// <summary>The phone number for SMS, in E.164 format.</summary>
    public PhoneNumber? Phone { get; init; }

    /// <summary>Mobile push device tokens.</summary>
    public IReadOnlyList<string> DeviceTokens { get; init; } = [];

    /// <summary>Browser push subscriptions.</summary>
    public IReadOnlyList<WebPushSubscription> WebPushSubscriptions { get; init; } = [];

    /// <summary>The Telegram chat id.</summary>
    public string? TelegramChatId { get; init; }

    /// <summary>Your own user id, used for in-app notifications.</summary>
    public string? UserId { get; init; }

    /// <summary>The name of a configured webhook endpoint (for example a Slack channel or Teams workflow).</summary>
    public string? WebhookEndpoint { get; init; }

    /// <summary>
    /// Builds a recipient from one string: an email address (contains <c>@</c>) or an E.164 phone number (starts with <c>+</c>).
    /// </summary>
    /// <exception cref="ArgumentException">The text is neither an email address nor a phone number.</exception>
    public static Recipient Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();

        if (value.Contains('@', StringComparison.Ordinal))
        {
            return new Recipient { Email = value };
        }

        if (value[0] == '+')
        {
            return new Recipient { Phone = value };
        }

        throw new ArgumentException("Cannot tell whether this is an email address or a phone number. Use '@' for email, '+' for phone, or build a Recipient explicitly.", nameof(value));
    }

    /// <summary>Converts an email address or phone number string. See <see cref="Parse"/>.</summary>
    public static implicit operator Recipient(string value) => Parse(value);

    /// <summary>Prints nothing so personal data never leaks into logs.</summary>
    public sealed override string ToString() => nameof(Recipient);
}
