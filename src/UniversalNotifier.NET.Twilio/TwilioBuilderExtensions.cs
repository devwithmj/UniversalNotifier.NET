using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Twilio;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Which Twilio channels to register.</summary>
[Flags]
public enum TwilioChannels
{
    /// <summary>SMS (<see cref="ISmsNotifier"/>).</summary>
    Sms = 1,

    /// <summary>WhatsApp (<see cref="IWhatsAppNotifier"/>).</summary>
    WhatsApp = 2,

    /// <summary>Both channels.</summary>
    All = Sms | WhatsApp,
}

/// <summary>Registers the Twilio SMS and WhatsApp providers.</summary>
public static class TwilioBuilderExtensions
{
    /// <summary>
    /// Adds Twilio as the SMS and WhatsApp provider. Without <paramref name="name"/> it becomes the default provider of each
    /// channel it serves (and can also be selected as <c>"twilio"</c>); with a name it is only used when selected by that name.
    /// Pass <paramref name="channels"/> to register one channel only, so Twilio does not become the default of a channel another
    /// provider should serve. Settings can also come from configuration: <c>services.Configure&lt;TwilioOptions&gt;("twilio", section)</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Sets the options (AccountSid, AuthToken or ApiKeySid and ApiKeySecret, From, WhatsAppFrom, ...).</param>
    /// <param name="name">A name for a second Twilio account or sender; <see langword="null"/> for the default.</param>
    /// <param name="channels">The channels to register; both by default.</param>
    public static INotifierBuilder AddTwilio(this INotifierBuilder builder, Action<TwilioOptions>? configure = null, string? name = null, TwilioChannels channels = TwilioChannels.All)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var key = name ?? TwilioSmsNotifier.Name;
        var options = builder.Services.AddOptions<TwilioOptions>(key);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TwilioOptions>, TwilioOptionsValidator>());
        builder.Services.AddHttpClient(HttpClientName(key), (services, client) =>
            client.Timeout = services.GetRequiredService<IOptionsMonitor<TwilioOptions>>().Get(key).Timeout);

        if (channels.HasFlag(TwilioChannels.Sms))
        {
            builder.AddNotifier<ISmsNotifier>(
                TwilioSmsNotifier.Name,
                name,
                static (services, key) => new TwilioSmsNotifier(
                    services.GetRequiredService<IOptionsMonitor<TwilioOptions>>().Get(key),
                    services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName(key)),
                    services.GetRequiredService<NotifierContext>()));
        }

        if (channels.HasFlag(TwilioChannels.WhatsApp))
        {
            builder.AddNotifier<IWhatsAppNotifier>(
                TwilioWhatsAppNotifier.Name,
                name,
                static (services, key) => new TwilioWhatsAppNotifier(
                    services.GetRequiredService<IOptionsMonitor<TwilioOptions>>().Get(key),
                    services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName(key)),
                    services.GetRequiredService<NotifierContext>()));
        }

        return builder;
    }

    private static string HttpClientName(string key) => "UniversalNotifier.Twilio." + key;
}
