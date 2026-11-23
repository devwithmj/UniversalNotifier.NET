using System.Text.Json.Serialization;

namespace UniversalNotifier.NET.Telegram;

internal sealed record SendMessageRequest(string ChatId, string Text, string? ParseMode, bool? DisableNotification);

// Source-generated so serialization needs no reflection (trim and AOT friendly).
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SendMessageRequest))]
internal sealed partial class TelegramJsonContext : JsonSerializerContext;
