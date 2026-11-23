using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Email.Smtp;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the SMTP email provider.</summary>
public static class SmtpBuilderExtensions
{
    /// <summary>
    /// Adds SMTP as an email provider. Without <paramref name="name"/> it becomes the default email provider (and can
    /// also be selected as <c>"smtp"</c>); with a name it is only used when selected by that name.
    /// Settings can also come from configuration: <c>services.Configure&lt;SmtpOptions&gt;("smtp", section)</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Sets the options (Host, Port, UserName, Password, From, ...).</param>
    /// <param name="name">A name for a second SMTP server; <see langword="null"/> for the default.</param>
    public static INotifierBuilder AddSmtp(this INotifierBuilder builder, Action<SmtpOptions>? configure = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<SmtpOptions>(name ?? SmtpNotifier.Name);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SmtpOptions>, SmtpOptionsValidator>());

        return builder.AddNotifier<IEmailNotifier>(
            SmtpNotifier.Name,
            name,
            static (services, key) => new SmtpNotifier(
                services.GetRequiredService<IOptionsMonitor<SmtpOptions>>().Get(key),
                services.GetRequiredService<NotifierContext>()));
    }
}
