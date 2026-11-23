using FirebaseAdmin.Messaging;
using FcmNotification = FirebaseAdmin.Messaging.Notification;

namespace UniversalNotifier.NET.Push.Fcm;

/// <summary>
/// Sends mobile push notifications through Firebase Cloud Messaging. Every device token is reported separately: a token
/// reported as <c>fcm.unregistered</c> belongs to an uninstalled or expired app and should be removed from your database.
/// </summary>
public sealed class FcmPushNotifier : NotifierBase<PushMessage>, IPushNotifier, IDisposable
{
    /// <summary>The provider name used for explicit selection.</summary>
    public const string Name = "fcm";

    private readonly IFcmGateway _gateway;

    /// <summary>Creates the notifier and connects to Firebase with the configured credentials. With dependency injection use <c>AddFcm</c> instead.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public FcmPushNotifier(FcmPushOptions options, NotifierContext context)
        : this(options, new FcmGateway(Validated(options), Name), context)
    {
    }

    internal FcmPushNotifier(FcmPushOptions options, IFcmGateway gateway, NotifierContext context)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
    }

    /// <inheritdoc />
    protected override string ProviderName => Name;

    /// <inheritdoc />
    public void Dispose() => _gateway.Dispose();

    /// <inheritdoc />
    protected override async Task<NotifierResponse> SendCoreAsync(PushMessage message, CancellationToken cancellationToken)
    {
        var targets = new List<(string Label, Message Payload)>(message.Tokens.Count + 1);
        foreach (var token in message.Tokens)
        {
            targets.Add((token, Build(message, token, topic: null)));
        }

        if (!string.IsNullOrWhiteSpace(message.Topic))
        {
            targets.Add(("topic:" + message.Topic, Build(message, token: null, message.Topic)));
        }

        var results = new List<RecipientResult>(targets.Count);
        for (var offset = 0; offset < targets.Count; offset += FcmGateway.MaxBatchSize)
        {
            var chunk = targets.Skip(offset).Take(FcmGateway.MaxBatchSize).ToArray();
            var sent = await _gateway.SendEachAsync([.. chunk.Select(t => t.Payload)], cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < chunk.Length; i++)
            {
                results.Add(sent[i].Success
                    ? new RecipientResult(chunk[i].Label, true, sent[i].MessageId)
                    : new RecipientResult(chunk[i].Label, false, Error: ToError(sent[i].ErrorCode)));
            }
        }

        return NotifierResponse.FromRecipients(results);
    }

    /// <inheritdoc />
    protected override NotifierError Classify(Exception exception) =>
        exception is FirebaseMessagingException { MessagingErrorCode: { } code } ? ToError(code) : base.Classify(exception);

    private static FcmPushOptions Validated(FcmPushOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return FcmPushOptions.Validate(options) is { } error ? throw new ArgumentException(error, nameof(options)) : options;
    }

    // Message.Token is marked obsolete in favour of Message.Fid, but Fid is the Firebase *installation* id. The
    // registration tokens apps get from getToken() still belong in Token, which is what PushMessage.Tokens holds.
#pragma warning disable CS0618
    private static Message Build(PushMessage message, string? token, string? topic) => new()
    {
        Token = token,
        Topic = topic,
        Notification = message.Title is null && message.Body is null
            ? null
            : new FcmNotification { Title = message.Title, Body = message.Body },
        Data = message.Data is { Count: > 0 } data ? new Dictionary<string, string>(data) : null,
    };
#pragma warning restore CS0618

    private static NotifierError ToError(MessagingErrorCode? code) => code switch
    {
        MessagingErrorCode.Unregistered =>
            new NotifierError(NotifierErrorKind.Permanent, "fcm.unregistered", "The device token is no longer registered. Remove it from your records."),
        MessagingErrorCode.InvalidArgument =>
            new NotifierError(NotifierErrorKind.Permanent, "fcm.invalid_argument", "FCM rejected the message or the device token as invalid."),
        MessagingErrorCode.SenderIdMismatch or MessagingErrorCode.ThirdPartyAuthError =>
            new NotifierError(NotifierErrorKind.Configuration, "fcm.unauthorized", "FCM rejected the credentials or the token belongs to a different Firebase project."),
        MessagingErrorCode.QuotaExceeded =>
            new NotifierError(NotifierErrorKind.Transient, "fcm.quota_exceeded", "FCM quota or rate limit exceeded."),
        MessagingErrorCode.Unavailable =>
            new NotifierError(NotifierErrorKind.Transient, "fcm.unavailable", "FCM is temporarily unavailable."),
        _ => new NotifierError(NotifierErrorKind.Unknown, "fcm.error", $"FCM failed to send the message ({code?.ToString() ?? "unknown"})."),
    };
}
