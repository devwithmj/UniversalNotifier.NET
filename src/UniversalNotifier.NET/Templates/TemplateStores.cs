using System.Collections.Concurrent;

namespace UniversalNotifier.NET;

/// <summary>Finds the content of a named template for a channel. Implement it to load templates from a database or anywhere else.</summary>
public interface ITemplateStore
{
    /// <summary>Returns the template for the channel, or <see langword="null"/> when this store does not have it.</summary>
    ValueTask<ChannelContent?> TryGetAsync(string name, NotificationChannel channel, CancellationToken cancellationToken = default);
}

/// <summary>A template registered in code. See <c>AddTemplate</c> on the builder.</summary>
/// <param name="Name">The template name.</param>
/// <param name="Channel">The channel the content is for.</param>
/// <param name="Content">The content.</param>
public sealed record TemplateRegistration(string Name, NotificationChannel Channel, ChannelContent Content);

/// <summary>Holds templates registered in code.</summary>
public sealed class InMemoryTemplateStore : IListableTemplateStore
{
    private readonly Dictionary<string, Dictionary<string, ChannelContent>> _templates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the store from registrations.</summary>
    public InMemoryTemplateStore(IEnumerable<TemplateRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var (name, channel, content) in registrations)
        {
            if (!_templates.TryGetValue(name, out var channels))
            {
                _templates[name] = channels = new Dictionary<string, ChannelContent>(StringComparer.Ordinal);
            }

            channels[channel.Name] = content;
        }
    }

    /// <inheritdoc />
    public ValueTask<ChannelContent?> TryGetAsync(string name, NotificationChannel channel, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            _templates.TryGetValue(name, out var channels) && channels.TryGetValue(channel.Name, out var content) ? content : null);

    /// <inheritdoc />
    public IEnumerable<TemplateEntry> List() =>
        _templates.SelectMany(t => t.Value.Select(c => new TemplateEntry(t.Key, new NotificationChannel(c.Key), c.Value)));
}

/// <summary>
/// Loads templates from a folder: <c>{root}/{template}/{channel}.subject.txt</c>, <c>.title.txt</c>, <c>.text.txt</c> and <c>.html</c>,
/// for example <c>templates/welcome/email.subject.txt</c> and <c>templates/welcome/email.html</c>. Loaded templates are cached.
/// </summary>
public sealed class DirectoryTemplateStore : IListableTemplateStore
{
    private readonly string _root;
    private readonly ConcurrentDictionary<(string Name, string Channel), ChannelContent> _cache = new();

    /// <summary>Creates a store for a folder.</summary>
    public DirectoryTemplateStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
    }

    /// <inheritdoc />
    public async ValueTask<ChannelContent?> TryGetAsync(string name, NotificationChannel channel, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue((name, channel.Name), out var cached))
        {
            return cached;
        }

        // Names become path segments, so only plain names are allowed. This blocks "../" and absolute paths.
        if (!IsSafeSegment(name) || !IsSafeSegment(channel.Name))
        {
            return null;
        }

        var directory = Path.Join(_root, name);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var prefix = Path.Join(directory, channel.Name);
        var subject = await ReadAsync(prefix + ".subject.txt", cancellationToken).ConfigureAwait(false);
        var title = await ReadAsync(prefix + ".title.txt", cancellationToken).ConfigureAwait(false);
        var text = await ReadAsync(prefix + ".text.txt", cancellationToken).ConfigureAwait(false);
        var html = await ReadAsync(prefix + ".html", cancellationToken).ConfigureAwait(false);

        if (subject is null && title is null && text is null && html is null)
        {
            return null;
        }

        var content = new ChannelContent { Subject = subject, Title = title, Text = text, Html = html };
        _cache[(name, channel.Name)] = content;
        return content;
    }

    /// <inheritdoc />
    public IEnumerable<TemplateEntry> List()
    {
        if (!Directory.Exists(_root))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var name = Path.GetFileName(directory);
            var channels = Directory.EnumerateFiles(directory)
                .Select(f => Path.GetFileName(f).Split('.')[0])
                .Distinct(StringComparer.Ordinal);

            foreach (var channelName in channels)
            {
                if (!IsSafeSegment(name) || !IsSafeSegment(channelName))
                {
                    continue;
                }

                var channel = new NotificationChannel(channelName);
                if (TryGetAsync(name, channel).AsTask().GetAwaiter().GetResult() is { } content)
                {
                    yield return new TemplateEntry(name, channel, content);
                }
            }
        }
    }

    private static bool IsSafeSegment(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] == '.' || value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<string?> ReadAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
}
