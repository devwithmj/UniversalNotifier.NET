namespace UniversalNotifier.NET;

/// <summary>
/// The one entry point for sending a notification on several channels. It renders content, picks the providers and
/// sends every channel concurrently. Failures are reported in the result, not thrown.
/// </summary>
public interface INotificationService
{
    /// <summary>Sends a notification.</summary>
    /// <exception cref="ArgumentException">The notification has no channels.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken = default);
}

/// <summary>What a channel handler receives: the notification, the already-rendered content and the provider choice.</summary>
/// <param name="Notification">The notification being sent.</param>
/// <param name="Content">The rendered content for this channel.</param>
/// <param name="Provider">The requested provider name, or <see langword="null"/> for the default.</param>
public sealed record NotificationDelivery(Notification Notification, ChannelContent Content, string? Provider);

/// <summary>
/// Connects the notification model to a notifier for one channel. Built-in channels are registered automatically;
/// implement this and register it with <c>AddChannel</c> to add your own.
/// </summary>
public interface INotificationChannel
{
    /// <summary>The channel this handler serves.</summary>
    NotificationChannel Channel { get; }

    /// <summary>Sends the delivery through the right notifier.</summary>
    Task<NotifierResponse> SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken);
}
