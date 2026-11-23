namespace UniversalNotifier.NET;

/// <summary>
/// A WhatsApp message. WhatsApp only lets a business send free-form text inside a 24-hour window after the user last wrote to it;
/// anything else must be an approved template (<see cref="TemplateId"/>). Providers such as Twilio expose both.
/// </summary>
public sealed record WhatsAppMessage : NotifierMessage
{
    /// <summary>The recipients' phone numbers in E.164 format. Each one gets its own message.</summary>
    public IReadOnlyList<PhoneNumber> To { get; init; } = [];

    /// <summary>Free-form text. Only deliverable inside the 24-hour customer service window (and always in a provider's sandbox).</summary>
    public string? Body { get; init; }

    /// <summary>The provider's id of an approved message template (for Twilio, a Content SID starting with <c>HX</c>). Use it to message users outside the window.</summary>
    public string? TemplateId { get; init; }

    /// <summary>The values for the template's variables, keyed by variable name or number (for example <c>"1"</c>).</summary>
    public IReadOnlyDictionary<string, string>? TemplateVariables { get; init; }

    /// <summary>Overrides the sender configured on the provider (a WhatsApp number, or a Messaging Service id).</summary>
    public string? From { get; init; }

    /// <summary>Creates a free-form text message for one recipient.</summary>
    public static WhatsAppMessage Create(PhoneNumber to, string body) => new() { To = [to], Body = body };

    /// <summary>Creates an approved-template message for one recipient.</summary>
    public static WhatsAppMessage FromTemplate(PhoneNumber to, string templateId, IReadOnlyDictionary<string, string>? variables = null) =>
        new() { To = [to], TemplateId = templateId, TemplateVariables = variables };

    /// <inheritdoc />
    public override IReadOnlyList<NotifierError> Validate()
    {
        var errors = new ValidationErrors();

        if (To.Count == 0)
        {
            errors.Add("whatsapp.recipients.required", "At least one recipient is required.");
        }

        for (var i = 0; i < To.Count; i++)
        {
            if (!To[i].IsValid)
            {
                errors.Add("whatsapp.number.invalid", $"To[{i}] is not a valid E.164 phone number.");
            }
        }

        var hasBody = !string.IsNullOrWhiteSpace(Body);
        var hasTemplate = !string.IsNullOrWhiteSpace(TemplateId);
        if (!hasBody && !hasTemplate)
        {
            errors.Add("whatsapp.content.required", "Body or TemplateId is required.");
        }
        else if (hasBody && hasTemplate)
        {
            errors.Add("whatsapp.content.ambiguous", "Set either Body or TemplateId, not both.");
        }

        if (TemplateVariables is { Count: > 0 } && !hasTemplate)
        {
            errors.Add("whatsapp.variables.invalid", "TemplateVariables only apply to a TemplateId.");
        }

        if (ValidationErrors.HasControlCharacter(From) || ValidationErrors.HasControlCharacter(TemplateId))
        {
            errors.Add("whatsapp.id.invalid", "From and TemplateId must not contain control characters.");
        }

        return errors.ToList();
    }
}

/// <summary>Sends WhatsApp messages. Implemented by Twilio.</summary>
public interface IWhatsAppNotifier : INotifier<WhatsAppMessage>;
