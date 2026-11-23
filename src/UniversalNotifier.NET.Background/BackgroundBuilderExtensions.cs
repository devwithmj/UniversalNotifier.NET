using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Background;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers background delivery.</summary>
public static class BackgroundBuilderExtensions
{
    /// <summary>
    /// Adds background delivery: inject <see cref="INotificationQueue"/> and call <c>EnqueueAsync</c> to return to your
    /// caller immediately while a hosted worker delivers the notification. Needs the Generic Host (ASP.NET Core, Worker
    /// Service, <c>Host.CreateDefaultBuilder</c>) because the worker is a hosted service.
    /// </summary>
    /// <exception cref="NotSupportedException">Used with <see cref="NotifierBuilder"/>, which has no host to run the worker.</exception>
    public static INotifierBuilder AddBackgroundDelivery(this INotifierBuilder builder, Action<BackgroundDeliveryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder is NotifierBuilder)
        {
            throw new NotSupportedException("Background delivery runs as a hosted service, so it needs the Generic Host. Use AddUniversalNotifier on the host's services, or send immediately with NotifierBuilder.");
        }

        var options = builder.Services.AddOptions<BackgroundDeliveryOptions>()
            .Validate(static o => BackgroundDeliveryOptions.Validate(o) is null, "Background delivery needs Capacity of at least 1 and MaxConcurrency between 1 and 1024.");
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        builder.Services.TryAddSingleton<INotificationQueue, ChannelNotificationQueue>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NotificationDeliveryService>());
        return builder;
    }
}
