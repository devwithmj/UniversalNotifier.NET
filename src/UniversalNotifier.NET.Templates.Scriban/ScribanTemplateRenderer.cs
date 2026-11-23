using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

namespace UniversalNotifier.NET.Templates.Scriban;

/// <summary>
/// Renders templates with the Scriban engine: <c>{{ if Total &gt; 100 }}</c>, <c>{{ for item in Items }}</c>, filters such as
/// <c>{{ Name | string.upcase }}</c>. Safety comes from three things: execution limits (loops, recursion, output size),
/// no <c>include</c> (there is no template loader), and a data model built explicitly from your values, so templates reach
/// plain data and never .NET methods. In HTML parts every string value is HTML-encoded before the template sees it.
/// </summary>
/// <remarks>
/// Names are case-sensitive here (unlike the built-in renderer). <see cref="MissingTokenBehavior.Throw"/> makes an undefined
/// variable an error; the other behaviors render it as empty. Parsed templates are cached.
/// </remarks>
public sealed class ScribanTemplateRenderer : IValidatingTemplateRenderer
{
    private const int MaxDepth = 8;
    private const int LoopLimit = 1000;
    private const int RecursionLimit = 100;
    private const int StringLimit = 1 << 20;
    private const int OutputLimit = 4 << 20;

    private readonly TemplateOptions _options;
    private readonly ConcurrentDictionary<string, Template> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates the renderer. Uses the same <see cref="TemplateOptions"/> as the built-in renderer (missing token behavior, culture, cache size).</summary>
    public ScribanTemplateRenderer(TemplateOptions? options = null) => _options = options ?? new TemplateOptions();

    /// <inheritdoc />
    public string Render(string templateText, TemplateData data, TemplateFormat format = TemplateFormat.Text)
    {
        ArgumentNullException.ThrowIfNull(templateText);
        ArgumentNullException.ThrowIfNull(data);

        var template = GetTemplate(templateText);
        var globals = ToScript(data.Value, format, depth: 0) as ScriptObject ?? [];

        var context = new TemplateContext
        {
            StrictVariables = _options.MissingToken == MissingTokenBehavior.Throw,
            LoopLimit = LoopLimit,
            RecursiveLimit = RecursionLimit,
            LimitToString = StringLimit,
            OutputLimit = OutputLimit,
            TemplateLoader = null, // no "include": templates cannot read other files
        };
        context.PushGlobal(globals);
        context.PushCulture(_options.Culture);

        try
        {
            return template.Render(context);
        }
        catch (ScriptRuntimeException exception)
        {
            throw new TemplateRenderException($"The template could not be rendered: {exception.OriginalMessage}");
        }
        finally
        {
            context.PopCulture();
        }
    }

    /// <inheritdoc />
    public string? Validate(string templateText)
    {
        ArgumentNullException.ThrowIfNull(templateText);
        var template = Template.Parse(templateText);
        return template.HasErrors ? string.Join("; ", template.Messages.Select(m => m.Message)) : null;
    }

    private Template GetTemplate(string text)
    {
        if (_cache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        var template = Template.Parse(text);
        if (template.HasErrors)
        {
            throw new TemplateRenderException("The template is not valid: " + string.Join("; ", template.Messages.Select(m => m.Message)));
        }

        if (_cache.Count >= _options.MaxCachedTemplates)
        {
            _cache.Clear();
        }

        _cache[text] = template;
        return template;
    }

    // Builds plain script values (objects, arrays, strings, numbers) so templates can read data but never call .NET members.
    private static object? ToScript(object? value, TemplateFormat format, int depth)
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
            case bool or char or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or Enum:
                return value is Enum or Guid or char ? value.ToString() : value;
            case JsonElement element:
                return FromJson(element, format, depth);
            case IDictionary dictionary:
                return FromDictionary(dictionary.Keys.Cast<object>().Select(k => (k.ToString() ?? string.Empty, dictionary[k])), format, depth);
            case IEnumerable sequence:
                var array = new ScriptArray();
                foreach (var item in sequence)
                {
                    array.Add(ToScript(item, format, depth + 1));
                }

                return array;
            default:
                return FromObject(value, format, depth);
        }
    }

    private static ScriptObject FromDictionary(IEnumerable<(string Key, object? Value)> entries, TemplateFormat format, int depth)
    {
        var result = new ScriptObject();
        foreach (var (key, entry) in entries)
        {
            result.SetValue(key, ToScript(entry, format, depth + 1), readOnly: true);
        }

        return result;
    }

    private static ScriptObject FromObject(object value, TemplateFormat format, int depth)
    {
        var result = new ScriptObject();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0)
            {
                result.SetValue(property.Name, ToScript(property.GetValue(value), format, depth + 1), readOnly: true);
            }
        }

        return result;
    }

    private static object? FromJson(JsonElement element, TemplateFormat format, int depth) => element.ValueKind switch
    {
        JsonValueKind.Object => FromDictionary(element.EnumerateObject().Select(p => (p.Name, (object?)p.Value)), format, depth),
        JsonValueKind.Array => ToScript(element.EnumerateArray().Select(e => (object?)e).ToList(), format, depth),
        JsonValueKind.String => ToScript(element.GetString(), format, depth),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
