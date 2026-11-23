using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET.Email.Ses;

/// <summary>Settings for <see cref="SesNotifier"/>.</summary>
public sealed class SesOptions
{
    /// <summary>The AWS Region the SES identity lives in, for example <c>eu-west-1</c>.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>The access key id of an IAM user or role allowed to call <c>ses:SendEmail</c>.</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>The secret access key. Keep it in a secret store.</summary>
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>The session token, when the credentials are temporary (STS, SSO, an assumed role).</summary>
    public string? SessionToken { get; set; }

    /// <summary>The default sender (a verified identity). A message can override it with <see cref="EmailMessage.From"/>.</summary>
    public EmailAddress From { get; set; }

    /// <summary>An SES configuration set to apply (event publishing, tracking). Optional.</summary>
    public string? ConfigurationSetName { get; set; }

    /// <summary>Overrides the endpoint (<c>https://email.{Region}.amazonaws.com</c>), for a VPC endpoint or a local emulator.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>How long a single API call may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many recipients of one message are sent in parallel. Each recipient gets its own request.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Never prints credentials.</summary>
    public override string ToString() => $"SesOptions {{ Region = {Region} }}";

    internal static string? Validate(SesOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Region) || options.Region.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            return "SES Region is required, for example 'eu-west-1'.";
        }

        if (string.IsNullOrWhiteSpace(options.AccessKeyId) || string.IsNullOrWhiteSpace(options.SecretAccessKey))
        {
            return "SES needs an AccessKeyId and a SecretAccessKey.";
        }

        if (!options.From.IsValid)
        {
            return "SES From must be a valid email address.";
        }

        if (options.BaseAddress is { } address && address.Scheme != Uri.UriSchemeHttps)
        {
            return "SES BaseAddress must be an https address.";
        }

        if (options.MaxConcurrency < 1)
        {
            return "SES MaxConcurrency must be at least 1.";
        }

        return options.Timeout > TimeSpan.Zero ? null : "SES Timeout must be positive.";
    }
}

internal sealed class SesOptionsValidator : IValidateOptions<SesOptions>
{
    public ValidateOptionsResult Validate(string? name, SesOptions options) =>
        SesOptions.Validate(options) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
