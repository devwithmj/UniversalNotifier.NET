using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Push.Fcm;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Firebase Cloud Messaging push provider.</summary>
public static class FcmBuilderExtensions
{
    /// <summary>
    /// Adds FCM as a mobile push provider. Without <paramref name="name"/> it becomes the default push provider (and can
    /// also be selected as <c>"fcm"</c>); with a name it is only used when selected by that name.
    /// Settings can also come from configuration: <c>services.Configure&lt;FcmPushOptions&gt;("fcm", section)</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Sets the options (CredentialJson or CredentialPath, ProjectId).</param>
    /// <param name="name">A name for a second Firebase project; <see langword="null"/> for the default.</param>
    public static INotifierBuilder AddFcm(this INotifierBuilder builder, Action<FcmPushOptions>? configure = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<FcmPushOptions>(name ?? FcmPushNotifier.Name);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FcmPushOptions>, FcmPushOptionsValidator>());

        return builder.AddNotifier<IPushNotifier>(
            FcmPushNotifier.Name,
            name,
            static (services, key) => new FcmPushNotifier(
                services.GetRequiredService<IOptionsMonitor<FcmPushOptions>>().Get(key),
                services.GetRequiredService<NotifierContext>()));
    }
}
