using Microsoft.Extensions.DependencyInjection;

namespace UniversalNotifier.NET;

/// <summary>A renderer that can check a template without data, so mistakes are found at startup instead of at the first send.</summary>
public interface IValidatingTemplateRenderer : ITemplateRenderer
{
    /// <summary>Returns a description of what is wrong with the template, or <see langword="null"/> when it is valid.</summary>
    string? Validate(string templateText);
}

/// <summary>A template store that can list everything it holds, which is what startup validation needs.</summary>
public interface IListableTemplateStore : ITemplateStore
{
    /// <summary>Lists every template this store holds.</summary>
    IEnumerable<TemplateEntry> List();
}

/// <summary>One template held by a store.</summary>
/// <param name="Name">The template name.</param>
/// <param name="Channel">The channel it is for.</param>
/// <param name="Content">Its content.</param>
public sealed record TemplateEntry(string Name, NotificationChannel Channel, ChannelContent Content);

/// <summary>One problem found by <see cref="TemplateValidator"/>.</summary>
/// <param name="Template">The template name.</param>
/// <param name="Channel">The channel.</param>
/// <param name="Part">Which part is wrong (<c>Subject</c>, <c>Title</c>, <c>Text</c>, <c>Html</c> or <c>Data</c>).</param>
/// <param name="Message">What is wrong.</param>
public sealed record TemplateProblem(string Template, NotificationChannel Channel, string Part, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Template}/{Channel} {Part}: {Message}";
}

/// <summary>
/// Checks every template in the listable stores with the current renderer. The built-in renderer accepts any text (a stray
/// <c>{{</c> stays literal by design), so this matters with the Scriban and Fluid renderers, where a syntax error would
/// otherwise surface only when that notification is first sent.
/// </summary>
public static class TemplateValidator
{
    /// <summary>Returns the problems found; an empty list means every listed template is valid.</summary>
    public static IReadOnlyList<TemplateProblem> Validate(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.GetService<ITemplateRenderer>() is not IValidatingTemplateRenderer renderer)
        {
            return [];
        }

        var problems = new List<TemplateProblem>();
        foreach (var store in services.GetServices<ITemplateStore>().OfType<IListableTemplateStore>())
        {
            foreach (var (name, channel, content) in store.List())
            {
                Check(renderer, problems, name, channel, nameof(content.Subject), content.Subject);
                Check(renderer, problems, name, channel, nameof(content.Title), content.Title);
                Check(renderer, problems, name, channel, nameof(content.Text), content.Text);
                Check(renderer, problems, name, channel, nameof(content.Html), content.Html);
                foreach (var (key, value) in content.Data ?? new Dictionary<string, string>())
                {
                    Check(renderer, problems, name, channel, $"{nameof(content.Data)}[{key}]", value);
                }
            }
        }

        return problems;
    }

    /// <summary>Throws a <see cref="TemplateRenderException"/> listing every problem. Call it once at startup.</summary>
    /// <exception cref="TemplateRenderException">At least one template is invalid.</exception>
    public static void ThrowIfInvalid(IServiceProvider services)
    {
        var problems = Validate(services);
        if (problems.Count > 0)
        {
            throw new TemplateRenderException("Invalid templates: " + string.Join("; ", problems));
        }
    }

    private static void Check(IValidatingTemplateRenderer renderer, List<TemplateProblem> problems, string name, NotificationChannel channel, string part, string? text)
    {
        if (text is not null && renderer.Validate(text) is { } message)
        {
            problems.Add(new TemplateProblem(name, channel, part, message));
        }
    }
}
