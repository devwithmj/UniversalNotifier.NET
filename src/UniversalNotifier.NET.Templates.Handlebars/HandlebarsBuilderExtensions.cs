using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Templates.Handlebars;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Handlebars template renderer.</summary>
public static class HandlebarsBuilderExtensions
{
    /// <summary>
    /// Replaces the built-in <c>{{Token}}</c> renderer with Handlebars. Existing <c>{{ name }}</c> and
    /// <c>{{ user.name }}</c> templates keep working; <c>{{#if}}</c>, <c>{{#each}}</c> and <c>{{#with}}</c> become available.
    /// </summary>
    public static INotifierBuilder UseHandlebars(this INotifierBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<ITemplateRenderer>(
            static services => new HandlebarsTemplateRenderer(services.GetRequiredService<IOptions<TemplateOptions>>().Value)));
        return builder;
    }
}
