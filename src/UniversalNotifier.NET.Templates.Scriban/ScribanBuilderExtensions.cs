using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Templates.Scriban;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Scriban template renderer.</summary>
public static class ScribanBuilderExtensions
{
    /// <summary>
    /// Replaces the built-in <c>{{Token}}</c> renderer with Scriban. Existing <c>{{Name}}</c> and <c>{{User.Name}}</c> templates
    /// keep working; conditionals, loops and filters become available.
    /// </summary>
    public static INotifierBuilder UseScriban(this INotifierBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<ITemplateRenderer>(
            static services => new ScribanTemplateRenderer(services.GetRequiredService<IOptions<TemplateOptions>>().Value)));
        return builder;
    }
}
