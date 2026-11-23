using Microsoft.Extensions.Logging;

namespace UniversalNotifier.NET;

/// <summary>Source-generated log messages. They never include recipients, content or credentials.</summary>
internal static partial class NotifierLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "{Provider} sending {MessageType} {MessageId} (attempt {Attempt})")]
    public static partial void Sending(ILogger logger, string provider, string messageType, string messageId, int attempt);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "{Provider} {Status} {MessageType} {MessageId} (provider id {ProviderMessageId})")]
    public static partial void Completed(ILogger logger, string provider, DeliveryStatus status, string messageType, string messageId, string? providerMessageId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "{Provider} {Status} {MessageType} {MessageId} after {Attempts} attempt(s): {ErrorCodes}")]
    public static partial void Failed(ILogger logger, string provider, DeliveryStatus status, string messageType, string messageId, int attempts, string errorCodes);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "{Provider} retrying {MessageType} {MessageId} in {Delay} (attempt {Attempt} failed)")]
    public static partial void Retrying(ILogger logger, string provider, string messageType, string messageId, TimeSpan delay, int attempt);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "{Provider} rejected invalid {MessageType} {MessageId}: {ErrorCodes}")]
    public static partial void Invalid(ILogger logger, string provider, string messageType, string messageId, string errorCodes);
}
