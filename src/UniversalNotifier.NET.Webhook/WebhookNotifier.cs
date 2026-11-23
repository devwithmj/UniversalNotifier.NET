using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalNotifier.NET.Internal;

namespace UniversalNotifier.NET.Webhook;

/// <summary>
/// Posts messages to named webhook endpoints: Slack, Microsoft Teams (workflow webhooks) or any HTTP endpoint that
/// accepts JSON. A webhook URL is a secret, so this notifier uses its own <see cref="HttpClient"/> (the factory's
/// default logging would write the URL to your logs) and never includes it in errors.
/// </summary>
public sealed class WebhookNotifier : NotifierBase<WebhookMessage>, IWebhookNotifier, IDisposable
{
    /// <summary>The provider name used for explicit selection.</summary>
    public const string Name = "webhook";

    private readonly HttpClient _http;
    private readonly Dictionary<string, WebhookEndpoint> _endpoints;

    /// <summary>Creates the notifier. With dependency injection use <c>AddWebhook</c> instead.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public WebhookNotifier(WebhookOptions options, NotifierContext context)
        : this(options, context, new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    internal WebhookNotifier(WebhookOptions options, NotifierContext context, HttpMessageHandler handler)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (WebhookOptions.Validate(options) is { } error)
        {
            handler.Dispose();
            throw new ArgumentException(error, nameof(options));
        }

        _endpoints = new Dictionary<string, WebhookEndpoint>(options.Endpoints, StringComparer.OrdinalIgnoreCase);
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = options.Timeout };
    }

    /// <inheritdoc />
    protected override string ProviderName => Name;

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <inheritdoc />
    protected override async Task<NotifierResponse> SendCoreAsync(WebhookMessage message, CancellationToken cancellationToken)
    {
        if (!_endpoints.TryGetValue(message.Endpoint, out var endpoint))
        {
            return NotifierResponse.Failed(new NotifierError(
                NotifierErrorKind.Configuration, "webhook.endpoint.unknown", $"No webhook endpoint named '{message.Endpoint}' is configured."));
        }

        var body = Encoding.UTF8.GetBytes(BuildBody(message, endpoint.Format));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        if (!string.IsNullOrEmpty(endpoint.SigningSecret))
        {
            var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(endpoint.SigningSecret), body);
            request.Headers.TryAddWithoutValidation("X-Signature-256", "sha256=" + Convert.ToHexStringLower(signature));
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        // The response body is ignored on purpose: chat services answer with plain text such as "ok" or an error sentence.
        return response.IsSuccessStatusCode
            ? NotifierResponse.Accepted(httpStatusCode: status)
            : NotifierResponse.Failed(ProviderHttp.ClassifyStatus(response.StatusCode, "Webhook", null), status, ProviderHttp.RetryAfter(response));
    }

    internal static string BuildBody(WebhookMessage message, WebhookFormat format) => format switch
    {
        WebhookFormat.Slack => JsonSerializer.Serialize(new SlackPayload(SlackText(message)), WebhookJsonContext.Default.SlackPayload),
        WebhookFormat.Teams => JsonSerializer.Serialize(BuildTeamsPayload(message), WebhookJsonContext.Default.TeamsPayload),
        _ => JsonSerializer.Serialize(
            new GenericPayload(message.Id, message.Title, message.Text, message.Data is { Count: > 0 } d ? new Dictionary<string, string>(d) : null),
            WebhookJsonContext.Default.GenericPayload),
    };

    private static string SlackText(WebhookMessage message)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(message.Title))
        {
            text.Append('*').Append(SlackEscape(message.Title)).Append("*\n");
        }

        text.Append(SlackEscape(message.Text));
        if (message.Data is { Count: > 0 })
        {
            foreach (var (key, value) in message.Data)
            {
                text.Append("\n• ").Append(SlackEscape(key)).Append(": ").Append(SlackEscape(value));
            }
        }

        return text.ToString();
    }

    // Slack treats &, < and > as control characters in message text.
    private static string SlackEscape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static TeamsPayload BuildTeamsPayload(WebhookMessage message)
    {
        var body = new List<TeamsElement>();
        if (!string.IsNullOrWhiteSpace(message.Title))
        {
            body.Add(new TeamsElement("TextBlock", message.Title, "Bolder", true, null));
        }

        body.Add(new TeamsElement("TextBlock", message.Text, null, true, null));
        if (message.Data is { Count: > 0 })
        {
            body.Add(new TeamsElement("FactSet", null, null, null, [.. message.Data.Select(p => new TeamsFact(p.Key, p.Value))]));
        }

        return new TeamsPayload(
            "message",
            [new TeamsAttachment("application/vnd.microsoft.card.adaptive", new TeamsCard("http://adaptivecards.io/schemas/adaptive-card.json", "AdaptiveCard", "1.4", [.. body]))]);
    }
}
