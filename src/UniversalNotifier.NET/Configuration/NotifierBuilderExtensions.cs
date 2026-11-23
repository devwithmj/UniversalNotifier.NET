using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace UniversalNotifier.NET;

/// <summary>Configuration methods available on every <see cref="INotifierBuilder"/>.</summary>
public static class NotifierBuilderExtensions
{
    /// <summary>
    /// Registers a notifier for a channel. Use this to write your own provider: an unnamed registration becomes the
    /// channel's default and is also reachable by <paramref name="providerName"/>; a named registration is reachable only by its name.
    /// </summary>
    /// <typeparam name="TChannel">The channel interface, for example <see cref="IEmailNotifier"/>.</typeparam>
    /// <param name="builder">The builder.</param>
    /// <param name="providerName">The provider's own name, for example <c>mailgun</c>.</param>
    /// <param name="name">A custom name to register a second instance of the same provider, or <see langword="null"/> for the default.</param>
    /// <param name="factory">Creates the notifier. It receives the services and the registration key (<paramref name="name"/> or <paramref name="providerName"/>), which is also the options name.</param>
    public static INotifierBuilder AddNotifier<TChannel>(
        this INotifierBuilder builder,
        string providerName,
        string? name,
        Func<IServiceProvider, string, TChannel> factory)
        where TChannel : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(factory);

        var key = name ?? providerName;
        builder.Services.AddSingleton(new NotifierRegistration(typeof(TChannel), key));
        builder.Services.AddKeyedSingleton<TChannel>(key, (services, _) => factory(services, key));
        if (name is null)
        {
            builder.Services.AddSingleton<TChannel>(services => services.GetRequiredKeyedService<TChannel>(key));
        }

        return builder;
    }

    /// <summary>
    /// Builds the notifier. Lets a chain of configuration calls end in <c>.Build()</c> even though those calls return
    /// <see cref="INotifierBuilder"/>. Only valid for <see cref="NotifierBuilder"/>; with dependency injection the container builds everything.
    /// </summary>
    /// <exception cref="InvalidOperationException">The builder is not a <see cref="NotifierBuilder"/>.</exception>
    public static Notifier Build(this INotifierBuilder builder) =>
        builder is NotifierBuilder standalone
            ? standalone.Build()
            : throw new InvalidOperationException("Build() is only for NotifierBuilder. With dependency injection, resolve INotificationService from the container instead.");

    /// <summary>Registers a handler for a custom channel (or replaces a built-in one).</summary>
    public static INotifierBuilder AddChannel<THandler>(this INotifierBuilder builder) where THandler : class, INotificationChannel
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<INotificationChannel, THandler>();
        return builder;
    }

    /// <summary>
    /// Turns on automatic retries. Only failures that are safe to repeat are retried, and never after a partial success.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="maxAttempts">Total attempts including the first (2 to 10).</param>
    /// <param name="baseDelay">The wait before the first retry; it doubles each attempt. Defaults to 500 ms.</param>
    /// <param name="maxDelay">The longest wait. Defaults to 30 seconds.</param>
    /// <param name="retryUnknownFailures">Also retry timeouts and dropped connections. This can send duplicates.</param>
    public static INotifierBuilder WithRetries(
        this INotifierBuilder builder,
        int maxAttempts = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        bool retryUnknownFailures = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure<NotifierOptions>(options =>
        {
            options.Retry.MaxAttempts = maxAttempts;
            options.Retry.BaseDelay = baseDelay ?? options.Retry.BaseDelay;
            options.Retry.MaxDelay = maxDelay ?? options.Retry.MaxDelay;
            options.Retry.RetryUnknownFailures = retryUnknownFailures;
        });
        return builder;
    }

    /// <summary>
    /// Spaces the calls each notifier makes to its provider so you stay under the provider's rate limit. Calls wait their
    /// turn instead of failing. Applies to every provider; waiting counts toward the caller's cancellation token.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="permitsPerSecond">The sustained calls per second, for example 10.</param>
    /// <param name="burst">How many calls may go out at once before spacing starts.</param>
    public static INotifierBuilder WithRateLimit(this INotifierBuilder builder, double permitsPerSecond, int burst = 1)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure<NotifierOptions>(options =>
        {
            options.RateLimit.PermitsPerSecond = permitsPerSecond;
            options.RateLimit.Burst = burst;
        });
        return builder;
    }

    /// <summary>Changes template settings such as what happens to a missing token.</summary>
    public static INotifierBuilder ConfigureTemplates(this INotifierBuilder builder, Action<TemplateOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.Configure(configure);
        return builder;
    }

    /// <summary>Registers a template for a channel.</summary>
    public static INotifierBuilder AddTemplate(this INotifierBuilder builder, string name, NotificationChannel channel, ChannelContent content)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        builder.Services.AddSingleton(new TemplateRegistration(name, channel, content));
        return builder;
    }

    /// <summary>Loads templates from a folder. See <see cref="DirectoryTemplateStore"/> for the file layout.</summary>
    public static INotifierBuilder AddTemplatesFromDirectory(this INotifierBuilder builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<ITemplateStore>(new DirectoryTemplateStore(path));
        return builder;
    }

    /// <summary>Loads templates compiled into an assembly. See <see cref="EmbeddedTemplateStore"/> for the layout.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="assembly">The assembly that holds the embedded resources.</param>
    /// <param name="resourcePrefix">The resource prefix of the templates folder, for example <c>MyApp.Templates</c>.</param>
    public static INotifierBuilder AddEmbeddedTemplates(this INotifierBuilder builder, System.Reflection.Assembly assembly, string resourcePrefix)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<ITemplateStore>(new EmbeddedTemplateStore(assembly, resourcePrefix));
        return builder;
    }

    /// <summary>Adds a custom template store, for example one backed by a database. Stores are asked in registration order.</summary>
    public static INotifierBuilder AddTemplateStore<TStore>(this INotifierBuilder builder) where TStore : class, ITemplateStore
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<ITemplateStore, TStore>();
        return builder;
    }

    /// <summary>Replaces the built-in token renderer, for example with an adapter for Scriban or Fluid.</summary>
    public static INotifierBuilder UseTemplateRenderer<TRenderer>(this INotifierBuilder builder) where TRenderer : class, ITemplateRenderer
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<ITemplateRenderer, TRenderer>());
        return builder;
    }

    /// <summary>Uses a logger factory. Only needed outside dependency injection, where there is no logging by default.</summary>
    public static INotifierBuilder UseLoggerFactory(this INotifierBuilder builder, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        builder.Services.AddSingleton(loggerFactory);
        return builder;
    }
}

/// <summary>Remembers which notifiers were registered so <see cref="NotifierBuilder.Build"/> can create them eagerly.</summary>
internal sealed record NotifierRegistration(Type Service, string Key);
