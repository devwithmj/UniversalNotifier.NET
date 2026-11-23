namespace UniversalNotifier.NET;

/// <summary>A text message.</summary>
public sealed record SmsMessage : NotifierMessage
{
    /// <summary>The recipients in E.164 format. Each one gets its own message.</summary>
    public IReadOnlyList<PhoneNumber> To { get; init; } = [];

    /// <summary>The message text.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Overrides the sender number, alphanumeric sender id or messaging service configured on the provider.</summary>
    public string? From { get; init; }

    /// <summary>Creates a message for one recipient.</summary>
    public static SmsMessage Create(PhoneNumber to, string body) => new() { To = [to], Body = body };

    /// <inheritdoc />
    public override IReadOnlyList<NotifierError> Validate()
    {
        var errors = new ValidationErrors();

        if (To.Count == 0)
        {
            errors.Add("sms.recipients.required", "At least one recipient is required.");
        }

        for (var i = 0; i < To.Count; i++)
        {
            if (!To[i].IsValid)
            {
                errors.Add("sms.number.invalid", $"To[{i}] is not a valid E.164 phone number.");
            }
        }

        if (string.IsNullOrWhiteSpace(Body))
        {
            errors.Add("sms.body.required", "Body is required.");
        }

        if (ValidationErrors.HasControlCharacter(From))
        {
            errors.Add("sms.from.invalid", "From must not contain control characters.");
        }

        return errors.ToList();
    }
}
