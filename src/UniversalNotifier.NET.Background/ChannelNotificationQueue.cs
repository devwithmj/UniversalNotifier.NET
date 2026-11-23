using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET.Background;

/// <summary>The built-in in-memory queue, a bounded <see cref="Channel{T}"/>. Lock-free for the common path and safe for many producers and workers.</summary>
public sealed class ChannelNotificationQueue : INotificationQueue
{
    private readonly Channel<Notification> _channel;
    private readonly QueueFullMode _fullMode;
    private readonly TimeSpan _deduplicationWindow;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, long> _seenKeys = new(StringComparer.Ordinal);

    /// <summary>Creates the queue from options.</summary>
    public ChannelNotificationQueue(IOptions<BackgroundDeliveryOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), TimeProvider.System)
    {
    }

    /// <summary>Creates the queue from options with a specific clock (used for the deduplication window).</summary>
    public ChannelNotificationQueue(IOptions<BackgroundDeliveryOptions> options, TimeProvider timeProvider)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), timeProvider)
    {
    }

    /// <summary>Creates the queue.</summary>
    public ChannelNotificationQueue(BackgroundDeliveryOptions options)
        : this(options, TimeProvider.System)
    {
    }

    /// <summary>Creates the queue with a specific clock (used for the deduplication window).</summary>
    public ChannelNotificationQueue(BackgroundDeliveryOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (BackgroundDeliveryOptions.Validate(options) is { } error)
        {
            throw new ArgumentException(error, nameof(options));
        }

        _fullMode = options.FullMode;
        _deduplicationWindow = options.DeduplicationWindow;
        _time = timeProvider;
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

        var key = notification.IdempotencyKey;
        if (key is not null && _deduplicationWindow > TimeSpan.Zero && !TryRegisterKey(key))
        {
            return true; // already accepted: the earlier submission carries it, so this one is a harmless repeat
        }

        try
        {
            if (_fullMode == QueueFullMode.Reject)
            {
                return Release(_channel.Writer.TryWrite(notification), key);
            }

            await _channel.Writer.WriteAsync(notification, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            return Release(false, key);
        }
        catch (OperationCanceledException)
        {
            Release(false, key);
            throw;
        }
    }

    // A notification that was not queued must not block the caller's next attempt with the same key.
    private bool Release(bool queued, string? key)
    {
        if (!queued && key is not null)
        {
            _seenKeys.TryRemove(key, out _);
        }

        return queued;
    }

    private bool TryRegisterKey(string key)
    {
        var now = _time.GetUtcNow().UtcTicks;
        var expiry = now + _deduplicationWindow.Ticks;

        if (_seenKeys.Count >= 4096)
        {
            foreach (var (seen, until) in _seenKeys)
            {
                if (until <= now)
                {
                    _seenKeys.TryRemove(new KeyValuePair<string, long>(seen, until));
                }
            }
        }

        while (true)
        {
            if (_seenKeys.TryAdd(key, expiry))
            {
                return true;
            }

            if (!_seenKeys.TryGetValue(key, out var existing))
            {
                continue;
            }

            if (existing > now)
            {
                return false;
            }

            if (_seenKeys.TryUpdate(key, expiry, existing))
            {
                return true;
            }
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Notification> DequeueAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <inheritdoc />
    public void Complete() => _channel.Writer.TryComplete();
}
