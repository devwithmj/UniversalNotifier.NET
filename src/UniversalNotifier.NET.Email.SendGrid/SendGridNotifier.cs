using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using UniversalNotifier.NET.Internal;

namespace UniversalNotifier.NET.Email.SendGrid;

/// <summary>
/// Sends email through the SendGrid v3 Mail Send API. Each <see cref="EmailMessage.To"/> recipient becomes its own
/// SendGrid personalization, so every recipient gets an individual message and never sees the others (up to 1000 per
/// request; larger sends are split). Repeated addresses in one message are sent once, which SendGrid requires.
/// </summary>
public sealed class SendGridNotifier : NotifierBase<EmailMessage>, IEmailNotifier
{
    /// <summary>The provider name used for explicit selection.</summary>
    public const string Name = "sendgrid";

    private const int MaxPersonalizationsPerRequest = 1000;
    private const int MaxCategories = 10;

    private readonly HttpClient _http;
    private readonly Uri _sendUri;
    private readonly SendGridAddress _from;
    private readonly AuthenticationHeaderValue _authorization;

    /// <summary>Creates the notifier. With dependency injection use <c>AddSendGrid</c> instead.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public SendGridNotifier(SendGridOptions options, HttpClient httpClient, NotifierContext context)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (SendGridOptions.Validate(options) is { } error)
        {
            throw new ArgumentException(error, nameof(options));
        }

        _http = httpClient;
        _from = ToAddress(options.From);
        _sendUri = new Uri(options.Region == SendGridRegion.Eu ? "https://api.eu.sendgrid.com/v3/mail/send" : "https://api.sendgrid.com/v3/mail/send");
        _authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }

    /// <inheritdoc />
    protected override string ProviderName => Name;

    /// <inheritdoc />
    protected override async Task<NotifierResponse> SendCoreAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (message.Tags.Count > MaxCategories)
        {
            return NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Validation, "sendgrid.tags.limit", $"SendGrid allows at most {MaxCategories} categories (tags) per message."));
        }

        if (message.To.Count == 0)
        {
            return NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Validation, "sendgrid.to.required", "SendGrid needs at least one To recipient."));
        }

        var recipients = message.To.DistinctBy(a => a.Address, StringComparer.OrdinalIgnoreCase).ToArray();
        if (recipients.Length <= MaxPersonalizationsPerRequest)
        {
            return await SendRequestAsync(message, recipients, cancellationToken).ConfigureAwait(false);
        }

        var results = new List<RecipientResult>(recipients.Length);
        foreach (var chunk in recipients.Chunk(MaxPersonalizationsPerRequest))
        {
            var response = await SendRequestAsync(message, chunk, cancellationToken).ConfigureAwait(false);
            results.AddRange(chunk.Select(r => new RecipientResult(r.Address, response.Success, response.ProviderMessageId, response.Success ? null : response.Errors[0])));
        }

        return NotifierResponse.FromRecipients(results);
    }

    private async Task<NotifierResponse> SendRequestAsync(EmailMessage message, EmailAddress[] to, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _sendUri)
        {
            Content = JsonContent.Create(Build(message, to), SendGridJsonContext.Default.SendGridMail),
        };
        request.Headers.Authorization = _authorization;

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return NotifierResponse.Accepted(response.Headers.TryGetValues("X-Message-Id", out var ids) ? ids.FirstOrDefault() : null, status);
        }

        // SendGrid's error text can quote addresses, so only the names of the offending fields are surfaced.
        using var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var fields = FieldNames(json);
        var error = ProviderHttp.ClassifyStatus(response.StatusCode, "SendGrid", null);
        if (fields.Length > 0)
        {
            error = error with { Message = $"{error.Message} Fields: {string.Join(", ", fields)}." };
        }

        return NotifierResponse.Failed(error, status, ProviderHttp.RetryAfter(response));
    }

    private SendGridMail Build(EmailMessage message, EmailAddress[] to) => new(
        [.. to.Select(r => new SendGridPersonalization(
            [ToAddress(r)],
            to.Length == 1 && message.Cc.Count > 0 ? [.. message.Cc.Select(ToAddress)] : null,
            to.Length == 1 && message.Bcc.Count > 0 ? [.. message.Bcc.Select(ToAddress)] : null))],
        message.From is { } from ? ToAddress(from) : _from,
        message.ReplyTo is { } replyTo ? ToAddress(replyTo) : null,
        message.Subject,
        Contents(message),
        message.Attachments.Count == 0 ? null : [.. message.Attachments.Select(a => new SendGridAttachment(
            Convert.ToBase64String(a.Content.Span), a.FileName, a.ContentType, a.ContentId is null ? "attachment" : "inline", a.ContentId))],
        message.Headers is { Count: > 0 } headers ? new Dictionary<string, string>(headers) : null,
        message.Tags.Count == 0 ? null : [.. message.Tags],
        message.Metadata is { Count: > 0 } metadata ? new Dictionary<string, string>(metadata) : null);

    // SendGrid requires text/plain before text/html.
    private static SendGridContent[] Contents(EmailMessage message)
    {
        var contents = new List<SendGridContent>(2);
        if (!string.IsNullOrEmpty(message.TextBody))
        {
            contents.Add(new SendGridContent("text/plain", message.TextBody));
        }

        if (!string.IsNullOrEmpty(message.HtmlBody))
        {
            contents.Add(new SendGridContent("text/html", message.HtmlBody));
        }

        return [.. contents];
    }

    private static SendGridAddress ToAddress(EmailAddress address) =>
        new(address.Address, string.IsNullOrWhiteSpace(address.Name) ? null : address.Name);

    private static string[] FieldNames(JsonDocument? json)
    {
        if (json is not { RootElement: { ValueKind: JsonValueKind.Object } root }
            || !root.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. errors.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct()
            .Take(3)!];
    }
}
