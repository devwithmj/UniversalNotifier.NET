using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers UniversalNotifier.NET with dependency injection.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the notification services. Add providers inside <paramref name="configure"/>:
    /// <code>services.AddUniversalNotifier(n =&gt; n.AddMailgun(o =&gt; { ... }));</code>
    /// </summary>
    public static IServiceCollection AddUniversalNotifier(this IServiceCollection services, Action<INotifierBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddCoreNotifierServices();
        configure?.Invoke(new ServiceCollectionNotifierBuilder(services));
        return services;
    }

    internal static IServiceCollection AddCoreNotifierServices(this IServiceCollection services)
    {
        services.AddOptions<NotifierOptions>()
            .Validate(
                static o => o.Retry.MaxAttempts is >= 1 and <= 10 && o.Retry.BaseDelay >= TimeSpan.Zero && o.Retry.MaxDelay >= o.Retry.BaseDelay,
                "Retry needs 1 to 10 attempts and a MaxDelay that is not shorter than BaseDelay.")
            .ValidateOnStart();
        services.AddOptions<TemplateOptions>()
            .Validate(static o => o.MaxCachedTemplates >= 1 && o.Culture is not null, "Templates need a culture and a cache size of at least 1.")
            .ValidateOnStart();

        services.TryAddSingleton(static services => new NotifierContext(
            services.GetRequiredService<IOptions<NotifierOptions>>().Value,
            services.GetService<ILoggerFactory>(),
            services.GetService<TimeProvider>()));
        services.TryAddSingleton<ITemplateRenderer>(static services =>
            new TokenTemplateRenderer(services.GetRequiredService<IOptions<TemplateOptions>>().Value));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITemplateStore, InMemoryTemplateStore>());
        services.TryAddSingleton<INotificationService, NotificationService>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, EmailChannel>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, SmsChannel>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, PushChannel>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, WebPushChannel>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, TelegramChannel>());
        return services;
    }

    private sealed class ServiceCollectionNotifierBuilder(IServiceCollection services) : INotifierBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
