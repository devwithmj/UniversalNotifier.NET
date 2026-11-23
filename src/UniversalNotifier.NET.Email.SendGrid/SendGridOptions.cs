using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET.Email.SendGrid;

/// <summary>Which SendGrid region the account uses.</summary>
public enum SendGridRegion
{
    /// <summary><c>api.sendgrid.com</c>.</summary>
    Us,

    /// <summary><c>api.eu.sendgrid.com</c>, for EU-regional subusers.</summary>
    Eu,
}

/// <summary>Settings for <see cref="SendGridNotifier"/>.</summary>
public sealed class SendGridOptions
{
    /// <summary>A SendGrid API key with the Mail Send permission. Keep it in a secret store.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The default sender (a verified sender or domain). A message can override it with <see cref="EmailMessage.From"/>.</summary>
    public EmailAddress From { get; set; }

    /// <summary>The account region.</summary>
    public SendGridRegion Region { get; set; } = SendGridRegion.Us;

    /// <summary>
    /// Turns on SendGrid's sandbox mode: the request is fully validated but the message is never delivered. Safe for checking a
    /// live API key and sender.
    /// </summary>
    public bool SandboxMode { get; set; }

    /// <summary>How long a single API call may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Never prints the API key.</summary>
    public override string ToString() => $"SendGridOptions {{ Region = {Region} }}";

    internal static string? Validate(SendGridOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return "SendGrid ApiKey is required.";
        }

        if (!options.From.IsValid)
        {
            return "SendGrid From must be a valid email address.";
        }

        return options.Timeout > TimeSpan.Zero ? null : "SendGrid Timeout must be positive.";
    }
}

internal sealed class SendGridOptionsValidator : IValidateOptions<SendGridOptions>
{
    public ValidateOptionsResult Validate(string? name, SendGridOptions options) =>
        SendGridOptions.Validate(options) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
