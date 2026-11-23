using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace UniversalNotifier.NET;

/// <summary>
/// Converts a <see cref="Notification"/> to and from JSON so it can be stored or put on a durable queue (database,
/// RabbitMQ, Azure Service Bus, ...). The JSON contains the recipient's addresses and the message text, so protect it
/// like any other personal data. Token values come back as JSON elements, which templates read just like the originals.
/// </summary>
/// <remarks>
/// Serialization is source generated, so it works under trimming and Native AOT as long as the token values are strings,
/// numbers, booleans, dates, GUIDs, <see cref="JsonElement"/>s, or dictionaries and lists of those. Where reflection is available
/// (a normal runtime) any other value, such as a custom class, is still written through reflection.
/// </remarks>
public static class NotificationJson
{
    [SuppressMessage("Usage", "CA2263:Prefer generic overload when type is known", Justification = "The generic GetTypeInfo<T> does not exist on net10.0.")]
    private static JsonTypeInfo<Notification> Load() => (JsonTypeInfo<Notification>)CreateOptions().GetTypeInfo(typeof(Notification));

    private static readonly JsonTypeInfo<Notification> NotificationInfo = Load();

    /// <summary>Serializes a notification.</summary>
    public static string Serialize(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return JsonSerializer.Serialize(notification, NotificationInfo);
    }

    /// <summary>Reads a notification written by <see cref="Serialize"/>.</summary>
    /// <exception cref="JsonException">The text is not a valid serialized notification.</exception>
    public static Notification Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, NotificationInfo) ?? throw new JsonException("The JSON does not contain a notification.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        // Fresh options (not the context's own) so the generated metadata is created for these options and nested lookups can reach the fallback.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = NotificationJsonContext.Default,
        };

        if (JsonSerializer.IsReflectionEnabledByDefault)
        {
            AddReflectionFallback(options);
        }

        return options;
    }

    // Only reached when reflection is enabled (never under Native AOT), so the trimming and AOT warnings do not apply.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Guarded by JsonSerializer.IsReflectionEnabledByDefault.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Guarded by JsonSerializer.IsReflectionEnabledByDefault.")]
    private static void AddReflectionFallback(JsonSerializerOptions options) =>
        options.TypeInfoResolver = JsonTypeInfoResolver.Combine(NotificationJsonContext.Default, new DefaultJsonTypeInfoResolver());
}

// Source-generated metadata for the notification graph and the value types a token can hold.
// Metadata only (no generated fast path): the fast path resolves nested object values through the context's own options and would skip the reflection fallback.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Notification))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(List<object?>))]
internal sealed partial class NotificationJsonContext : JsonSerializerContext;
