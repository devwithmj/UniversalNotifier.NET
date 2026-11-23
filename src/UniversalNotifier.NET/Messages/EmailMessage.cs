namespace UniversalNotifier.NET;

/// <summary>A file attached to an email.</summary>
/// <param name="FileName">The file name shown to the recipient. No path separators.</param>
/// <param name="Content">The file bytes.</param>
/// <param name="ContentType">The MIME type, for example <c>application/pdf</c>.</param>
/// <param name="ContentId">Set to embed the file inline and reference it from HTML as <c>cid:id</c>.</param>
public sealed record EmailAttachment(
    string FileName,
    ReadOnlyMemory<byte> Content,
    string ContentType = "application/octet-stream",
    string? ContentId = null);

/// <summary>An email.</summary>
public sealed record EmailMessage : NotifierMessage
{
    /// <summary>Overrides the sender configured on the provider.</summary>
    public EmailAddress? From { get; init; }

    /// <summary>
    /// The primary recipients. With more than one, each recipient gets an individual message and never sees the others
    /// (bulk send). Cc and Bcc are only allowed with a single recipient.
    /// </summary>
    public IReadOnlyList<EmailAddress> To { get; init; } = [];

    /// <summary>Visible copy recipients.</summary>
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];

    /// <summary>Hidden copy recipients.</summary>
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];

    /// <summary>Where replies should go.</summary>
    public EmailAddress? ReplyTo { get; init; }

    /// <summary>The subject line. Must not contain line breaks.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>The plain-text body.</summary>
    public string? TextBody { get; init; }

    /// <summary>The HTML body.</summary>
    public string? HtmlBody { get; init; }

    /// <summary>Files to attach.</summary>
    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];

    /// <summary>Extra headers. Names are letters, digits and hyphens; values contain no line breaks.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Provider tags used for grouping and analytics where supported.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Creates a message for one recipient.</summary>
    public static EmailMessage Create(EmailAddress to, string subject, string? textBody = null, string? htmlBody = null) =>
        new() { To = [to], Subject = subject, TextBody = textBody, HtmlBody = htmlBody };

    /// <inheritdoc />
    public override IReadOnlyList<NotifierError> Validate()
    {
        var errors = new ValidationErrors();

        if (To.Count + Cc.Count + Bcc.Count == 0)
        {
            errors.Add("email.recipients.required", "At least one recipient in To, Cc or Bcc is required.");
        }

        if (To.Count > 1 && (Cc.Count > 0 || Bcc.Count > 0))
        {
            errors.Add("email.copies.bulk", "Cc and Bcc can only be used with a single To recipient. Send one message per recipient instead.");
        }

        CheckAddresses(ref errors, To, nameof(To));
        CheckAddresses(ref errors, Cc, nameof(Cc));
        CheckAddresses(ref errors, Bcc, nameof(Bcc));

        if (From is { IsValid: false })
        {
            errors.Add("email.from.invalid", "From is not a valid email address.");
        }

        if (ReplyTo is { IsValid: false })
        {
            errors.Add("email.replyto.invalid", "ReplyTo is not a valid email address.");
        }

        if (string.IsNullOrWhiteSpace(Subject))
        {
            errors.Add("email.subject.required", "Subject is required.");
        }
        else if (ValidationErrors.HasLineBreak(Subject))
        {
            errors.Add("email.subject.invalid", "Subject must not contain line breaks.");
        }

        if (string.IsNullOrWhiteSpace(TextBody) && string.IsNullOrWhiteSpace(HtmlBody))
        {
            errors.Add("email.body.required", "TextBody or HtmlBody is required.");
        }

        if (Headers is not null)
        {
            foreach (var (name, value) in Headers)
            {
                if (!IsHeaderName(name) || ValidationErrors.HasLineBreak(value))
                {
                    errors.Add("email.header.invalid", "A header has an invalid name or contains a line break.");
                    break;
                }
            }
        }

        foreach (var tag in Tags)
        {
            if (string.IsNullOrWhiteSpace(tag) || ValidationErrors.HasControlCharacter(tag))
            {
                errors.Add("email.tag.invalid", "Tags must be non-empty text without control characters.");
                break;
            }
        }

        foreach (var attachment in Attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.FileName)
                || attachment.FileName.AsSpan().IndexOfAny('/', '\\') >= 0
                || ValidationErrors.HasControlCharacter(attachment.FileName)
                || !IsMediaType(attachment.ContentType))
            {
                errors.Add("email.attachment.invalid", "An attachment needs a file name without path separators and a type/subtype content type.");
                break;
            }
        }

        return errors.ToList();
    }

    private static void CheckAddresses(ref ValidationErrors errors, IReadOnlyList<EmailAddress> addresses, string field)
    {
        for (var i = 0; i < addresses.Count; i++)
        {
            if (!addresses[i].IsValid)
            {
                errors.Add("email.address.invalid", $"{field}[{i}] is not a valid email address.");
            }
        }
    }

    private static bool IsHeaderName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMediaType(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < value.Length - 1 && !ValidationErrors.HasControlCharacter(value) && !value.Contains(' ', StringComparison.Ordinal);
    }
}
