using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UniversalNotifier.NET.Internal;

namespace UniversalNotifier.NET.Sms.Twilio;

/// <summary>
/// Sends SMS through the Twilio Messages REST API. Twilio sends one message per recipient, so a message with several
/// recipients makes one call each (a few in parallel) and reports every recipient separately.
/// </summary>
public sealed class TwilioSmsNotifier : NotifierBase<SmsMessage>, ISmsNotifier
{
    /// <summary>The provider name used for explicit selection.</summary>
    public const string Name = "twilio";

    private readonly HttpClient _http;
    private readonly Uri _messagesUri;
    private readonly string _defaultFrom;
    private readonly int _maxConcurrency;
    private readonly AuthenticationHeaderValue _authorization;

    /// <summary>Creates the notifier. With dependency injection use <c>AddTwilioSms</c> instead.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public TwilioSmsNotifier(TwilioSmsOptions options, HttpClient httpClient, NotifierContext context)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (TwilioSmsOptions.Validate(options) is { } error)
        {
            throw new ArgumentException(error, nameof(options));
        }

        _http = httpClient;
        _defaultFrom = options.From;
        _maxConcurrency = options.MaxConcurrency;
        _messagesUri = new Uri(options.BaseAddress, $"/2010-04-01/Accounts/{Uri.EscapeDataString(options.AccountSid)}/Messages.json");

        var (user, secret) = !string.IsNullOrWhiteSpace(options.ApiKeySid)
            ? (options.ApiKeySid, options.ApiKeySecret)
            : (options.AccountSid, options.AuthToken);
        _authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));
    }

    /// <inheritdoc />
    protected override string ProviderName => Name;

    /// <inheritdoc />
    protected override async Task<NotifierResponse> SendCoreAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        if (message.To.Count == 1)
        {
            return await SendToAsync(message, message.To[0], cancellationToken).ConfigureAwait(false);
        }

        var results = new RecipientResult[message.To.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, results.Length),
            new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrency, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var recipient = message.To[index];
                try
                {
                    var response = await SendToAsync(message, recipient, token).ConfigureAwait(false);
                    results[index] = new RecipientResult(
                        recipient.Value, response.Success, response.ProviderMessageId, response.Success ? null : response.Errors[0]);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    results[index] = new RecipientResult(recipient.Value, false, Error: Classify(exception));
                }
            }).ConfigureAwait(false);

        return NotifierResponse.FromRecipients(results);
    }

    private async Task<NotifierResponse> SendToAsync(SmsMessage message, PhoneNumber to, CancellationToken cancellationToken)
    {
        var sender = message.From ?? _defaultFrom;
        var fields = new List<KeyValuePair<string, string>>(3)
        {
            new("To", to.Value),
            new(IsMessagingServiceSid(sender) ? "MessagingServiceSid" : "From", sender),
            new("Body", message.Body),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _messagesUri) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Authorization = _authorization;

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        using var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            return NotifierResponse.Accepted(ProviderHttp.GetString(json, "sid"), status);
        }

        // Twilio's error text usually repeats the phone number, so only its numeric code and documentation link are surfaced.
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

    private static bool IsMessagingServiceSid(string sender) =>
        sender.Length == 34 && sender.StartsWith("MG", StringComparison.Ordinal);
}
