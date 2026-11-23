using System.Reflection;

namespace UniversalNotifier.NET;

/// <summary>
/// Loads templates compiled into an assembly, so an application or a library can ship its notification templates inside the
/// binary. Mark the files as embedded resources (<c>&lt;EmbeddedResource Include="Templates\**\*.*" /&gt;</c>) using the same
/// layout as the directory store: <c>Templates/welcome/email.subject.txt</c>, <c>email.html</c>, <c>sms.text.txt</c>, ...
/// </summary>
/// <remarks>
/// MSBuild turns folder separators into dots and hyphens in folder names into underscores, so the template
/// <c>order-shipped</c> is stored as <c>order_shipped</c>; lookups treat <c>-</c> and <c>_</c> alike.
/// </remarks>
public sealed class EmbeddedTemplateStore : IListableTemplateStore
{
    private static readonly string[] Parts = ["subject.txt", "title.txt", "text.txt", "html"];

    private readonly Assembly _assembly;
    private readonly string _prefix;
    private readonly string[] _resources;

    /// <summary>Creates a store over an assembly.</summary>
    /// <param name="assembly">The assembly holding the embedded templates.</param>
    /// <param name="resourcePrefix">The resource name prefix of the templates folder, for example <c>MyApp.Templates</c> (root namespace plus folder).</param>
    public EmbeddedTemplateStore(Assembly assembly, string resourcePrefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);
        _assembly = assembly;
        _prefix = resourcePrefix.TrimEnd('.') + ".";
        _resources = assembly.GetManifestResourceNames();
    }

    /// <inheritdoc />
    public ValueTask<ChannelContent?> TryGetAsync(string name, NotificationChannel channel, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Load(name, channel));

    /// <inheritdoc />
    public IEnumerable<TemplateEntry> List()
    {
        var seen = new HashSet<(string, string)>();
        foreach (var resource in _resources)
        {
            if (!resource.StartsWith(_prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = resource[_prefix.Length..];
            var part = Array.Find(Parts, p => remainder.EndsWith("." + p, StringComparison.Ordinal));
            if (part is null)
            {
                continue;
            }

            // remainder is "<name>.<channel>.<part>"; the name itself never contains a dot.
            var segments = remainder[..^(part.Length + 1)].Split('.');
            if (segments.Length != 2 || !seen.Add((segments[0], segments[1])))
            {
                continue;
            }

            var channel = new NotificationChannel(segments[1]);
            if (Load(segments[0], channel) is { } content)
            {
                yield return new TemplateEntry(segments[0], channel, content);
            }
        }
    }

    private ChannelContent? Load(string name, NotificationChannel channel)
    {
        var stem = _prefix + name.Replace('-', '_') + "." + channel.Name + ".";
        var subject = Read(stem + "subject.txt");
        var title = Read(stem + "title.txt");
        var text = Read(stem + "text.txt");
        var html = Read(stem + "html");

        return subject is null && title is null && text is null && html is null
            ? null
            : new ChannelContent { Subject = subject, Title = title, Text = text, Html = html };
    }

    private string? Read(string resourceName)
    {
        // Resource names are case-sensitive; match the real one so a template written as "Welcome" still loads.
        var actual = Array.Find(_resources, r => string.Equals(r, resourceName, StringComparison.OrdinalIgnoreCase));
        if (actual is null)
        {
            return null;
        }

        using var stream = _assembly.GetManifestResourceStream(actual);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
