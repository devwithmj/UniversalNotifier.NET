using System.Globalization;

namespace UniversalNotifier.NET;

/// <summary>How token values are written into the output.</summary>
public enum TemplateFormat
{
    /// <summary>Values are inserted as they are.</summary>
    Text,

    /// <summary>Values are HTML-encoded so user data cannot inject markup. Use <c>{{{Token}}}</c> to insert raw HTML on purpose.</summary>
    Html,
}

/// <summary>What happens when a template uses a token the data does not contain.</summary>
public enum MissingTokenBehavior
{
    /// <summary>Throw a <see cref="TemplateRenderException"/>. The dispatcher turns it into a failed result. The default.</summary>
    Throw,

    /// <summary>Leave the <c>{{Token}}</c> text in the output.</summary>
    KeepToken,

    /// <summary>Replace the token with nothing.</summary>
    Empty,
}

/// <summary>Settings for <see cref="TokenTemplateRenderer"/>.</summary>
public sealed class TemplateOptions
{
    /// <summary>What to do with a token the data does not contain.</summary>
    public MissingTokenBehavior MissingToken { get; set; } = MissingTokenBehavior.Throw;

    /// <summary>The culture used to format numbers and dates. Invariant by default so output does not change per server.</summary>
    public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;

    /// <summary>How many parsed templates are cached. When the limit is reached the cache is cleared.</summary>
    public int MaxCachedTemplates { get; set; } = 512;
}

/// <summary>Turns a template and data into final text. Replace it to use Scriban, Fluid or any other engine.</summary>
public interface ITemplateRenderer
{
    /// <summary>Renders a template.</summary>
    /// <exception cref="TemplateRenderException">The template cannot be rendered with the given data.</exception>
    string Render(string templateText, TemplateData data, TemplateFormat format = TemplateFormat.Text);
}

/// <summary>A template could not be rendered.</summary>
public sealed class TemplateRenderException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TemplateRenderException(string message, string? token = null)
        : base(message) => Token = token;

    /// <summary>The token that caused the failure, when there is one.</summary>
    public string? Token { get; }
}
