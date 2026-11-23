using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET.Sms.Twilio;

/// <summary>Settings for <see cref="TwilioSmsNotifier"/>.</summary>
public sealed class TwilioSmsOptions
{
    /// <summary>The Twilio Account SID (starts with <c>AC</c>).</summary>
    public string AccountSid { get; set; } = string.Empty;

    /// <summary>The account Auth Token. Keep it in a secret store. Not needed when an API key is used.</summary>
    public string? AuthToken { get; set; }

    /// <summary>An API key SID (starts with <c>SK</c>). Recommended over the Auth Token because it can be revoked on its own.</summary>
    public string? ApiKeySid { get; set; }

    /// <summary>The API key secret. Set together with <see cref="ApiKeySid"/>.</summary>
    public string? ApiKeySecret { get; set; }

    /// <summary>
    /// The default sender: a Twilio phone number in E.164 format, an alphanumeric sender id, or a Messaging Service SID
    /// (starts with <c>MG</c>). A message can override it with <see cref="SmsMessage.From"/>.
    /// </summary>
    public string From { get; set; } = string.Empty;

    /// <summary>The API base address. Change it to use a Twilio regional endpoint such as <c>https://api.dublin.ie1.twilio.com</c>.</summary>
    public Uri BaseAddress { get; set; } = new("https://api.twilio.com");

    /// <summary>How long a single API call may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many recipients of one message are sent in parallel. Twilio sends one message per recipient.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Never prints secrets.</summary>
    public override string ToString() => $"TwilioSmsOptions {{ AccountSid = {AccountSid} }}";

    internal static string? Validate(TwilioSmsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AccountSid) || !options.AccountSid.StartsWith("AC", StringComparison.Ordinal))
        {
            return "Twilio AccountSid is required and starts with 'AC'.";
        }

        var hasKey = !string.IsNullOrWhiteSpace(options.ApiKeySid) && !string.IsNullOrWhiteSpace(options.ApiKeySecret);
        if (!hasKey && string.IsNullOrWhiteSpace(options.AuthToken))
        {
            return "Twilio needs an AuthToken, or an ApiKeySid together with an ApiKeySecret.";
        }

        if (string.IsNullOrWhiteSpace(options.From))
        {
            return "Twilio From is required (a phone number, sender id or Messaging Service SID).";
        }

        if (options.BaseAddress is null || options.BaseAddress.Scheme != Uri.UriSchemeHttps)
        {
            return "Twilio BaseAddress must be an https address.";
        }

        if (options.MaxConcurrency < 1)
        {
            return "Twilio MaxConcurrency must be at least 1.";
        }

        return options.Timeout > TimeSpan.Zero ? null : "Twilio Timeout must be positive.";
    }
}

internal sealed class TwilioSmsOptionsValidator : IValidateOptions<TwilioSmsOptions>
{
    public ValidateOptionsResult Validate(string? name, TwilioSmsOptions options) =>
        TwilioSmsOptions.Validate(options) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
