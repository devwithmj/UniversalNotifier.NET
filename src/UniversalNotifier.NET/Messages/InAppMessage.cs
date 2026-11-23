namespace UniversalNotifier.NET;

/// <summary>A notification shown inside your own app (a bell icon, an inbox), stored until the user reads it.</summary>
public sealed record InAppMessage : NotifierMessage
{
    /// <summary>The user the notification is for (your own user id).</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>An optional heading.</summary>
    public string? Title { get; init; }

    /// <summary>The message text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Extra key/value data for your UI, for example a link or an entity id.</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    /// <summary>Creates a message for one user.</summary>
    public static InAppMessage Create(string userId, string text, string? title = null) =>
        new() { UserId = userId, Text = text, Title = title };

    /// <inheritdoc />
    public override IReadOnlyList<NotifierError> Validate()
    {
        var errors = new ValidationErrors();

        if (string.IsNullOrWhiteSpace(UserId) || ValidationErrors.HasControlCharacter(UserId))
        {
            errors.Add("inapp.user.required", "UserId is required.");
        }

        if (string.IsNullOrWhiteSpace(Text))
        {
            errors.Add("inapp.text.required", "Text is required.");
        }

        return errors.ToList();
    }
}

/// <summary>Stores in-app notifications for later display.</summary>
public interface IInAppNotifier : INotifier<InAppMessage>;
