using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET;

/// <summary>
/// Sets up notifications without a host or dependency injection, for console apps and scripts:
/// <code>var notifier = new NotifierBuilder().AddMailgun(o => { ... }).Build();</code>
/// Dispose the result when the app ends.
/// </summary>
public sealed class NotifierBuilder : INotifierBuilder
{
    /// <inheritdoc />
    public IServiceCollection Services { get; } = new ServiceCollection();

    /// <summary>Builds the notifier. Invalid provider settings throw here, not at the first send.</summary>
    public Notifier Build()
    {
        Services.AddCoreNotifierServices();
        var provider = Services.BuildServiceProvider();
        try
        {
            // Creating every registered notifier (and reading the shared options) runs options validation now,
            // so a bad setting throws from Build() instead of from the first send.
            _ = provider.GetRequiredService<IOptions<NotifierOptions>>().Value;
            foreach (var registration in provider.GetServices<NotifierRegistration>())
            {
                _ = provider.GetRequiredKeyedService(registration.Service, registration.Key);
            }

            return new Notifier(provider);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }
}

/// <summary>
/// The result of <see cref="NotifierBuilder.Build"/>: direct access to each channel's notifier plus the multi-channel
/// <see cref="INotificationService"/>. Not needed when you use dependency injection.
/// </summary>
public sealed class Notifier : INotificationService, IDisposable, IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly INotificationService _service;

    internal Notifier(ServiceProvider provider)
    {
        _provider = provider;
        _service = provider.GetRequiredService<INotificationService>();
    }

    /// <summary>The default email notifier.</summary>
    public IEmailNotifier Email => Get<IEmailNotifier>("email");

    /// <summary>The default SMS notifier.</summary>
    public ISmsNotifier Sms => Get<ISmsNotifier>("SMS");

    /// <summary>The default mobile push notifier.</summary>
    public IPushNotifier Push => Get<IPushNotifier>("push");

    /// <summary>The default Telegram notifier.</summary>
    public ITelegramNotifier Telegram => Get<ITelegramNotifier>("Telegram");

    /// <summary>The default in-app notifier.</summary>
    public IInAppNotifier InApp => Get<IInAppNotifier>("in-app");

    /// <summary>The default webhook notifier.</summary>
    public IWebhookNotifier Webhook => Get<IWebhookNotifier>("webhook");

    /// <summary>The default browser push notifier.</summary>
    public IWebPushNotifier WebPush => Get<IWebPushNotifier>("web push");

    /// <inheritdoc />
    public Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken = default) =>
        _service.SendAsync(notification, cancellationToken);

    /// <summary>Gets a notifier by type and optional provider name.</summary>
    /// <exception cref="InvalidOperationException">No such provider is registered.</exception>
    public TNotifier GetNotifier<TNotifier>(string? provider = null) where TNotifier : class =>
        (provider is null ? _provider.GetService<TNotifier>() : _provider.GetKeyedService<TNotifier>(provider))
        ?? throw new InvalidOperationException(provider is null
            ? $"No {typeof(TNotifier).Name} is registered. Add a provider on the builder first."
            : $"No {typeof(TNotifier).Name} named '{provider}' is registered.");

    /// <inheritdoc />
    public void Dispose() => _provider.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    private TNotifier Get<TNotifier>(string channel) where TNotifier : class =>
        _provider.GetService<TNotifier>()
        ?? throw new InvalidOperationException($"No {channel} provider is registered. Add one on the builder before calling Build().");
}
