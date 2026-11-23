using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Email.Ses;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Amazon SES email provider.</summary>
public static class SesBuilderExtensions
{
    /// <summary>
    /// Adds Amazon SES as an email provider. Without <paramref name="name"/> it becomes the default email provider (and can
    /// also be selected as <c>"ses"</c>); with a name it is only used when selected by that name.
    /// Settings can also come from configuration: <c>services.Configure&lt;SesOptions&gt;("ses", section)</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Sets the options (Region, AccessKeyId, SecretAccessKey, From, ...).</param>
    /// <param name="name">A name for a second SES account or region; <see langword="null"/> for the default.</param>
    public static INotifierBuilder AddSes(this INotifierBuilder builder, Action<SesOptions>? configure = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var key = name ?? SesNotifier.Name;
        var options = builder.Services.AddOptions<SesOptions>(key);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SesOptions>, SesOptionsValidator>());
        builder.Services.AddHttpClient(HttpClientName(key), (services, client) =>
            client.Timeout = services.GetRequiredService<IOptionsMonitor<SesOptions>>().Get(key).Timeout);

        return builder.AddNotifier<IEmailNotifier>(
            SesNotifier.Name,
            name,
            static (services, key) => new SesNotifier(
                services.GetRequiredService<IOptionsMonitor<SesOptions>>().Get(key),
                services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName(key)),
                services.GetRequiredService<NotifierContext>()));
    }

    private static string HttpClientName(string key) => "UniversalNotifier.Ses." + key;
}
