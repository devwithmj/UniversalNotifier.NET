namespace UniversalNotifier.NET;

/// <summary>
/// A message to a chat or automation webhook (Slack, Microsoft Teams, or any HTTP endpoint). The endpoint is named, not
/// addressed: the URL (a secret) lives in the provider's configuration and the message only names it.
/// </summary>
public sealed record WebhookMessage : NotifierMessage
{
    /// <summary>The name of a configured endpoint, for example <c>ops-alerts</c>.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>An optional heading.</summary>
    public string? Title { get; init; }

    /// <summary>The message text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Extra key/value data. Generic JSON endpoints receive it as-is; chat formats show it as fields.</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    /// <summary>Creates a message for a named endpoint.</summary>
    public static WebhookMessage Create(string endpoint, string text, string? title = null) =>
        new() { Endpoint = endpoint, Text = text, Title = title };

    /// <inheritdoc />
    public override IReadOnlyList<NotifierError> Validate()
    {
        var errors = new ValidationErrors();

        if (string.IsNullOrWhiteSpace(Endpoint) || ValidationErrors.HasControlCharacter(Endpoint))
        {
            errors.Add("webhook.endpoint.required", "Endpoint must name a configured webhook endpoint.");
        }

        if (string.IsNullOrWhiteSpace(Text))
        {
            errors.Add("webhook.text.required", "Text is required.");
        }

        return errors.ToList();
    }
}

/// <summary>Sends webhook messages (Slack, Teams, generic JSON).</summary>
public interface IWebhookNotifier : INotifier<WebhookMessage>;
