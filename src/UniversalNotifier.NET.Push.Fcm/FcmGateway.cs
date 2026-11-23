using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace UniversalNotifier.NET.Push.Fcm;

/// <summary>The outcome of one FCM message.</summary>
internal readonly record struct FcmResult(string? MessageId, MessagingErrorCode? ErrorCode)
{
    public bool Success => MessageId is not null;
}

/// <summary>The seam between the notifier and the Firebase SDK, so the notifier can be tested without Google.</summary>
internal interface IFcmGateway : IDisposable
{
    /// <summary>Sends up to <see cref="FcmGateway.MaxBatchSize"/> messages and returns one result per message, in order.</summary>
    Task<IReadOnlyList<FcmResult>> SendEachAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken);
}

internal sealed class FcmGateway : IFcmGateway
{
    public const int MaxBatchSize = 500;

    private readonly FirebaseApp _app;
    private readonly FirebaseMessaging _messaging;

    public FcmGateway(FcmPushOptions options, string name)
    {
        var credential = options switch
        {
            { CredentialJson: { Length: > 0 } json } => CredentialFactory.FromJson<ServiceAccountCredential>(json).ToGoogleCredential(),
            { CredentialPath: { Length: > 0 } path } => CredentialFactory.FromFile<ServiceAccountCredential>(path).ToGoogleCredential(),
            _ => GoogleCredential.GetApplicationDefault(),
        };

        // Each notifier gets its own uniquely named app so several notifiers (or tests) never collide on the SDK's global registry.
        _app = FirebaseApp.Create(
            new AppOptions { Credential = credential, ProjectId = string.IsNullOrWhiteSpace(options.ProjectId) ? null : options.ProjectId },
            $"UniversalNotifier.{name}.{Guid.NewGuid():N}");
        _messaging = FirebaseMessaging.GetMessaging(_app);
    }

    public async Task<IReadOnlyList<FcmResult>> SendEachAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        var batch = await _messaging.SendEachAsync(messages, cancellationToken).ConfigureAwait(false);
        var results = new FcmResult[batch.Responses.Count];
        for (var i = 0; i < results.Length; i++)
        {
            var response = batch.Responses[i];
            results[i] = response.IsSuccess
                ? new FcmResult(response.MessageId, null)
                : new FcmResult(null, response.Exception?.MessagingErrorCode ?? MessagingErrorCode.Internal);
        }

        return results;
    }

    public void Dispose() => _app.Delete();
}
