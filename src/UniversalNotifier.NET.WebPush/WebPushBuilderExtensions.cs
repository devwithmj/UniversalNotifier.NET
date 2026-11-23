using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.WebPush;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the browser Web Push provider.</summary>
public static class WebPushBuilderExtensions
{
    /// <summary>
    /// Adds Web Push (VAPID) as a provider. Without <paramref name="name"/> it becomes the default web push provider (and can
    /// also be selected as <c>"webpush"</c>); with a name it is only used when selected by that name.
    /// Settings can also come from configuration: <c>services.Configure&lt;WebPushOptions&gt;("webpush", section)</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Sets the options (PublicKey, PrivateKey, Subject).</param>
    /// <param name="name">A name for a second key pair; <see langword="null"/> for the default.</param>
    public static INotifierBuilder AddWebPush(this INotifierBuilder builder, Action<WebPushOptions>? configure = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<WebPushOptions>(name ?? WebPushNotifier.Name);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WebPushOptions>, WebPushOptionsValidator>());

        return builder.AddNotifier<IWebPushNotifier>(
            WebPushNotifier.Name,
            name,
            static (services, key) => new WebPushNotifier(
                services.GetRequiredService<IOptionsMonitor<WebPushOptions>>().Get(key),
                services.GetRequiredService<NotifierContext>()));
    }
}
