using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET.Background;

/// <summary>The built-in in-memory queue, a bounded <see cref="Channel{T}"/>. Lock-free for the common path and safe for many producers and workers.</summary>
public sealed class ChannelNotificationQueue : INotificationQueue
{
    private readonly Channel<Notification> _channel;
    private readonly QueueFullMode _fullMode;

    /// <summary>Creates the queue from options.</summary>
    public ChannelNotificationQueue(IOptions<BackgroundDeliveryOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    /// <summary>Creates the queue.</summary>
    public ChannelNotificationQueue(BackgroundDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (BackgroundDeliveryOptions.Validate(options) is { } error)
        {
            throw new ArgumentException(error, nameof(options));
        }

        _fullMode = options.FullMode;
        _channel = Channel.CreateBounded<Notification>(new BoundedChannelOptions(options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false, // a producer must never run a worker's continuation inline
        });
    }

    /// <inheritdoc />
    public async ValueTask<bool> EnqueueAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (_fullMode == QueueFullMode.Reject)
        {
            return _channel.Writer.TryWrite(notification);
        }

        try
        {
            await _channel.Writer.WriteAsync(notification, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Notification> DequeueAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <inheritdoc />
    public void Complete() => _channel.Writer.TryComplete();
}
