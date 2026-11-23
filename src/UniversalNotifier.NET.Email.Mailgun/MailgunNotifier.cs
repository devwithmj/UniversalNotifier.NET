using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UniversalNotifier.NET.Internal;

namespace UniversalNotifier.NET.Email.Mailgun;

/// <summary>
/// Sends email through the Mailgun HTTP API. A message with several <see cref="EmailMessage.To"/> recipients is sent as
/// Mailgun batch sends (up to 1000 per call), so every recipient gets an individual message and never sees the others.
/// </summary>
public sealed class MailgunNotifier : NotifierBase<EmailMessage>, IEmailNotifier
{
    /// <summary>The provider name used for explicit selection.</summary>
    public const string Name = "mailgun";

    private const int MaxRecipientsPerRequest = 1000;
    private const int MaxTags = 3;

    private readonly HttpClient _http;
    private readonly Uri _messagesUri;
    private readonly string _from;
    private readonly AuthenticationHeaderValue _authorization;

    /// <summary>Creates the notifier. With dependency injection use <c>AddMailgun</c> instead.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public MailgunNotifier(MailgunOptions options, HttpClient httpClient, NotifierContext context)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (MailgunOptions.Validate(options) is { } error)
        {
            throw new ArgumentException(error, nameof(options));
        }

        _http = httpClient;
        _from = options.From.ToString();
        var host = options.Region == MailgunRegion.Eu ? "api.eu.mailgun.net" : "api.mailgun.net";
        _messagesUri = new Uri($"https://{host}/v3/{Uri.EscapeDataString(options.Domain)}/messages");
        _authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("api:" + options.ApiKey)));
    }

    /// <inheritdoc />
    protected override string ProviderName => Name;

    /// <inheritdoc />
    protected override async Task<NotifierResponse> SendCoreAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (message.Tags.Count > MaxTags)
        {
            return NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Validation, "mailgun.tags.limit", $"Mailgun allows at most {MaxTags} tags per message."));
        }

        if (message.To.Count == 0)
        {
            return NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Validation, "mailgun.to.required", "Mailgun needs at least one To recipient."));
        }

        if (message.To.Count <= MaxRecipientsPerRequest)
        {
            return await SendRequestAsync(message, message.To, cancellationToken).ConfigureAwait(false);
        }

        var results = new List<RecipientResult>(message.To.Count);
        for (var offset = 0; offset < message.To.Count; offset += MaxRecipientsPerRequest)
        {
            var chunk = message.To.Skip(offset).Take(MaxRecipientsPerRequest).ToArray();
            var response = await SendRequestAsync(message, chunk, cancellationToken).ConfigureAwait(false);
            foreach (var recipient in chunk)
            {
                results.Add(new RecipientResult(
                    recipient.Address, response.Success, response.ProviderMessageId, response.Success ? null : response.Errors[0]));
            }
        }

        return NotifierResponse.FromRecipients(results);
    }

    private async Task<NotifierResponse> SendRequestAsync(EmailMessage message, IReadOnlyList<EmailAddress> to, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _messagesUri)
        {
            Content = BuildForm(message, to),
        };
        request.Headers.Authorization = _authorization;

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        using var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        return response.IsSuccessStatusCode
            ? NotifierResponse.Accepted(ProviderHttp.GetString(json, "id"), status)
            : NotifierResponse.Failed(
                ProviderHttp.ClassifyStatus(response.StatusCode, "Mailgun", ProviderHttp.GetString(json, "message")),
                status,
                ProviderHttp.RetryAfter(response));
    }

    private MultipartFormDataContent BuildForm(EmailMessage message, IReadOnlyList<EmailAddress> to)
    {
        var form = new MultipartFormDataContent();
        Add(form, "from", message.From?.ToString() ?? _from);

        foreach (var recipient in to)
        {
            Add(form, "to", recipient.ToString());
        }

        foreach (var recipient in message.Cc)
        {
            Add(form, "cc", recipient.ToString());
        }

        foreach (var recipient in message.Bcc)
        {
            Add(form, "bcc", recipient.ToString());
        }

        if (to.Count > 1)
        {
            // Giving every recipient their own variables makes Mailgun send individual messages, so addresses stay private.
            var variables = new Dictionary<string, object>(to.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < to.Count; i++)
            {
                variables[to[i].Address] = new { id = i + 1 };
            }

            Add(form, "recipient-variables", JsonSerializer.Serialize(variables));
        }

        Add(form, "subject", message.Subject);
        if (!string.IsNullOrEmpty(message.TextBody))
        {
            Add(form, "text", message.TextBody);
        }

        if (!string.IsNullOrEmpty(message.HtmlBody))
        {
            Add(form, "html", message.HtmlBody);
        }

        if (message.ReplyTo is { } replyTo)
        {
            Add(form, "h:Reply-To", replyTo.ToString());
        }

        if (message.Headers is not null)
        {
            foreach (var (name, value) in message.Headers)
            {
                Add(form, "h:" + name, value);
            }
        }

        foreach (var tag in message.Tags)
        {
            Add(form, "o:tag", tag);
        }

        if (message.Metadata is not null)
        {
            foreach (var (name, value) in message.Metadata)
            {
                Add(form, "v:" + name, value);
            }
        }

        foreach (var attachment in message.Attachments)
        {
            var file = new ReadOnlyMemoryContent(attachment.Content);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(attachment.ContentType);
            if (attachment.ContentId is { } contentId)
            {
                form.Add(file, "inline", contentId); // Mailgun references inline images as cid:<file name>
            }
            else
            {
                form.Add(file, "attachment", attachment.FileName);
            }
        }

        return form;
    }

    private static void Add(MultipartFormDataContent form, string name, string value) =>
        form.Add(new StringContent(value, Encoding.UTF8), name);
}
