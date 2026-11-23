using System.Text.Json.Serialization;

namespace UniversalNotifier.NET.Email.Ses;

internal sealed record SesDestination(string[] ToAddresses, string[]? CcAddresses, string[]? BccAddresses);

internal sealed record SesData(string Data, string Charset = "UTF-8");

internal sealed record SesBody(SesData? Text, SesData? Html);

internal sealed record SesHeader(string Name, string Value);

internal sealed record SesAttachment(byte[] RawContent, string FileName, string ContentType, string ContentDisposition, string? ContentId);

internal sealed record SesSimple(SesData Subject, SesBody Body, SesHeader[]? Headers, SesAttachment[]? Attachments);

internal sealed record SesContent(SesSimple Simple);

internal sealed record SesTag(string Name, string Value);

internal sealed record SesSendEmail(
    string FromEmailAddress,
    SesDestination Destination,
    string[]? ReplyToAddresses,
    SesContent Content,
    SesTag[]? EmailTags,
    string? ConfigurationSetName);

// Source-generated so serialization needs no reflection (trim and AOT friendly). Property names are the PascalCase ones SES expects; byte[] is base64.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SesSendEmail))]
internal sealed partial class SesJsonContext : JsonSerializerContext;
