using System.Text.Json.Serialization;

namespace UniversalNotifier.NET.Webhook;

internal sealed record GenericPayload(string? Id, string? Title, string Text, Dictionary<string, string>? Data);

internal sealed record SlackPayload(string Text);

internal sealed record TeamsPayload(string Type, TeamsAttachment[] Attachments);

internal sealed record TeamsAttachment(string ContentType, TeamsCard Content);

internal sealed record TeamsCard(
    [property: JsonPropertyName("$schema")] string Schema,
    string Type,
    string Version,
    TeamsElement[] Body);

internal sealed record TeamsElement(string Type, string? Text, string? Weight, bool? Wrap, TeamsFact[]? Facts);

internal sealed record TeamsFact(string Title, string Value);

// Source-generated so serialization needs no reflection (trim and AOT friendly).
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GenericPayload))]
[JsonSerializable(typeof(SlackPayload))]
[JsonSerializable(typeof(TeamsPayload))]
internal sealed partial class WebhookJsonContext : JsonSerializerContext;
