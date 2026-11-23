using System.Net;
using System.Text.Json;

namespace UniversalNotifier.NET.Internal;

// Compiled into each HTTP-based provider (it is not a public type of any package).
internal static class ProviderHttp
{
    /// <summary>Reads a JSON body. Returns <see langword="null"/> for an empty or non-JSON body instead of throwing.</summary>
    public static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a string property of the root object. Returns <see langword="null"/> when absent or not a string.</summary>
    public static string? GetString(JsonDocument? document, string name) =>
        document is { RootElement: { ValueKind: JsonValueKind.Object } root }
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Maps an HTTP failure to an error kind that tells callers whether repeating is safe.</summary>
    public static NotifierError ClassifyStatus(HttpStatusCode status, string provider, string? providerMessage)
    {
        var code = (int)status;
        var kind = code switch
        {
            401 or 403 => NotifierErrorKind.Configuration,
            429 or 503 => NotifierErrorKind.Transient,
            >= 400 and < 500 and not 408 => NotifierErrorKind.Permanent,
            _ => NotifierErrorKind.Unknown, // 408 and 5xx: the provider may or may not have processed the request
        };

        var suffix = code switch
        {
            401 or 403 => "unauthorized",
            429 => "rate_limited",
            _ => "rejected",
        };

        var text = string.IsNullOrWhiteSpace(providerMessage)
            ? $"{provider} returned HTTP {code}."
            : $"{provider} returned HTTP {code}: {Truncate(providerMessage)}";

        return new NotifierError(kind, $"{provider.ToLowerInvariant()}.{suffix}", text);
    }

    /// <summary>The wait the provider asked for in its Retry-After header, if any.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "...";
}
