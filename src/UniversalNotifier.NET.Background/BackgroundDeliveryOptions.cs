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

    internal static string? Validate(BackgroundDeliveryOptions options) =>
        options.Capacity < 1 ? "Capacity must be at least 1."
        : options.MaxConcurrency is < 1 or > 1024 ? "MaxConcurrency must be between 1 and 1024."
        : null;
}
