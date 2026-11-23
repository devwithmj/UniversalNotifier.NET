namespace UniversalNotifier.NET;

/// <summary>One channel a notification should go out on, with optional inline content and an optional provider choice.</summary>
/// <param name="Channel">The channel.</param>
/// <param name="Content">Inline content. When <see langword="null"/>, the notification's template supplies it.</param>
/// <param name="Provider">The provider name to use. When <see langword="null"/>, the channel's default provider is used.</param>
public sealed record ChannelRequest(NotificationChannel Channel, ChannelContent? Content = null, string? Provider = null);

/// <summary>
/// What to tell whom, on which channels. A notification is plain immutable data, so it can be queued, stored or
/// serialized. Build one fluently and hand it to <see cref="INotificationService"/>:
/// <code>Notification.To("user@example.com").Email("Welcome", html: "&lt;b&gt;Hi {{Name}}&lt;/b&gt;").WithData(new { Name = "John" })</code>
/// </summary>
public sealed record Notification
{
    /// <summary>An optional correlation id, copied into logs and results.</summary>
    public string? Id { get; init; }

    /// <summary>Who the notification is for.</summary>
    public required Recipient Recipient { get; init; }

    /// <summary>The channels to send on.</summary>
    public IReadOnlyList<ChannelRequest> Channels { get; init; } = [];

    /// <summary>The name of a registered template that supplies content for channels without inline content.</summary>
    public string? TemplateName { get; init; }

    /// <summary>The values for <c>{{Token}}</c> placeholders.</summary>
    public IReadOnlyDictionary<string, object?>? Data { get; init; }

    /// <summary>
    /// Makes a repeated send of the same logical notification harmless: the background queue accepts a notification with a
    /// key it has already seen (within its deduplication window) without delivering it again.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Free-form key/value data copied onto each message.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Starts a notification for a recipient.</summary>
    public static Notification To(Recipient recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        return new Notification { Recipient = recipient };
    }

    /// <summary>Starts a notification for an email address (contains <c>@</c>) or an E.164 phone number (starts with <c>+</c>).</summary>
    public static Notification To(string emailOrPhone) => To(Recipient.Parse(emailOrPhone));

    /// <summary>Adds the email channel. Parts left out come from the template.</summary>
    public Notification Email(string? subject = null, string? html = null, string? text = null, string? provider = null) =>
        Via(NotificationChannel.Email, Inline(new ChannelContent { Subject = subject, Html = html, Text = text }), provider);

    /// <summary>Adds the SMS channel. Leave <paramref name="text"/> out to use the template.</summary>
    public Notification Sms(string? text = null, string? provider = null) =>
        Via(NotificationChannel.Sms, Inline(new ChannelContent { Text = text }), provider);

    /// <summary>Adds the mobile push channel. Parts left out come from the template.</summary>
    public Notification Push(string? title = null, string? body = null, IReadOnlyDictionary<string, string>? data = null, string? provider = null) =>
        Via(NotificationChannel.Push, Inline(new ChannelContent { Title = title, Text = body, Data = data }), provider);

    /// <summary>Adds the browser push channel. Parts left out come from the template.</summary>
    public Notification WebPush(string? title = null, string? body = null, IReadOnlyDictionary<string, string>? data = null, string? provider = null) =>
        Via(NotificationChannel.WebPush, Inline(new ChannelContent { Title = title, Text = body, Data = data }), provider);

    /// <summary>Adds the Telegram channel. Pass <paramref name="html"/> to send Telegram HTML instead of plain text.</summary>
    public Notification Telegram(string? text = null, string? html = null, string? provider = null) =>
        Via(NotificationChannel.Telegram, Inline(new ChannelContent { Text = text, Html = html }), provider);

    /// <summary>Adds the webhook channel (Slack, Teams, generic). The title and text come from the template when left out.</summary>
    public Notification Webhook(string? title = null, string? text = null, string? provider = null) =>
        Via(NotificationChannel.Webhook, Inline(new ChannelContent { Title = title, Text = text }), provider);

    /// <summary>Adds any channel, including custom ones.</summary>
    public Notification Via(NotificationChannel channel, ChannelContent? content = null, string? provider = null) =>
        this with { Channels = [.. Channels, new ChannelRequest(channel, content, provider)] };

    /// <summary>Uses a registered template for every channel that has no inline content.</summary>
    public Notification UseTemplate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return this with { TemplateName = name };
    }

    /// <summary>Sets the token values from an object's public properties. The values are copied now.</summary>
    public Notification WithData(object data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return this with { Data = TemplateData.Snapshot(data) };
    }

    /// <summary>Sets the token values from a dictionary. The values are copied now.</summary>
    public Notification WithData(IReadOnlyDictionary<string, object?> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return this with { Data = TemplateData.Snapshot(data) };
    }

    /// <summary>Sets the idempotency key used to drop duplicate submissions. See <see cref="IdempotencyKey"/>.</summary>
    public Notification WithIdempotencyKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return this with { IdempotencyKey = key };
    }

    /// <summary>Sets a correlation id.</summary>
    public Notification WithId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return this with { Id = id };
    }

    /// <summary>Prints only the id so recipients and content never leak into logs.</summary>
    public sealed override string ToString() => $"Notification {{ Id = {Id ?? "-"} }}";

    // A channel call with every part left out means "use the template", not "send empty content".
    private static ChannelContent? Inline(ChannelContent content) =>
        content.Subject is null && content.Title is null && content.Text is null && content.Html is null && content.Data is null ? null : content;
}
