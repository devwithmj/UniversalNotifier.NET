using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;

namespace UniversalNotifier.NET;

/// <summary>
/// The values a template reads. Wraps a dictionary (preferred, no reflection) or any object whose public properties
/// become tokens (<c>new { Name = "John" }</c>). Nested values use dots: <c>{{User.Name}}</c>.
/// </summary>
public sealed class TemplateData
{
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo?> Properties = new();

    private readonly object? _root;

    private TemplateData(object? root) => _root = root;

    /// <summary>The wrapped dictionary or object. Template engines read it to build their own data model.</summary>
    public object? Value => _root;

    /// <summary>Data with no values.</summary>
    public static TemplateData Empty { get; } = new(null);

    /// <summary>Wraps a dictionary or an object. <see langword="null"/> gives <see cref="Empty"/>.</summary>
    public static TemplateData From(object? data) => data switch
    {
        null => Empty,
        TemplateData existing => existing,
        _ => new TemplateData(data),
    };

    /// <summary>Copies the entries of a dictionary into an independent dictionary. Does not use reflection.</summary>
    internal static IReadOnlyDictionary<string, object?> Snapshot(IReadOnlyDictionary<string, object?> data) =>
        new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies the public properties of an object into an independent dictionary. Uses reflection.</summary>
    [RequiresUnreferencedCode("Reads the public properties of the object with reflection, which trimming can remove. Pass a dictionary for trimming or Native AOT.")]
    internal static IReadOnlyDictionary<string, object?> SnapshotProperties(object data)
    {
        if (data is IReadOnlyDictionary<string, object?> dictionary)
        {
            return Snapshot(dictionary);
        }

        var snapshot = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in data.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0)
            {
                snapshot[property.Name] = property.GetValue(data);
            }
        }

        return snapshot;
    }

    /// <summary>Finds the value at a path. A missing key (or a missing step in the path) returns <see langword="false"/>; a present <see langword="null"/> returns <see langword="true"/>.</summary>
    internal bool TryGetValue(string[] path, out object? value)
    {
        var current = _root;
        foreach (var segment in path)
        {
            if (current is null || !TryGetMember(current, segment, out current))
            {
                value = null;
                return false;
            }
        }

        value = current;
        return true;
    }

    private static bool TryGetMember(object source, string name, out object? value)
    {
        switch (source)
        {
            case IReadOnlyDictionary<string, object?> dictionary:
                return dictionary.TryGetValue(name, out value);

            case JsonElement element:
                // Values that came back from a serialized (for example queued) notification are JSON elements.
                return TryGetJsonProperty(element, name, out value);

            case IDictionary legacy:
                if (legacy.Contains(name))
                {
                    value = legacy[name];
                    return true;
                }

                value = null;
                return false;

            default:
                var property = Properties.GetOrAdd((source.GetType(), name), static key => FindProperty(key.Type, key.Name));
                value = property?.GetValue(source);
                return property is not null;
        }
    }

    private static bool TryGetJsonProperty(JsonElement element, string name, out object? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty(name, out var exact))
        {
            value = exact;
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    // Reached only when a caller put a plain object where a dictionary, list, JSON element or primitive was expected; WithData(object) is already marked as needing reflection.
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Plain objects only reach this path when the caller chose the reflection-based WithData(object) or nested a plain object; dictionaries, lists and JSON never do.")]
    private static PropertyInfo? FindProperty(Type type, string name)
    {
        try
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return property is { CanRead: true } && property.GetIndexParameters().Length == 0 ? property : null;
        }
        catch (AmbiguousMatchException)
        {
            return null;
        }
    }
}
