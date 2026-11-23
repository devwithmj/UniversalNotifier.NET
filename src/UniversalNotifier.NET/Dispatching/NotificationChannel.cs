namespace UniversalNotifier.NET;

/// <summary>
/// A way of reaching someone (email, SMS, ...). The built-in channels are static members; create your own with
/// <c>new NotificationChannel("slack")</c> and register an <see cref="INotificationChannel"/> for it.
/// Names are case-insensitive.
/// </summary>
public readonly struct NotificationChannel : IEquatable<NotificationChannel>
{
    private readonly string? _name;

    /// <summary>Creates a channel with the given name.</summary>
    public NotificationChannel(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name.Trim().ToLowerInvariant();
    }

    /// <summary>Email.</summary>
    public static NotificationChannel Email { get; } = new("email");

    /// <summary>SMS text messages.</summary>
    public static NotificationChannel Sms { get; } = new("sms");

    /// <summary>Mobile push notifications.</summary>
    public static NotificationChannel Push { get; } = new("push");

    /// <summary>Browser push notifications.</summary>
    public static NotificationChannel WebPush { get; } = new("webpush");

    /// <summary>Telegram bot messages.</summary>
    public static NotificationChannel Telegram { get; } = new("telegram");

    /// <summary>The lower-case channel name.</summary>
    public string Name => _name ?? string.Empty;

    /// <inheritdoc />
    public bool Equals(NotificationChannel other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NotificationChannel other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => Name;

    /// <summary>Compares two channels by name.</summary>
    public static bool operator ==(NotificationChannel left, NotificationChannel right) => left.Equals(right);

    /// <summary>Compares two channels by name.</summary>
    public static bool operator !=(NotificationChannel left, NotificationChannel right) => !left.Equals(right);
}
