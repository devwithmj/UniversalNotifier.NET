using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Fluid;
using FluidOptions = Fluid.TemplateOptions;

namespace UniversalNotifier.NET.Templates.Fluid;

/// <summary>
/// Renders templates with the Fluid engine (Liquid syntax): <c>{% if total &gt; 100 %}</c>, <c>{% for item in items %}</c>,
/// filters such as <c>{{ name | upcase }}</c>. Safety comes from execution limits (steps, output size, collection size),
/// no file provider (so no <c>include</c>), and a data model built explicitly from your values: templates reach plain data,
/// never .NET members. In HTML parts every string value is HTML-encoded before the template sees it.
/// </summary>
/// <remarks>
/// Variable names are case-sensitive. <see cref="MissingTokenBehavior.Throw"/> makes an undefined variable an error; the
/// other behaviors render it as empty. Parsed templates are cached.
/// </remarks>
public sealed class FluidTemplateRenderer : ITemplateRenderer
{
    private const int MaxDepth = 8;

    private static readonly FluidParser Parser = new();

    private readonly FluidOptions _templateOptions;
    private readonly int _maxCachedTemplates;
    private readonly ConcurrentDictionary<string, IFluidTemplate> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates the renderer. Uses the same <see cref="UniversalNotifier.NET.TemplateOptions"/> as the built-in renderer (missing token behavior, culture, cache size).</summary>
    public FluidTemplateRenderer(UniversalNotifier.NET.TemplateOptions? options = null)
    {
        var settings = options ?? new UniversalNotifier.NET.TemplateOptions();
        _maxCachedTemplates = settings.MaxCachedTemplates;
        _templateOptions = new TemplateOptionsBuilder()
            .WithStrictVariables(settings.MissingToken == MissingTokenBehavior.Throw)
            .WithMaxSteps(10_000)
            .WithMaxOutputSize(4 << 20)
            .WithMaxCollectionSize(10_000)
            .WithCultureInfo(settings.Culture)
            .WithMaxRecursion(100)
            .WithStrictFilters(true)
            .Build();
    }

    /// <inheritdoc />
    public string Render(string templateText, TemplateData data, TemplateFormat format = TemplateFormat.Text)
    {
        ArgumentNullException.ThrowIfNull(templateText);
        ArgumentNullException.ThrowIfNull(data);

        var template = GetTemplate(templateText);
        var context = new TemplateContext(_templateOptions);
        if (ToPlain(data.Value, format, depth: 0) is Dictionary<string, object?> root)
        {
            foreach (var (key, value) in root)
            {
                context.SetValue(key, value!);
            }
        }

        try
        {
            return template.Render(context);
        }
        catch (Exception exception) when (exception is FluidException or InvalidOperationException or FileNotFoundException)
        {
            throw new TemplateRenderException($"The template could not be rendered: {exception.Message}");
        }
    }

    private IFluidTemplate GetTemplate(string text)
    {
        if (_cache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        if (!Parser.TryParse(text, out var template, out var error))
        {
            throw new TemplateRenderException("The template is not valid: " + error);
        }

        if (_cache.Count >= _maxCachedTemplates)
        {
            _cache.Clear();
        }

        _cache[text] = template;
        return template;
    }

    // Builds plain values (dictionaries, lists, strings, numbers) so templates can read data but never call .NET members.
    private static object? ToPlain(object? value, TemplateFormat format, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new TemplateRenderException($"The template data is nested more than {MaxDepth} levels deep.");
        }

        switch (value)
        {
            case null:
                return null;
            case string text:
                return format == TemplateFormat.Html ? WebUtility.HtmlEncode(text) : text;
            case bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or TimeSpan:
                return value;
            case char or Guid or Enum:
                return value.ToString();
            case JsonElement element:
                return FromJson(element, format, depth);
            case IDictionary dictionary:
                return FromEntries(dictionary.Keys.Cast<object>().Select(k => (k.ToString() ?? string.Empty, dictionary[k])), format, depth);
            case IEnumerable sequence:
                return sequence.Cast<object?>().Select(item => ToPlain(item, format, depth + 1)).ToList();
            default:
                return FromEntries(
                    value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                        .Select(p => (p.Name, p.GetValue(value))),
                    format,
                    depth);
        }
    }

    private static Dictionary<string, object?> FromEntries(IEnumerable<(string Key, object? Value)> entries, TemplateFormat format, int depth)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, entry) in entries)
        {
            result[key] = ToPlain(entry, format, depth + 1);
        }

        return result;
    }

    private static object? FromJson(JsonElement element, TemplateFormat format, int depth) => element.ValueKind switch
    {
        JsonValueKind.Object => FromEntries(element.EnumerateObject().Select(p => (p.Name, (object?)p.Value)), format, depth),
        JsonValueKind.Array => element.EnumerateArray().Select(e => ToPlain(e, format, depth + 1)).ToList(),
        JsonValueKind.String => ToPlain(element.GetString(), format, depth),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
