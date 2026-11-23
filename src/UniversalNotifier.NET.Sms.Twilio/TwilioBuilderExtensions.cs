using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Sms.Twilio;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Twilio SMS provider.</summary>
public static class TwilioBuilderExtensions
{
    /// <summary>
    /// Adds Twilio as an SMS provider. Without <paramref name="name"/> it becomes the default SMS provider (and can
    /// also be selected as <c>"twilio"</c>); with a name it is only used when selected by that name.
    /// Settings can also come from configuration: <c>services.Configure&lt;TwilioSmsOptions&gt;("twilio", section)</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Sets the options (AccountSid, AuthToken or ApiKeySid and ApiKeySecret, From, ...).</param>
    /// <param name="name">A name for a second Twilio account or sender; <see langword="null"/> for the default.</param>
    public static INotifierBuilder AddTwilioSms(this INotifierBuilder builder, Action<TwilioSmsOptions>? configure = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var key = name ?? TwilioSmsNotifier.Name;
        var options = builder.Services.AddOptions<TwilioSmsOptions>(key);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TwilioSmsOptions>, TwilioSmsOptionsValidator>());
        builder.Services.AddHttpClient(HttpClientName(key), (services, client) =>
            client.Timeout = services.GetRequiredService<IOptionsMonitor<TwilioSmsOptions>>().Get(key).Timeout);

        return builder.AddNotifier<ISmsNotifier>(
            TwilioSmsNotifier.Name,
            name,
            static (services, key) => new TwilioSmsNotifier(
                services.GetRequiredService<IOptionsMonitor<TwilioSmsOptions>>().Get(key),
                services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName(key)),
                services.GetRequiredService<NotifierContext>()));
    }

    private static string HttpClientName(string key) => "UniversalNotifier.Twilio." + key;
}
