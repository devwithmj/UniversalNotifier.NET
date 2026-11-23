using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Email.SendGrid;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the SendGrid email provider.</summary>
public static class SendGridBuilderExtensions
{
    /// <summary>
    /// Adds SendGrid as an email provider. Without <paramref name="name"/> it becomes the default email provider (and can
    /// also be selected as <c>"sendgrid"</c>); with a name it is only used when selected by that name.
    /// Settings can also come from configuration: <c>services.Configure&lt;SendGridOptions&gt;("sendgrid", section)</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Sets the options (ApiKey, From, ...).</param>
    /// <param name="name">A name for a second SendGrid account; <see langword="null"/> for the default.</param>
    public static INotifierBuilder AddSendGrid(this INotifierBuilder builder, Action<SendGridOptions>? configure = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var key = name ?? SendGridNotifier.Name;
        var options = builder.Services.AddOptions<SendGridOptions>(key);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SendGridOptions>, SendGridOptionsValidator>());
        builder.Services.AddHttpClient(HttpClientName(key), (services, client) =>
            client.Timeout = services.GetRequiredService<IOptionsMonitor<SendGridOptions>>().Get(key).Timeout);

        return builder.AddNotifier<IEmailNotifier>(
            SendGridNotifier.Name,
            name,
            static (services, key) => new SendGridNotifier(
                services.GetRequiredService<IOptionsMonitor<SendGridOptions>>().Get(key),
                services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName(key)),
                services.GetRequiredService<NotifierContext>()));
    }

    private static string HttpClientName(string key) => "UniversalNotifier.SendGrid." + key;
}
