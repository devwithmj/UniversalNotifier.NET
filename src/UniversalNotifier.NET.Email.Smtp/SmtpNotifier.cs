using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace UniversalNotifier.NET.Email.Smtp;

/// <summary>
/// Sends email over SMTP with MailKit. With several <see cref="EmailMessage.To"/> recipients every recipient gets an
/// individual message over one connection and is reported separately, so one bad address does not block the rest and
/// recipients never see each other. Each call opens its own connection; pool or batch at a higher level if you send at very high volume.
/// </summary>
public sealed class SmtpNotifier : NotifierBase<EmailMessage>, IEmailNotifier
{
    /// <summary>The provider name used for explicit selection.</summary>
    public const string Name = "smtp";

    private readonly SmtpOptions _options;
    private readonly SecureSocketOptions _security;
    private readonly MailboxAddress _defaultFrom;

    /// <summary>Creates the notifier. With dependency injection use <c>AddSmtp</c> instead.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public SmtpNotifier(SmtpOptions options, NotifierContext context)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (SmtpOptions.Validate(options) is { } error)
        {
            throw new ArgumentException(error, nameof(options));
        }

        _options = options;
        _defaultFrom = ToMailbox(options.From);
        _security = options.Security switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
    }

    /// <inheritdoc />
    protected override string ProviderName => Name;

    /// <inheritdoc />
    protected override async Task<NotifierResponse> SendCoreAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient { Timeout = (int)_options.Timeout.TotalMilliseconds };

        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, _security, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not SslHandshakeException)
        {
            // Nothing was sent yet, so repeating is safe.
            return NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Transient, "smtp.unreachable", "Could not connect to the SMTP server."));
        }

        try
        {
            if (!string.IsNullOrEmpty(_options.UserName))
            {
                await client.AuthenticateAsync(_options.UserName, _options.Password!, cancellationToken).ConfigureAwait(false);
            }

            return message.To.Count <= 1
                ? await SendOneAsync(client, message, cancellationToken).ConfigureAwait(false)
                : await SendIndividuallyAsync(client, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DisconnectQuietlyAsync(client).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override NotifierError Classify(Exception exception) => exception switch
    {
        MailKit.Security.AuthenticationException =>
            new NotifierError(NotifierErrorKind.Configuration, "smtp.auth_failed", "The SMTP server rejected the credentials."),
        SslHandshakeException =>
            new NotifierError(NotifierErrorKind.Configuration, "smtp.tls_failed", "The TLS handshake with the SMTP server failed."),
        SmtpCommandException command => ClassifyCommand(command),
        FormatException =>
            new NotifierError(NotifierErrorKind.Validation, "smtp.message.invalid", "The message could not be converted to an email (check content types and addresses)."),
        ProtocolException =>
            new NotifierError(NotifierErrorKind.Unknown, "smtp.protocol", "The SMTP conversation failed unexpectedly."),
        _ => base.Classify(exception),
    };

    // 4xx replies mean "not accepted, try later" and are safe to repeat; 5xx replies are final. Server text is not
    // included because servers often echo the recipient address.
    private static NotifierError ClassifyCommand(SmtpCommandException exception)
    {
        var code = (int)exception.StatusCode;
        var kind = code is >= 400 and < 500 ? NotifierErrorKind.Transient : NotifierErrorKind.Permanent;
        var what = exception.ErrorCode switch
        {
            SmtpErrorCode.RecipientNotAccepted => "recipient",
            SmtpErrorCode.SenderNotAccepted => "sender",
            _ => "message",
        };

        return new NotifierError(kind, $"smtp.{what}_rejected", $"The SMTP server rejected the {what} (reply {code}).");
    }

    private async Task<NotifierResponse> SendOneAsync(SmtpClient client, EmailMessage message, CancellationToken cancellationToken)
    {
        var reply = await client.SendAsync(Build(message, message.To), cancellationToken).ConfigureAwait(false);
        return NotifierResponse.Accepted(reply.ResponseText);
    }

    private async Task<NotifierResponse> SendIndividuallyAsync(SmtpClient client, EmailMessage message, CancellationToken cancellationToken)
    {
        var results = new List<RecipientResult>(message.To.Count);
        foreach (var recipient in message.To)
        {
            if (!client.IsConnected)
            {
                // The connection dropped on an earlier recipient; this one was never attempted, so retrying it is safe.
                results.Add(new RecipientResult(recipient.Address, false, Error: new NotifierError(NotifierErrorKind.Transient, "smtp.connection_lost", "The SMTP connection was lost before this recipient.")));
                continue;
            }

            try
            {
                var reply = await client.SendAsync(Build(message, [recipient]), cancellationToken).ConfigureAwait(false);
                results.Add(new RecipientResult(recipient.Address, true, reply.ResponseText));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                results.Add(new RecipientResult(recipient.Address, false, Error: Classify(exception)));
            }
        }

        return NotifierResponse.FromRecipients(results);
    }

    private MimeMessage Build(EmailMessage message, IReadOnlyList<EmailAddress> to)
    {
        var mime = new MimeMessage();
        mime.From.Add(message.From is { } from ? ToMailbox(from) : _defaultFrom);

        foreach (var recipient in to)
        {
            mime.To.Add(ToMailbox(recipient));
        }

        foreach (var recipient in message.Cc)
        {
            mime.Cc.Add(ToMailbox(recipient));
        }

        foreach (var recipient in message.Bcc)
        {
            mime.Bcc.Add(ToMailbox(recipient));
        }

        if (message.ReplyTo is { } replyTo)
        {
            mime.ReplyTo.Add(ToMailbox(replyTo));
        }

        mime.Subject = message.Subject;

        if (message.Headers is not null)
        {
            foreach (var (name, value) in message.Headers)
            {
                mime.Headers.Add(name, value);
            }
        }

        var body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        foreach (var attachment in message.Attachments)
        {
            var type = ContentType.Parse(attachment.ContentType);
            if (attachment.ContentId is { } contentId)
            {
                body.LinkedResources.Add(attachment.FileName, attachment.Content.ToArray(), type).ContentId = contentId;
            }
            else
            {
                body.Attachments.Add(attachment.FileName, attachment.Content.ToArray(), type);
            }
        }

        mime.Body = body.ToMessageBody();
        return mime;
    }

    private static MailboxAddress ToMailbox(EmailAddress address) =>
        new(string.IsNullOrWhiteSpace(address.Name) ? null : address.Name, address.Address);

    private static async Task DisconnectQuietlyAsync(SmtpClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(quit: true).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The message outcome is already decided; a failed QUIT must not change it.
        }
    }
}
