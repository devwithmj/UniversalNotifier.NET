namespace UniversalNotifier.NET;

/// <summary>Library-wide settings shared by every notifier.</summary>
public sealed class NotifierOptions
{
    /// <summary>Retry behavior. Retries are off unless <see cref="RetryOptions.MaxAttempts"/> is above 1.</summary>
    public RetryOptions Retry { get; } = new();
}

/// <summary>
/// Controls automatic retries. A send is repeated only when the provider did not accept it and repeating is safe
/// (see <see cref="NotifierErrorKind.Transient"/>), and never after a partial success.
/// </summary>
public sealed class RetryOptions
{
    /// <summary>Total attempts including the first. 1 (the default) means no retries. Maximum 10.</summary>
    public int MaxAttempts { get; set; } = 1;

    /// <summary>The wait before the first retry. It doubles each attempt, with jitter. A provider's own retry hint takes precedence.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest wait between attempts.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Also retry when the outcome is unknown (timeouts, dropped connections). The provider may already have sent the
    /// message, so enabling this can deliver duplicates. Off by default.
    /// </summary>
    public bool RetryUnknownFailures { get; set; }
}
