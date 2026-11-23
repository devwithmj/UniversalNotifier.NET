using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET.Webhook;

/// <summary>The payload shape an endpoint expects.</summary>
public enum WebhookFormat
{
    /// <summary>A generic JSON body: <c>{"title", "text", "data", "id"}</c>.</summary>
    Json,

    /// <summary>A Slack incoming webhook (<c>{"text": ...}</c> with mrkdwn).</summary>
    Slack,

    /// <summary>A Microsoft Teams workflow webhook (an Adaptive Card message).</summary>
    Teams,
}

/// <summary>One named webhook destination.</summary>
public sealed class WebhookEndpoint
{
    /// <summary>The webhook URL (https). It is a secret: anyone holding it can post to the channel.</summary>
    public Uri? Url { get; set; }

    /// <summary>The payload shape.</summary>
    public WebhookFormat Format { get; set; } = WebhookFormat.Json;

    /// <summary>When set, each request carries an <c>X-Signature-256: sha256=...</c> HMAC of the body so the receiver can verify it.</summary>
    public string? SigningSecret { get; set; }

    /// <summary>Never prints the URL or secret.</summary>
    public override string ToString() => $"WebhookEndpoint {{ Format = {Format} }}";
}

/// <summary>Settings for <see cref="WebhookNotifier"/>.</summary>
public sealed class WebhookOptions
{
    /// <summary>The destinations a message can name, keyed by a name of your choice (case-insensitive).</summary>
    public IDictionary<string, WebhookEndpoint> Endpoints { get; } = new Dictionary<string, WebhookEndpoint>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a single call may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Adds a destination.</summary>
    public WebhookOptions Add(string name, string url, WebhookFormat format = WebhookFormat.Json, string? signingSecret = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Endpoints[name] = new WebhookEndpoint { Url = new Uri(url), Format = format, SigningSecret = signingSecret };
        return this;
    }

    /// <summary>Never prints URLs or secrets.</summary>
    public override string ToString() => $"WebhookOptions {{ Endpoints = {Endpoints.Count} }}";

    internal static string? Validate(WebhookOptions options)
    {
        if (options.Endpoints.Count == 0)
        {
            return "Webhook needs at least one endpoint. Add one with options.Add(\"name\", url).";
        }

        foreach (var (name, endpoint) in options.Endpoints)
        {
            if (string.IsNullOrWhiteSpace(name) || endpoint?.Url is null || !endpoint.Url.IsAbsoluteUri || endpoint.Url.Scheme != Uri.UriSchemeHttps)
            {
                return $"Webhook endpoint '{name}' needs an absolute https URL.";
            }
        }

        return options.Timeout > TimeSpan.Zero ? null : "Webhook Timeout must be positive.";
    }
}

internal sealed class WebhookOptionsValidator : IValidateOptions<WebhookOptions>
{
    public ValidateOptionsResult Validate(string? name, WebhookOptions options) =>
        WebhookOptions.Validate(options) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
