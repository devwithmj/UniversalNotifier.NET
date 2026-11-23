namespace UniversalNotifier.NET;

/// <summary>
/// The text of a notification for one channel. Every part may contain <c>{{Token}}</c> placeholders. Each channel
/// uses the parts that make sense for it: email uses Subject, Text and Html; push uses Title and Text; SMS uses Text.
/// </summary>
public sealed record ChannelContent
{
    /// <summary>The email subject.</summary>
    public string? Subject { get; init; }

    /// <summary>The title of a push notification.</summary>
    public string? Title { get; init; }

    /// <summary>The plain text (email text body, SMS text, push body, Telegram text).</summary>
    public string? Text { get; init; }

    /// <summary>The HTML (email HTML body, or Telegram HTML text). Token values are HTML-encoded.</summary>
    public string? Html { get; init; }

    /// <summary>Custom key/value data for push and web push. Values may contain tokens.</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }
}
