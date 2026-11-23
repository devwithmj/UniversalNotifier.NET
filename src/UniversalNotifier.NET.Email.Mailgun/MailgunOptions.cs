using System.Buffers;
using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET.Email.Mailgun;

/// <summary>Which Mailgun region hosts the sending domain.</summary>
public enum MailgunRegion
{
    /// <summary><c>api.mailgun.net</c>.</summary>
    Us,

    /// <summary><c>api.eu.mailgun.net</c>. Domains created in the EU region only work here.</summary>
    Eu,
}

/// <summary>Settings for <see cref="MailgunNotifier"/>.</summary>
public sealed class MailgunOptions
{
    /// <summary>The Mailgun private API key. Keep it in a secret store, not in source control.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The sending domain, for example <c>mg.example.com</c>.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The region the domain was created in.</summary>
    public MailgunRegion Region { get; set; } = MailgunRegion.Us;

    /// <summary>The default sender. A message can override it with <see cref="EmailMessage.From"/>.</summary>
    public EmailAddress From { get; set; }

    /// <summary>
    /// Sends in Mailgun's test mode (<c>o:testmode=yes</c>): Mailgun accepts and processes the message, fires webhooks and
    /// counts it for validation, but does not deliver it. Safe for checking a live API key and domain.
    /// </summary>
    public bool TestMode { get; set; }

    /// <summary>How long a single API call may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Never prints the API key.</summary>
    public override string ToString() => $"MailgunOptions {{ Domain = {Domain}, Region = {Region} }}";

    private static readonly SearchValues<char> InvalidDomainCharacters = SearchValues.Create("/?# ");

    internal static string? Validate(MailgunOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return "Mailgun ApiKey is required.";
        }

        if (string.IsNullOrWhiteSpace(options.Domain) || options.Domain.AsSpan().ContainsAny(InvalidDomainCharacters))
        {
            return "Mailgun Domain is required and must be a bare domain name such as mg.example.com.";
        }

        if (!options.From.IsValid)
        {
            return "Mailgun From must be a valid email address.";
        }

        return options.Timeout > TimeSpan.Zero ? null : "Mailgun Timeout must be positive.";
    }
}

internal sealed class MailgunOptionsValidator : IValidateOptions<MailgunOptions>
{
    public ValidateOptionsResult Validate(string? name, MailgunOptions options) =>
        MailgunOptions.Validate(options) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
