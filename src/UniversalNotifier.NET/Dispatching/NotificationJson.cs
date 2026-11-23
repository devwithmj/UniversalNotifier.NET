using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniversalNotifier.NET;

/// <summary>
/// Converts a <see cref="Notification"/> to and from JSON so it can be stored or put on a durable queue (database,
/// RabbitMQ, Azure Service Bus, ...). The JSON contains the recipient's addresses and the message text, so protect it
/// like any other personal data. Token values come back as JSON elements, which templates read just like the originals.
/// </summary>
public static class NotificationJson
{
    // shortcut: reflection-based serialization because notification data can hold any object; use your own source-generated context if you need Native AOT.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes a notification.</summary>
    public static string Serialize(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return JsonSerializer.Serialize(notification, Options);
    }

    /// <summary>Reads a notification written by <see cref="Serialize"/>.</summary>
    /// <exception cref="JsonException">The text is not a valid serialized notification.</exception>
    public static Notification Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<Notification>(json, Options) ?? throw new JsonException("The JSON does not contain a notification.");
    }
}
