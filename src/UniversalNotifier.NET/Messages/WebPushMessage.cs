namespace UniversalNotifier.NET;

/// <summary>A browser push subscription, as produced by <c>PushSubscription.toJSON()</c> in the browser.</summary>
/// <param name="Endpoint">The push service URL (https).</param>
/// <param name="P256dh">The browser's public key (base64url).</param>
/// <param name="Auth">The browser's auth secret (base64url).</param>
public sealed record WebPushSubscription(string Endpoint, string P256dh, string Auth)
{
    /// <summary>Prints nothing: the endpoint and keys identify and encrypt for a user's browser.</summary>
    public override string ToString() => nameof(WebPushSubscription);
}

/// <summary>How quickly the push service should deliver (RFC 8030).</summary>
public enum WebPushUrgency
{
    /// <summary>Deliver when the device is on power and Wi-Fi.</summary>
    VeryLow,

    /// <summary>Deliver when the device is on power or Wi-Fi.</summary>
    Low,

    /// <summary>Deliver without special conditions. The default.</summary>
    Normal,

    /// <summary>Deliver immediately, for example an incoming call.</summary>
    High,
}

/// <summary>A browser (Web Push / VAPID) notification.</summary>
public sealed record WebPushMessage : NotifierMessage
{
    /// <summary>The browser subscriptions to notify. Each one is reported separately in the response.</summary>
    public IReadOnlyList<WebPushSubscription> Subscriptions { get; init; } = [];

    /// <summary>The notification title, sent in a JSON payload for your service worker.</summary>
    public string? Title { get; init; }

    /// <summary>The notification text.</summary>
    public string? Body { get; init; }

    /// <summary>Custom key/value data added to the JSON payload.</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    /// <summary>A raw payload that replaces the JSON built from <see cref="Title"/>, <see cref="Body"/> and <see cref="Data"/>.</summary>
    public string? Payload { get; init; }

    /// <summary>How long the push service keeps the message if the browser is offline.</summary>
    public TimeSpan? TimeToLive { get; init; }

    /// <summary>The delivery urgency.</summary>
    public WebPushUrgency Urgency { get; init; } = WebPushUrgency.Normal;

    /// <summary>A topic that replaces an earlier undelivered message with the same topic.</summary>
    public string? Topic { get; init; }

    /// <inheritdoc />
    public override IReadOnlyList<NotifierError> Validate()
    {
        var errors = new ValidationErrors();

        if (Subscriptions.Count == 0)
        {
            errors.Add("webpush.subscriptions.required", "At least one subscription is required.");
        }

        for (var i = 0; i < Subscriptions.Count; i++)
        {
            var s = Subscriptions[i];
            if (!Uri.TryCreate(s.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
                || string.IsNullOrWhiteSpace(s.P256dh) || string.IsNullOrWhiteSpace(s.Auth))
            {
                errors.Add("webpush.subscription.invalid", $"Subscriptions[{i}] needs an https endpoint, P256dh and Auth.");
            }
        }

        if (string.IsNullOrWhiteSpace(Payload) && string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Body))
        {
            errors.Add("webpush.content.required", "Payload, Title or Body is required.");
        }

        if (TimeToLive is { } ttl && ttl < TimeSpan.Zero)
        {
            errors.Add("webpush.ttl.invalid", "TimeToLive must not be negative.");
        }

        return errors.ToList();
    }
}
