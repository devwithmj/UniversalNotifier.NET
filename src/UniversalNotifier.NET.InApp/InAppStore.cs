namespace UniversalNotifier.NET.InApp;

/// <summary>One entry in a user's inbox.</summary>
/// <param name="Id">The entry id, used to mark it read.</param>
/// <param name="UserId">The user it belongs to.</param>
/// <param name="Title">An optional heading.</param>
/// <param name="Text">The message text.</param>
/// <param name="Data">Extra key/value data for your UI.</param>
/// <param name="CreatedAt">When it was stored.</param>
/// <param name="IsRead">Whether the user has read it.</param>
public sealed record InAppNotification(
    string Id,
    string UserId,
    string? Title,
    string Text,
    IReadOnlyDictionary<string, string>? Data,
    DateTimeOffset CreatedAt,
    bool IsRead);

/// <summary>
/// Where in-app notifications live. The default is an in-memory store; implement this over your database to keep
/// them across restarts, and register it before <c>AddInApp</c>. Inject it to build the inbox in your own endpoints.
/// </summary>
public interface IInAppStore
{
    /// <summary>Stores a notification.</summary>
    ValueTask AddAsync(InAppNotification notification, CancellationToken cancellationToken = default);

    /// <summary>Lists a user's notifications, newest first.</summary>
    ValueTask<IReadOnlyList<InAppNotification>> ListAsync(string userId, bool unreadOnly = false, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Counts a user's unread notifications.</summary>
    ValueTask<int> CountUnreadAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Marks one notification read. Returns <see langword="false"/> when the user has no such notification.</summary>
    ValueTask<bool> MarkReadAsync(string userId, string notificationId, CancellationToken cancellationToken = default);

    /// <summary>Marks all of a user's notifications read.</summary>
    ValueTask MarkAllReadAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>An in-memory inbox, bounded per user (the oldest entries are dropped). Lost on restart; suitable for development and small apps.</summary>
public sealed class InMemoryInAppStore : IInAppStore
{
    private readonly Dictionary<string, LinkedList<InAppNotification>> _inboxes = new(StringComparer.Ordinal);
    private readonly int _maxPerUser;
    private readonly object _lock = new();

    /// <summary>Creates the store keeping at most <paramref name="maxPerUser"/> entries per user.</summary>
    public InMemoryInAppStore(int maxPerUser = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerUser, 1);
        _maxPerUser = maxPerUser;
    }

    /// <inheritdoc />
    public ValueTask AddAsync(InAppNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (_lock)
        {
            if (!_inboxes.TryGetValue(notification.UserId, out var inbox))
            {
                _inboxes[notification.UserId] = inbox = new LinkedList<InAppNotification>();
            }

            inbox.AddFirst(notification);
            while (inbox.Count > _maxPerUser)
            {
                inbox.RemoveLast();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<InAppNotification>> ListAsync(string userId, bool unreadOnly = false, int limit = 50, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<InAppNotification> result = _inboxes.TryGetValue(userId, out var inbox)
                ? [.. inbox.Where(n => !unreadOnly || !n.IsRead).Take(Math.Max(0, limit))]
                : [];
            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> CountUnreadAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_inboxes.TryGetValue(userId, out var inbox) ? inbox.Count(n => !n.IsRead) : 0);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> MarkReadAsync(string userId, string notificationId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_inboxes.TryGetValue(userId, out var inbox))
            {
                for (var node = inbox.First; node is not null; node = node.Next)
                {
                    if (node.Value.Id == notificationId)
                    {
                        node.Value = node.Value with { IsRead = true };
                        return ValueTask.FromResult(true);
                    }
                }
            }

            return ValueTask.FromResult(false);
        }
    }

    /// <inheritdoc />
    public ValueTask MarkAllReadAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_inboxes.TryGetValue(userId, out var inbox))
            {
                for (var node = inbox.First; node is not null; node = node.Next)
                {
                    node.Value = node.Value with { IsRead = true };
                }
            }
        }

        return ValueTask.CompletedTask;
    }
}
