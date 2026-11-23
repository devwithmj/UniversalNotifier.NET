namespace UniversalNotifier.NET.Background;

/// <summary>
/// Where notifications wait for background delivery. The built-in queue lives in memory (fast, but queued notifications
/// are lost if the process stops). To survive restarts, implement this interface over a database or message broker
/// (see <see cref="NotificationJson"/> for storing a notification) and register it before <c>AddBackgroundDelivery</c>.
/// </summary>
public interface INotificationQueue
{
    /// <summary>
    /// Adds a notification. Returns <see langword="true"/> when it was accepted (queued, not delivered) and
    /// <see langword="false"/> when the queue is full and set to reject, or has been shut down.
    /// With the default <see cref="QueueFullMode.Wait"/> a full queue makes this call wait, which slows producers down
    /// instead of growing memory without limit (backpressure).
    /// </summary>
    ValueTask<bool> EnqueueAsync(Notification notification, CancellationToken cancellationToken = default);

    /// <summary>Reads notifications as they arrive, ending when the queue is completed and empty. Several readers may run at once.</summary>
    IAsyncEnumerable<Notification> DequeueAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops accepting notifications. Readers finish what is already queued, then end.</summary>
    void Complete();
}

/// <summary>What happens when a notification is enqueued and the queue is full.</summary>
public enum QueueFullMode
{
    /// <summary>The call waits for space. Applies backpressure to producers. The default.</summary>
    Wait,

    /// <summary>The call returns <see langword="false"/> immediately so the caller can shed load.</summary>
    Reject,
}

/// <summary>Called after each background notification was attempted. Register one to store results, alert on failures or dead-letter.</summary>
public interface INotificationResultHandler
{
    /// <summary>Receives the outcome. Exceptions thrown here are logged and do not stop the worker.</summary>
    ValueTask HandleAsync(Notification notification, NotificationResult result, CancellationToken cancellationToken);
}
