namespace UniversalNotifier.NET;

/// <summary>The outcome on one channel.</summary>
/// <param name="Channel">The channel.</param>
/// <param name="Response">What the provider reported.</param>
public readonly record struct ChannelResult(NotificationChannel Channel, NotifierResponse Response);

/// <summary>The outcome of a notification: one result per requested channel, in the order requested.</summary>
public sealed class NotificationResult
{
    /// <summary>Creates a result.</summary>
    public NotificationResult(string? notificationId, IReadOnlyList<ChannelResult> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        NotificationId = notificationId;
        Channels = channels;

        var accepted = channels.Count(channel => channel.Response.Success);

        Status = accepted == channels.Count && accepted > 0
            ? DeliveryStatus.Accepted
            : accepted > 0 ? DeliveryStatus.PartiallyAccepted : DeliveryStatus.Failed;
    }

    /// <summary>The notification's correlation id.</summary>
    public string? NotificationId { get; }

    /// <summary>The result for each channel.</summary>
    public IReadOnlyList<ChannelResult> Channels { get; }

    /// <summary>Accepted when every channel accepted, PartiallyAccepted when some did, Failed when none did.</summary>
    public DeliveryStatus Status { get; }

    /// <summary><see langword="true"/> when every channel accepted the message.</summary>
    public bool Success => Status == DeliveryStatus.Accepted;

    /// <summary>Finds the result for a channel, or <see langword="null"/> when it was not requested.</summary>
    public NotifierResponse? this[NotificationChannel channel]
    {
        get
        {
            foreach (var result in Channels)
            {
                if (result.Channel == channel)
                {
                    return result.Response;
                }
            }

            return null;
        }
    }
}
