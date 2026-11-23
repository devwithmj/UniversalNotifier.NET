using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UniversalNotifier.NET.Receipts;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Hosts provider delivery webhooks in an ASP.NET Core application.</summary>
public static partial class DeliveryReceiptEndpoints
{
    private const int MaxBodyBytes = 1 << 20;

    /// <summary>
    /// Maps a POST endpoint that verifies the provider's signature (<paramref name="webhook"/>) and gives every delivery event to each
    /// registered <see cref="IDeliveryReceiptHandler"/>. It answers 401 when the signature is wrong, 413 above 1 MiB, 200 when handled and
    /// 500 when a handler throws (so the provider retries). Nothing from the request is logged except the provider name on a rejection.
    /// </summary>
    /// <remarks>
    /// Twilio signs the exact public URL, so behind a proxy or load balancer enable forwarded-header handling
    /// (<c>app.UseForwardedHeaders()</c>) so the scheme and host the application sees match what you configured at the provider.
    /// </remarks>
    public static IEndpointConventionBuilder MapDeliveryReceipts(this IEndpointRouteBuilder endpoints, string pattern, IDeliveryWebhook webhook)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(webhook);

        return endpoints.MapPost(pattern, (RequestDelegate)(http => HandleAsync(http, webhook)));
    }

    private static async Task HandleAsync(HttpContext http, IDeliveryWebhook webhook)
    {
        var cancellationToken = http.RequestAborted;
        var body = await ReadBodyAsync(http.Request, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            await Results.StatusCode(StatusCodes.Status413PayloadTooLarge).ExecuteAsync(http).ConfigureAwait(false);
            return;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in http.Request.Headers)
        {
            headers[name] = values.ToString();
        }

        var result = await webhook.ProcessAsync(new WebhookRequest(new Uri(http.Request.GetEncodedUrl()), headers, body), cancellationToken).ConfigureAwait(false);
        if (!result.Authentic)
        {
            Log.Rejected(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UniversalNotifier.NET.Receipts"), webhook.Provider);
            await Results.Unauthorized().ExecuteAsync(http).ConfigureAwait(false);
            return;
        }

        var handlers = http.RequestServices.GetServices<IDeliveryReceiptHandler>().ToArray();
        foreach (var deliveryEvent in result.Events)
        {
            foreach (var handler in handlers)
            {
                await handler.HandleAsync(deliveryEvent, cancellationToken).ConfigureAwait(false);
            }
        }

        await Results.Ok().ExecuteAsync(http).ConfigureAwait(false);
    }

    // Returns null when the body is larger than the limit.
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "A {Provider} delivery webhook call failed signature verification and was ignored.")]
        public static partial void Rejected(ILogger logger, string provider);
    }
}
