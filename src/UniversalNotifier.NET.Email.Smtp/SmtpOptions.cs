using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET.Email.Smtp;

/// <summary>How the connection to the SMTP server is secured.</summary>
public enum SmtpSecurity
{
    /// <summary>Upgrade with STARTTLS (usually port 587) and fail if the server does not support it. The default.</summary>
    StartTls,

    /// <summary>TLS from the first byte (usually port 465).</summary>
    SslOnConnect,

    /// <summary>Use STARTTLS when the server offers it, otherwise continue unencrypted. Not recommended.</summary>
    StartTlsWhenAvailable,

    /// <summary>No encryption. Only for local test servers.</summary>
    None,
}

/// <summary>Settings for <see cref="SmtpNotifier"/>.</summary>
public sealed class SmtpOptions
{
    /// <summary>The SMTP server host name.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The port. 587 for STARTTLS, 465 for implicit TLS.</summary>
    public int Port { get; set; } = 587;

    /// <summary>How the connection is secured.</summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>The login name. Leave empty for servers without authentication.</summary>
    public string? UserName { get; set; }

    /// <summary>The password. Keep it in a secret store, not in source control.</summary>
    public string? Password { get; set; }

    /// <summary>The default sender. A message can override it with <see cref="EmailMessage.From"/>.</summary>
    public EmailAddress From { get; set; }

    /// <summary>How long connecting and each server reply may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Allows sending the login over an unencrypted connection. Leave off outside local testing.</summary>
    public bool AllowInsecureAuthentication { get; set; }

    /// <summary>Never prints the password.</summary>
    public override string ToString() => $"SmtpOptions {{ Host = {Host}, Port = {Port}, Security = {Security} }}";

    internal static string? Validate(SmtpOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            return "SMTP Host is required.";
        }

        if (options.Port is < 1 or > 65535)
        {
            return "SMTP Port must be between 1 and 65535.";
        }

        if (!options.From.IsValid)
        {
            return "SMTP From must be a valid email address.";
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            return "SMTP Timeout must be positive.";
        }

        if (string.IsNullOrEmpty(options.UserName) != string.IsNullOrEmpty(options.Password))
        {
            return "SMTP UserName and Password must be set together.";
        }

        if (!string.IsNullOrEmpty(options.UserName) && options.Security == SmtpSecurity.None && !options.AllowInsecureAuthentication)
        {
            return "SMTP credentials must not be sent over an unencrypted connection. Use StartTls or SslOnConnect, or set AllowInsecureAuthentication for a local test server.";
        }

        return null;
    }
}

internal sealed class SmtpOptionsValidator : IValidateOptions<SmtpOptions>
{
    public ValidateOptionsResult Validate(string? name, SmtpOptions options) =>
        SmtpOptions.Validate(options) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
