using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Templates.Fluid;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Fluid template renderer.</summary>
public static class FluidBuilderExtensions
{
    /// <summary>
    /// Replaces the built-in <c>{{Token}}</c> renderer with Fluid (Liquid syntax). Existing <c>{{ name }}</c> and
    /// <c>{{ user.name }}</c> templates keep working; conditionals, loops and filters become available.
    /// </summary>
    public static INotifierBuilder UseFluid(this INotifierBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<ITemplateRenderer>(
            static services => new FluidTemplateRenderer(services.GetRequiredService<IOptions<TemplateOptions>>().Value)));
        return builder;
    }
}
