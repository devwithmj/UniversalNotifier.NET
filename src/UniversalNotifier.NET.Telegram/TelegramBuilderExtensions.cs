using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Telegram;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Telegram provider.</summary>
public static class TelegramBuilderExtensions
{
    /// <summary>
    /// Adds Telegram as a provider. Without <paramref name="name"/> it becomes the default Telegram provider (and can
    /// also be selected as <c>"telegram"</c>); with a name it is only used when selected by that name.
    /// Settings can also come from configuration: <c>services.Configure&lt;TelegramOptions&gt;("telegram", section)</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Sets the options (BotToken).</param>
    /// <param name="name">A name for a second bot; <see langword="null"/> for the default.</param>
    public static INotifierBuilder AddTelegram(this INotifierBuilder builder, Action<TelegramOptions>? configure = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<TelegramOptions>(name ?? TelegramNotifier.Name);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TelegramOptions>, TelegramOptionsValidator>());

        return builder.AddNotifier<ITelegramNotifier>(
            TelegramNotifier.Name,
            name,
            static (services, key) => new TelegramNotifier(
                services.GetRequiredService<IOptionsMonitor<TelegramOptions>>().Get(key),
                services.GetRequiredService<NotifierContext>()));
    }
}
