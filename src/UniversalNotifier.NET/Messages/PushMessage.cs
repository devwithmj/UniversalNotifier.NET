namespace UniversalNotifier.NET;

/// <summary>A mobile push notification.</summary>
public sealed record PushMessage : NotifierMessage
{
    /// <summary>Device registration tokens. Each token is reported separately in the response.</summary>
    public IReadOnlyList<string> Tokens { get; init; } = [];

    /// <summary>A topic to publish to instead of (or as well as) individual tokens.</summary>
    public string? Topic { get; init; }

    /// <summary>The notification title.</summary>
    public string? Title { get; init; }

    /// <summary>The notification text.</summary>
    public string? Body { get; init; }

    /// <summary>Custom key/value data delivered to the app.</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    /// <summary>Creates a message for one device.</summary>
    public static PushMessage Create(string token, string title, string body) =>
        new() { Tokens = [token], Title = title, Body = body };

    /// <inheritdoc />
    public override IReadOnlyList<NotifierError> Validate()
    {
        var errors = new ValidationErrors();

        if (Tokens.Count == 0 && string.IsNullOrWhiteSpace(Topic))
        {
            errors.Add("push.target.required", "At least one token or a topic is required.");
        }

        foreach (var token in Tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                errors.Add("push.token.invalid", "Tokens must not be empty.");
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Body) && (Data is null || Data.Count == 0))
        {
            errors.Add("push.content.required", "Title, Body or Data is required.");
        }

        return errors.ToList();
    }
}
