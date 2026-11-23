using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalNotifier.NET.Internal;

namespace UniversalNotifier.NET.Twilio;

/// <summary>The part of the Twilio Messages API that SMS and WhatsApp share: authentication, the request, error mapping and per-recipient fan-out.</summary>
internal sealed class TwilioApi
{
    private readonly HttpClient _http;
    private readonly Uri _messagesUri;
    private readonly AuthenticationHeaderValue _authorization;
    private readonly int _maxConcurrency;

    public TwilioApi(TwilioOptions options, HttpClient http)
    {
        if (TwilioOptions.Validate(options) is { } error)
        {
            throw new ArgumentException(error, nameof(options));
        }

        _http = http;
        _maxConcurrency = options.MaxConcurrency;
        _messagesUri = new Uri(options.BaseAddress, $"/2010-04-01/Accounts/{Uri.EscapeDataString(options.AccountSid)}/Messages.json");

        var (user, secret) = !string.IsNullOrWhiteSpace(options.ApiKeySid)
            ? (options.ApiKeySid, options.ApiKeySecret)
            : (options.AccountSid, options.AuthToken);
        _authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));
    }

    public static bool IsMessagingServiceSid(string sender) =>
        sender.Length == 34 && sender.StartsWith("MG", StringComparison.Ordinal);

    public static NotifierResponse MissingSender(string code, string what) =>
        NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Configuration, code, $"No {what} sender is configured. Set it on TwilioOptions or on the message."));

    /// <summary>Posts one message and maps the answer. Twilio's error text usually repeats the phone number, so only its numeric code and docs link are surfaced.</summary>
    public async Task<NotifierResponse> PostAsync(List<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _messagesUri) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Authorization = _authorization;

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        using var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            return NotifierResponse.Accepted(ProviderHttp.GetString(json, "sid"), status);
        }

        var twilioCode = json is { RootElement: { ValueKind: JsonValueKind.Object } root }
            && root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
                ? code.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
        var error = ProviderHttp.ClassifyStatus(response.StatusCode, "Twilio", null);
        if (twilioCode is not null)
        {
            error = error with { Message = $"{error.Message} Twilio error {twilioCode}: https://www.twilio.com/docs/errors/{twilioCode}" };
        }

        return NotifierResponse.Failed(error, status, ProviderHttp.RetryAfter(response));
    }

    /// <summary>Sends to each recipient (a few in parallel) and reports every one separately. A single recipient keeps the plain response, including its HTTP status and retry hint.</summary>
    public async Task<NotifierResponse> SendEachAsync(
        IReadOnlyList<string> recipients,
        Func<string, CancellationToken, Task<NotifierResponse>> send,
        Func<Exception, NotifierError> classify,
        CancellationToken cancellationToken)
    {
        if (recipients.Count == 1)
        {
            return await send(recipients[0], cancellationToken).ConfigureAwait(false);
        }

        var results = new RecipientResult[recipients.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, results.Length),
            new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrency, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var recipient = recipients[index];
                try
                {
                    var response = await send(recipient, token).ConfigureAwait(false);
                    results[index] = new RecipientResult(recipient, response.Success, response.ProviderMessageId, response.Success ? null : response.Errors[0]);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    results[index] = new RecipientResult(recipient, false, Error: classify(exception));
                }
            }).ConfigureAwait(false);

        return NotifierResponse.FromRecipients(results);
    }
}

// Source-generated so serialization needs no reflection (trim and AOT friendly).
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class TwilioJsonContext : JsonSerializerContext;
