using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Email.Mailgun;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Mailgun email provider.</summary>
public static class MailgunBuilderExtensions
{
    /// <summary>
    /// Adds Mailgun as an email provider. Without <paramref name="name"/> it becomes the default email provider (and can
    /// also be selected as <c>"mailgun"</c>); with a name it is only used when selected by that name.
    /// Settings can also come from configuration: <c>services.Configure&lt;MailgunOptions&gt;("mailgun", section)</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Sets the options (ApiKey, Domain, From, ...).</param>
    /// <param name="name">A name for a second Mailgun account or domain; <see langword="null"/> for the default.</param>
    public static INotifierBuilder AddMailgun(this INotifierBuilder builder, Action<MailgunOptions>? configure = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var key = name ?? MailgunNotifier.Name;
        var options = builder.Services.AddOptions<MailgunOptions>(key);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MailgunOptions>, MailgunOptionsValidator>());
        builder.Services.AddHttpClient(HttpClientName(key), (services, client) =>
            client.Timeout = services.GetRequiredService<IOptionsMonitor<MailgunOptions>>().Get(key).Timeout);

        return builder.AddNotifier<IEmailNotifier>(
            MailgunNotifier.Name,
            name,
            static (services, key) => new MailgunNotifier(
                services.GetRequiredService<IOptionsMonitor<MailgunOptions>>().Get(key),
                services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName(key)),
                services.GetRequiredService<NotifierContext>()));
    }

    private static string HttpClientName(string key) => "UniversalNotifier.Mailgun." + key;
}
