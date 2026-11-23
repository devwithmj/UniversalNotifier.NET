using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Webhook;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the webhook provider (Slack, Microsoft Teams, generic JSON).</summary>
public static class WebhookBuilderExtensions
{
    /// <summary>
    /// Adds webhooks as a provider. Without <paramref name="name"/> it becomes the default webhook provider (and can
    /// also be selected as <c>"webhook"</c>); with a name it is only used when selected by that name.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Adds endpoints: <c>o.Add("ops", url, WebhookFormat.Slack)</c>.</param>
    /// <param name="name">A name for a second set of endpoints; <see langword="null"/> for the default.</param>
    public static INotifierBuilder AddWebhook(this INotifierBuilder builder, Action<WebhookOptions>? configure = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<WebhookOptions>(name ?? WebhookNotifier.Name);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WebhookOptions>, WebhookOptionsValidator>());

        return builder.AddNotifier<IWebhookNotifier>(
            WebhookNotifier.Name,
            name,
            static (services, key) => new WebhookNotifier(
                services.GetRequiredService<IOptionsMonitor<WebhookOptions>>().Get(key),
                services.GetRequiredService<NotifierContext>()));
    }
}
