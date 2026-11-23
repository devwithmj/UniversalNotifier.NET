using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET.WebPush;

/// <summary>Settings for <see cref="WebPushNotifier"/>.</summary>
public sealed class WebPushOptions
{
    /// <summary>The VAPID public key (base64url, 65 bytes). Create a pair with <see cref="VapidKeys.Generate"/>.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>The VAPID private key (base64url, 32 bytes). Keep it in a secret store.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>Who push services should contact about your pushes: <c>mailto:you@example.com</c> or an <c>https://</c> address.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>How long a single push service call may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many subscriptions of one message are pushed in parallel.</summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>Never prints the private key.</summary>
    public override string ToString() => $"WebPushOptions {{ Subject = {Subject} }}";

    internal static string? Validate(WebPushOptions options)
    {
        if (!Base64Url.TryDecode(options.PublicKey, out var publicKey) || publicKey.Length != 65 || publicKey[0] != 0x04)
        {
            return "WebPush PublicKey must be a base64url P-256 public key (65 bytes). Create one with VapidKeys.Generate().";
        }

        if (!Base64Url.TryDecode(options.PrivateKey, out var privateKey) || privateKey.Length != 32)
        {
            return "WebPush PrivateKey must be a base64url P-256 private key (32 bytes). Create one with VapidKeys.Generate().";
        }

        if (!options.Subject.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            && !options.Subject.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "WebPush Subject must start with 'mailto:' or 'https://'.";
        }

        if (options.MaxConcurrency < 1)
        {
            return "WebPush MaxConcurrency must be at least 1.";
        }

        return options.Timeout > TimeSpan.Zero ? null : "WebPush Timeout must be positive.";
    }
}

internal sealed class WebPushOptionsValidator : IValidateOptions<WebPushOptions>
{
    public ValidateOptionsResult Validate(string? name, WebPushOptions options) =>
        WebPushOptions.Validate(options) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
