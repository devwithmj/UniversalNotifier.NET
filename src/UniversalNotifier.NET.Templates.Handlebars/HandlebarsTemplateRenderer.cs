using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using HandlebarsDotNet;
using HandlebarsDotNet.Compiler;

namespace UniversalNotifier.NET.Templates.Handlebars;

/// <summary>
/// Renders templates with Handlebars (<c>{{#if Paid}}</c>, <c>{{#each Items}}</c>, <c>{{#with Order}}</c>). Safety comes from
/// disabling partials and the file system (templates cannot read files) and from a data model built explicitly from your
/// values: templates reach plain data, never .NET objects. In HTML parts every string value is HTML-encoded before the
/// template sees it, so a forgotten <c>{{{triple}}}</c> cannot inject markup.
/// </summary>
/// <remarks>
/// Variable names are case-sensitive. <see cref="MissingTokenBehavior.Throw"/> makes an undefined variable an error; the
/// other behaviors render it as empty. Compiled templates are cached. Handlebars has no comparison operators: compute
/// flags such as <c>IsLarge</c> in the data. Handlebars.Net has no step or output limits, but without partials a template
/// cannot loop except over your data, so work is bounded by the data you pass.
/// </remarks>
public sealed class HandlebarsTemplateRenderer : IValidatingTemplateRenderer
{
    private const int MaxDepth = 8;

    private readonly IHandlebars _environment;
    private readonly int _maxCachedTemplates;
    private readonly ConcurrentDictionary<string, HandlebarsTemplate<object, object>> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates the renderer. Uses the same <see cref="UniversalNotifier.NET.TemplateOptions"/> as the built-in renderer (missing token behavior, culture, cache size).</summary>
    public HandlebarsTemplateRenderer(UniversalNotifier.NET.TemplateOptions? options = null)
    {
        var settings = options ?? new UniversalNotifier.NET.TemplateOptions();
        _maxCachedTemplates = settings.MaxCachedTemplates;
        _environment = HandlebarsDotNet.Handlebars.Create(new HandlebarsConfiguration
        {
            // Encoding is done on the data (see ToPlain) so the text and HTML parts share one environment and {{{raw}}} gains nothing.
            NoEscape = true,
            FormatProvider = settings.Culture,
            ThrowOnUnresolvedBindingExpression = settings.MissingToken == MissingTokenBehavior.Throw,
            PartialTemplateResolver = new NoPartials(),
            PartialRecursionDepthLimit = 8,
        });
    }

    /// <inheritdoc />
    public string Render(string templateText, TemplateData data, TemplateFormat format = TemplateFormat.Text)
    {
        ArgumentNullException.ThrowIfNull(templateText);
        ArgumentNullException.ThrowIfNull(data);

        var template = GetTemplate(templateText);
        try
        {
            return template(ToPlain(data.Value, format, depth: 0) ?? new Dictionary<string, object?>());
        }
        catch (Exception exception) when (exception is HandlebarsException or HandlebarsUndefinedBindingException or InvalidOperationException)
        {
            throw new TemplateRenderException($"The template could not be rendered: {exception.Message}");
        }
    }

    /// <inheritdoc />
    public string? Validate(string templateText)
    {
        ArgumentNullException.ThrowIfNull(templateText);
        try
        {
            _ = _environment.Compile(templateText);
            return null;
        }
        catch (HandlebarsException exception)
        {
            return exception.Message;
        }
    }

    private HandlebarsTemplate<object, object> GetTemplate(string text)
    {
        if (_cache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        HandlebarsTemplate<object, object> template;
        try
        {
            template = _environment.Compile(text);
        }
        catch (HandlebarsException exception)
        {
            throw new TemplateRenderException("The template is not valid: " + exception.Message);
        }

        if (_cache.Count >= _maxCachedTemplates)
        {
            _cache.Clear();
        }

        _cache[text] = template;
        return template;
    }

    // Builds plain values (dictionaries, lists, strings, numbers) so templates can read data but never reach .NET objects.
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
        JsonValueKind.Object => FromEntries(element.EnumerateObject().Select<JsonProperty, (string, object?)>(p => (p.Name, p.Value)), format, depth),
        JsonValueKind.Array => element.EnumerateArray().Select(e => ToPlain(e, format, depth + 1)).ToList(),
        JsonValueKind.String => ToPlain(element.GetString(), format, depth),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    // Templates must not pull in files: an unregistered partial is simply not found.
    private sealed class NoPartials : IPartialTemplateResolver
    {
        public bool TryRegisterPartial(IHandlebars env, string? partialName, string? templatePath) => false;
    }
}
