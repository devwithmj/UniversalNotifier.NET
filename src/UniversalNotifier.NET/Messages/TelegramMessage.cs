namespace UniversalNotifier.NET;

/// <summary>How Telegram interprets the message text.</summary>
public enum TelegramParseMode
{
    /// <summary>Plain text.</summary>
    None,

    /// <summary>Telegram's HTML subset.</summary>
    Html,

    /// <summary>Telegram's MarkdownV2 syntax.</summary>
    MarkdownV2,
}

/// <summary>A Telegram bot message.</summary>
public sealed record TelegramMessage : NotifierMessage
{
    /// <summary>Chat ids or <c>@channelusername</c> values. Each one gets its own message.</summary>
    public IReadOnlyList<string> ChatIds { get; init; } = [];

    /// <summary>The message text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>How the text is interpreted.</summary>
    public TelegramParseMode ParseMode { get; init; }

    /// <summary>Delivers without a notification sound.</summary>
    public bool Silent { get; init; }

    /// <summary>Creates a message for one chat.</summary>
    public static TelegramMessage Create(string chatId, string text) => new() { ChatIds = [chatId], Text = text };

    /// <inheritdoc />
    public override IReadOnlyList<NotifierError> Validate()
    {
        var errors = new ValidationErrors();

        if (ChatIds.Count == 0)
        {
            errors.Add("telegram.chat.required", "At least one chat id is required.");
        }

        foreach (var chatId in ChatIds)
        {
            if (string.IsNullOrWhiteSpace(chatId))
            {
                errors.Add("telegram.chat.invalid", "Chat ids must not be empty.");
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(Text))
        {
            errors.Add("telegram.text.required", "Text is required.");
        }

        return errors.ToList();
    }
}
