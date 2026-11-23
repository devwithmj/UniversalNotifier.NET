using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;

namespace UniversalNotifier.NET;

/// <summary>
/// The built-in renderer. It replaces <c>{{Token}}</c> (and nested <c>{{User.Name}}</c>) in a single pass. It is not a
/// programming language: there are no expressions, loops or code, and values are never re-scanned for tokens, so
/// data cannot inject new placeholders. Templates are parsed once and cached.
/// </summary>
public sealed class TokenTemplateRenderer : ITemplateRenderer
{
    [ThreadStatic]
    private static StringBuilder? t_builder;

    private readonly TemplateOptions _options;
    private readonly ConcurrentDictionary<string, CompiledTemplate> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates a renderer.</summary>
    public TokenTemplateRenderer(TemplateOptions? options = null) => _options = options ?? new TemplateOptions();

    /// <inheritdoc />
    public string Render(string templateText, TemplateData data, TemplateFormat format = TemplateFormat.Text)
    {
        ArgumentNullException.ThrowIfNull(templateText);
        ArgumentNullException.ThrowIfNull(data);

        var compiled = GetOrCompile(templateText);
        if (!compiled.HasTokens)
        {
            return templateText;
        }

        var builder = t_builder ?? new StringBuilder(256);
        t_builder = null;
        builder.Clear();

        foreach (var segment in compiled.Segments)
        {
            if (segment.Path is null)
            {
                builder.Append(segment.Text);
            }
            else if (data.TryGetValue(segment.Path, out var value))
            {
                var text = ToText(value);
                builder.Append(format == TemplateFormat.Html && !segment.Raw ? WebUtility.HtmlEncode(text) : text);
            }
            else
            {
                switch (_options.MissingToken)
                {
                    case MissingTokenBehavior.Throw:
                        throw new TemplateRenderException($"Template token '{string.Join('.', segment.Path)}' has no value.", string.Join('.', segment.Path));
                    case MissingTokenBehavior.KeepToken:
                        builder.Append(segment.Text);
                        break;
                }
            }
        }

        var result = builder.ToString();
        if (builder.Capacity <= 16 * 1024)
        {
            t_builder = builder;
        }

        return result;
    }

    private string ToText(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        IFormattable formattable => formattable.ToString(null, _options.Culture),
        _ => value.ToString() ?? string.Empty,
    };

    private CompiledTemplate GetOrCompile(string template)
    {
        if (_cache.TryGetValue(template, out var cached))
        {
            return cached;
        }

        var compiled = CompiledTemplate.Parse(template);
        if (_cache.Count >= _options.MaxCachedTemplates)
        {
            _cache.Clear();
        }

        _cache[template] = compiled;
        return compiled;
    }

    /// <summary>A literal (Path is null, Text is the literal) or a token (Path is set, Text is the original source).</summary>
    private readonly record struct Segment(string Text, string[]? Path, bool Raw);

    private sealed class CompiledTemplate
    {
        private CompiledTemplate(Segment[] segments, bool hasTokens)
        {
            Segments = segments;
            HasTokens = hasTokens;
        }

        public Segment[] Segments { get; }

        public bool HasTokens { get; }

        public static CompiledTemplate Parse(string template)
        {
            var segments = new List<Segment>();
            var hasTokens = false;
            var literalStart = 0;
            var position = 0;

            while (position < template.Length)
            {
                var open = template.IndexOf("{{", position, StringComparison.Ordinal);
                if (open < 0)
                {
                    break;
                }

                var raw = open + 2 < template.Length && template[open + 2] == '{';
                var start = open + (raw ? 3 : 2);
                var close = template.IndexOf(raw ? "}}}" : "}}", start, StringComparison.Ordinal);
                if (close < 0)
                {
                    // "{{{x}}" is a literal brace followed by a normal token; anything else is plain text.
                    position = raw ? open + 1 : template.Length;
                    continue;
                }

                var path = ParsePath(template.AsSpan(start, close - start));
                var end = close + (raw ? 3 : 2);
                if (path is null)
                {
                    position = open + 2;
                    continue;
                }

                if (open > literalStart)
                {
                    segments.Add(new Segment(template[literalStart..open], null, false));
                }

                segments.Add(new Segment(template[open..end], path, raw));
                hasTokens = true;
                position = literalStart = end;
            }

            if (literalStart < template.Length)
            {
                segments.Add(new Segment(template[literalStart..], null, false));
            }

            return new CompiledTemplate([.. segments], hasTokens);
        }

        // Returns null when the text is not a token (for example CSS such as "{{ color: red }}"), so it stays literal.
        private static string[]? ParsePath(ReadOnlySpan<char> text)
        {
            text = text.Trim();
            if (text.IsEmpty)
            {
                return null;
            }

            foreach (var c in text)
            {
                if (!(char.IsLetterOrDigit(c) || c is '_' or '.' or '-'))
                {
                    return null;
                }
            }

            var path = text.ToString().Split('.');
            return Array.Exists(path, static part => part.Length == 0) ? null : path;
        }
    }
}
