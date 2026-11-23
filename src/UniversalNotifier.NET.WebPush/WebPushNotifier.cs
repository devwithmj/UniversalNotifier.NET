using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using LibPushMessage = Lib.Net.Http.WebPush.PushMessage;

namespace UniversalNotifier.NET.WebPush;

/// <summary>
/// Sends browser push notifications (Web Push with VAPID) using Lib.Net.Http.WebPush for the encryption and signing.
/// Every subscription is reported separately: <c>webpush.gone</c> means the browser unsubscribed or the subscription
/// expired and it should be removed from your records. Uses its own <see cref="HttpClient"/> because push endpoint URLs
/// identify a user's browser and must not be written to HTTP client logs.
/// </summary>
public sealed class WebPushNotifier : NotifierBase<WebPushMessage>, IWebPushNotifier, IDisposable
{
    /// <summary>The provider name used for explicit selection.</summary>
    public const string Name = "webpush";

    // RFC 8291: push services must accept 4096 bytes after encryption; the aes128gcm header and tag take 103 of them.
    private const int MaxPayloadBytes = 3993;

    private readonly HttpClient _http;
    private readonly PushServiceClient _client;
    private readonly VapidAuthentication _vapid;
    private readonly int _maxConcurrency;

    /// <summary>Creates the notifier. With dependency injection use <c>AddWebPush</c> instead.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public WebPushNotifier(WebPushOptions options, NotifierContext context)
        : this(options, context, new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    internal WebPushNotifier(WebPushOptions options, NotifierContext context, HttpMessageHandler handler)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (WebPushOptions.Validate(options) is { } error)
        {
            handler.Dispose();
            throw new ArgumentException(error, nameof(options));
        }

        _maxConcurrency = options.MaxConcurrency;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = options.Timeout };
        _vapid = new VapidAuthentication(options.PublicKey, options.PrivateKey) { Subject = options.Subject };
        _client = new PushServiceClient(_http)
        {
            DefaultAuthentication = _vapid,
            AutoRetryAfter = false, // retries are the notifier's job (see NotifierOptions.Retry), so there is one policy
        };
    }

    /// <inheritdoc />
    protected override string ProviderName => Name;

    /// <inheritdoc />
    public void Dispose()
    {
        _vapid.Dispose();
        _http.Dispose();
    }

    internal static string BuildPayload(WebPushMessage message) =>
        message.Payload ?? JsonSerializer.Serialize(
            new WebPushPayload(message.Title, message.Body, message.Data is { Count: > 0 } data ? new Dictionary<string, string>(data) : null),
            WebPushJsonContext.Default.WebPushPayload);

    /// <inheritdoc />
    protected override async Task<NotifierResponse> SendCoreAsync(WebPushMessage message, CancellationToken cancellationToken)
    {
        var payload = BuildPayload(message);
        if (Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes)
        {
            return NotifierResponse.Failed(new NotifierError(
                NotifierErrorKind.Validation, "webpush.payload_too_large", $"A web push payload is limited to {MaxPayloadBytes} bytes."));
        }

        var results = new RecipientResult[message.Subscriptions.Count];
        var waits = new TimeSpan?[results.Length];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, results.Length),
            new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrency, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var subscription = message.Subscriptions[index];
                try
                {
                    await _client.RequestPushMessageDeliveryAsync(ToSubscription(subscription), ToPushMessage(message, payload), token).ConfigureAwait(false);
                    results[index] = new RecipientResult(subscription.Endpoint, true);
                }
                catch (PushServiceClientException exception)
                {
                    results[index] = new RecipientResult(subscription.Endpoint, false, Error: ToError(exception.StatusCode));
                    waits[index] = exception.Headers?.RetryAfter is { } retry ? retry.Delta ?? (retry.Date - DateTimeOffset.UtcNow) : null;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    results[index] = new RecipientResult(subscription.Endpoint, false, Error: Classify(exception));
                }
            }).ConfigureAwait(false);

        return NotifierResponse.FromRecipients(results, retryAfter: waits.Where(w => w is not null).Max());
    }

    private static PushSubscription ToSubscription(WebPushSubscription subscription)
    {
        var result = new PushSubscription { Endpoint = subscription.Endpoint };
        result.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
        result.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);
        return result;
    }

    private static LibPushMessage ToPushMessage(WebPushMessage message, string payload) => new(payload)
    {
        Topic = message.Topic,
        TimeToLive = message.TimeToLive is { } ttl ? (int)Math.Min(ttl.TotalSeconds, int.MaxValue) : null,
        Urgency = message.Urgency switch
        {
            WebPushUrgency.VeryLow => PushMessageUrgency.VeryLow,
            WebPushUrgency.Low => PushMessageUrgency.Low,
            WebPushUrgency.High => PushMessageUrgency.High,
            _ => PushMessageUrgency.Normal,
        },
    };

    private static NotifierError ToError(HttpStatusCode status) => (int)status switch
    {
        404 or 410 => new NotifierError(NotifierErrorKind.Permanent, "webpush.gone", "The subscription is no longer valid. Remove it from your records."),
        401 or 403 => new NotifierError(NotifierErrorKind.Configuration, "webpush.unauthorized", "The push service rejected the VAPID credentials."),
        413 => new NotifierError(NotifierErrorKind.Permanent, "webpush.payload_too_large", "The push service rejected the payload as too large."),
        429 => new NotifierError(NotifierErrorKind.Transient, "webpush.rate_limited", "The push service is rate limiting this server."),
        >= 400 and < 500 => new NotifierError(NotifierErrorKind.Permanent, "webpush.rejected", $"The push service rejected the push (HTTP {(int)status})."),
        _ => new NotifierError(NotifierErrorKind.Unknown, "webpush.error", $"The push service failed to accept the push (HTTP {(int)status})."),
    };
}

internal sealed record WebPushPayload(string? Title, string? Body, Dictionary<string, string>? Data);

// Source-generated so serialization needs no reflection (trim and AOT friendly).
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WebPushPayload))]
internal sealed partial class WebPushJsonContext : JsonSerializerContext;
