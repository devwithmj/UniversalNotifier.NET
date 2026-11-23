using System.Net.Http.Headers;
using System.Text.Json;
using UniversalNotifier.NET.Internal;

namespace UniversalNotifier.NET.Email.Ses;

/// <summary>
/// Sends email through the Amazon SES v2 API. Every <see cref="EmailMessage.To"/> recipient gets its own message (a few
/// requests in parallel) and is reported separately, so recipients never see each other. Requests are signed with AWS
/// Signature Version 4 using the configured credentials; no AWS SDK is needed.
/// </summary>
public sealed class SesNotifier : NotifierBase<EmailMessage>, IEmailNotifier
{
    /// <summary>The provider name used for explicit selection.</summary>
    public const string Name = "ses";

    private const string ContentType = "application/json";
    private const string Path = "/v2/email/outbound-emails";

    private readonly HttpClient _http;
    private readonly Uri _sendUri;
    private readonly SesOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the notifier. With dependency injection use <c>AddSes</c> instead.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public SesNotifier(SesOptions options, HttpClient httpClient, NotifierContext context)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (SesOptions.Validate(options) is { } error)
        {
            throw new ArgumentException(error, nameof(options));
        }

        _http = httpClient;
        _options = options;
        _time = context.TimeProvider;
        _sendUri = new Uri(options.BaseAddress ?? new Uri($"https://email.{options.Region}.amazonaws.com"), Path);
    }

    /// <inheritdoc />
    protected override string ProviderName => Name;

    /// <inheritdoc />
    protected override async Task<NotifierResponse> SendCoreAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var recipients = message.To.DistinctBy(a => a.Address, StringComparer.OrdinalIgnoreCase).ToArray();
        if (recipients.Length == 1)
        {
            return await SendOneAsync(message, recipients[0], cancellationToken).ConfigureAwait(false);
        }

        var results = new RecipientResult[recipients.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, results.Length),
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrency, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var recipient = recipients[index];
                try
                {
                    var response = await SendOneAsync(message, recipient, token).ConfigureAwait(false);
                    results[index] = new RecipientResult(recipient.Address, response.Success, response.ProviderMessageId, response.Success ? null : response.Errors[0]);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    results[index] = new RecipientResult(recipient.Address, false, Error: Classify(exception));
                }
            }).ConfigureAwait(false);

        return NotifierResponse.FromRecipients(results);
    }

    private async Task<NotifierResponse> SendOneAsync(EmailMessage message, EmailAddress to, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(Build(message, to), SesJsonContext.Default.SesSendEmail);
        var signed = AwsSigV4.Sign("POST", _sendUri.Authority, _sendUri.AbsolutePath, string.Empty, body, ContentType, _options.Region, "ses",
            _options.AccessKeyId, _options.SecretAccessKey, _options.SessionToken, _time.GetUtcNow());

        using var request = new HttpRequestMessage(HttpMethod.Post, _sendUri) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
        request.Headers.TryAddWithoutValidation("X-Amz-Date", signed.AmzDate);
        request.Headers.TryAddWithoutValidation("Authorization", signed.Authorization);
        if (!string.IsNullOrEmpty(_options.SessionToken))
        {
            request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", _options.SessionToken);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        using var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return NotifierResponse.Accepted(ProviderHttp.GetString(json, "MessageId"), status);
        }

        return NotifierResponse.Failed(MapError(response), status, ProviderHttp.RetryAfter(response));
    }

    // The service message text can quote addresses, so only the error type (a fixed name such as MessageRejected) is surfaced.
    private static NotifierError MapError(HttpResponseMessage response)
    {
        var type = response.Headers.TryGetValues("x-amzn-ErrorType", out var values) ? values.FirstOrDefault()?.Split(':')[0] : null;
        var error = ProviderHttp.ClassifyStatus(response.StatusCode, "SES", null);
        if (string.IsNullOrWhiteSpace(type))
        {
            return error;
        }

        var kind = type switch
        {
            "AccountSuspendedException" or "SendingPausedException" or "MailFromDomainNotVerifiedException" => NotifierErrorKind.Configuration,
            "ThrottlingException" or "TooManyRequestsException" => NotifierErrorKind.Transient,
            _ => error.Kind,
        };

        return error with { Kind = kind, Message = $"{error.Message} SES error: {type}." };
    }

    private SesSendEmail Build(EmailMessage message, EmailAddress to)
    {
        // shortcut: tags become EmailTags with the value "true" and metadata keys are sent as tag names, which SES limits to letters, digits, _ and -; revisit if users need richer tags.
        var tags = message.Tags.Select(t => new SesTag(t, "true"))
            .Concat(message.Metadata is { Count: > 0 } metadata ? metadata.Select(m => new SesTag(m.Key, m.Value)) : [])
            .ToArray();

        return new SesSendEmail(
            Format(message.From ?? _options.From),
            new SesDestination(
                [Format(to)],
                message.Cc.Count > 0 && message.To.Count == 1 ? [.. message.Cc.Select(Format)] : null,
                message.Bcc.Count > 0 && message.To.Count == 1 ? [.. message.Bcc.Select(Format)] : null),
            message.ReplyTo is { } replyTo ? [Format(replyTo)] : null,
            new SesContent(new SesSimple(
                new SesData(message.Subject),
                new SesBody(
                    string.IsNullOrEmpty(message.TextBody) ? null : new SesData(message.TextBody),
                    string.IsNullOrEmpty(message.HtmlBody) ? null : new SesData(message.HtmlBody)),
                message.Headers is { Count: > 0 } headers ? [.. headers.Select(h => new SesHeader(h.Key, h.Value))] : null,
                message.Attachments.Count == 0 ? null : [.. message.Attachments.Select(a => new SesAttachment(
                    a.Content.ToArray(), a.FileName, a.ContentType, a.ContentId is null ? "ATTACHMENT" : "INLINE", a.ContentId))])),
            tags.Length == 0 ? null : tags,
            _options.ConfigurationSetName);
    }

    // "Name" <address>, with the name quoted so it cannot split or inject recipients.
    private static string Format(EmailAddress address) =>
        string.IsNullOrWhiteSpace(address.Name)
            ? address.Address
            : $"\"{address.Name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\" <{address.Address}>";
}
