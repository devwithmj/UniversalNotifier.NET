using System.Text.Json.Serialization;

namespace UniversalNotifier.NET.Email.SendGrid;

internal sealed record SendGridAddress(string Email, string? Name);

internal sealed record SendGridPersonalization(SendGridAddress[] To, SendGridAddress[]? Cc, SendGridAddress[]? Bcc);

internal sealed record SendGridContent(string Type, string Value);

internal sealed record SendGridAttachment(string Content, string Filename, string Type, string Disposition, string? ContentId);

internal sealed record SendGridMail(
    SendGridPersonalization[] Personalizations,
    SendGridAddress From,
    SendGridAddress? ReplyTo,
    string Subject,
    SendGridContent[] Content,
    SendGridAttachment[]? Attachments,
    Dictionary<string, string>? Headers,
    string[]? Categories,
    Dictionary<string, string>? CustomArgs);

// Source-generated so serialization needs no reflection (trim and AOT friendly).
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SendGridMail))]
internal sealed partial class SendGridJsonContext : JsonSerializerContext;
