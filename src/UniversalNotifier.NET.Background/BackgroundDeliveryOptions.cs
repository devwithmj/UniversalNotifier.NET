namespace UniversalNotifier.NET.Background;

/// <summary>Settings for background delivery.</summary>
public sealed class BackgroundDeliveryOptions
{
    /// <summary>How many notifications the in-memory queue holds before it applies <see cref="FullMode"/>.</summary>
    public int Capacity { get; set; } = 1000;

    /// <summary>What to do when the queue is full.</summary>
    public QueueFullMode FullMode { get; set; } = QueueFullMode.Wait;

    /// <summary>How many notifications are delivered at the same time. Delivery is I/O bound, so this can exceed the CPU count.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>
    /// How long a notification's <see cref="Notification.IdempotencyKey"/> is remembered. Enqueuing a notification with a key
    /// seen inside this window is accepted (returns <see langword="true"/>) but not queued again, so a client retry or a
    /// double submit cannot send twice. <see cref="TimeSpan.Zero"/> (the default) turns it off. The memory is per process.
    /// </summary>
    public TimeSpan DeduplicationWindow { get; set; }

    internal static string? Validate(BackgroundDeliveryOptions options) =>
        options.DeduplicationWindow < TimeSpan.Zero ? "DeduplicationWindow must not be negative."
        : options.Capacity < 1 ? "Capacity must be at least 1."
        : options.MaxConcurrency is < 1 or > 1024 ? "MaxConcurrency must be between 1 and 1024."
        : null;
}
