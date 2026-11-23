using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET.Email.Smtp;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class SmtpNotifierTests
{
    private FakeSmtpServer _server = null!;

    [SetUp]
    public void StartServer() => _server = new FakeSmtpServer();

    [TearDown]
    public void StopServer() => _server.Dispose();

    private SmtpNotifier Create(Action<SmtpOptions>? configure = null)
    {
        var options = new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = _server.Port,
            Security = SmtpSecurity.None,
            From = new EmailAddress("noreply@example.com", "Example"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        configure?.Invoke(options);
        return new SmtpNotifier(options, new NotifierContext());
    }

    private static EmailMessage Email(params string[] to) => new()
    {
        To = [.. to.Select(t => new EmailAddress(t))],
        Subject = "Hello",
        TextBody = "Plain text",
        HtmlBody = "<p>Rich</p>",
    };

    [Test]
    public async Task Delivers_a_multipart_message_to_the_server()
    {
        var response = await Create().SendAsync(Email("ada@example.com"));

        Assert.That(response.Success, Is.True, string.Join(';', response.Errors));
        Assert.That(response.ProviderMessageId, Does.Contain("queued as"));
        var mail = _server.Mails.Single();
        Assert.That(mail.From, Is.EqualTo("noreply@example.com"));
        Assert.That(mail.Recipients, Is.EqualTo(new[] { "ada@example.com" }));
        Assert.That(mail.Data, Does.Contain("Subject: Hello"));
        Assert.That(mail.Data, Does.Contain("From: Example <noreply@example.com>"));
        Assert.That(mail.Data, Does.Contain("Plain text").And.Contain("<p>Rich</p>"));
    }

    [Test]
    public async Task Cc_and_bcc_are_delivered_but_bcc_is_not_shown_in_the_headers()
    {
        var message = Email("ada@example.com") with { Cc = ["cc@example.com"], Bcc = ["hidden@example.com"] };

        await Create().SendAsync(message);

        var mail = _server.Mails.Single();
        Assert.That(mail.Recipients, Is.EquivalentTo(new[] { "ada@example.com", "cc@example.com", "hidden@example.com" }));
        Assert.That(mail.Data, Does.Contain("Cc: cc@example.com"));
        Assert.That(mail.Data, Does.Not.Contain("hidden@example.com"));
    }

    [Test]
    public async Task Reply_to_custom_headers_and_a_sender_override_are_applied()
    {
        var message = Email("ada@example.com") with
        {
            From = "alerts@example.com",
            ReplyTo = "support@example.com",
            Headers = new Dictionary<string, string> { ["X-Campaign"] = "spring" },
        };

        await Create().SendAsync(message);

        var mail = _server.Mails.Single();
        Assert.That(mail.From, Is.EqualTo("alerts@example.com"));
        Assert.That(mail.Data, Does.Contain("Reply-To: support@example.com").And.Contain("X-Campaign: spring"));
    }

    [Test]
    public async Task Attachments_and_inline_images_are_included()
    {
        var message = Email("ada@example.com") with
        {
            Attachments =
            [
                new EmailAttachment("report.pdf", Encoding.ASCII.GetBytes("PDF"), "application/pdf"),
                new EmailAttachment("logo.png", Encoding.ASCII.GetBytes("PNG"), "image/png", ContentId: "logo"),
            ],
        };

        var response = await Create().SendAsync(message);

        Assert.That(response.Success, Is.True);
        var data = _server.Mails.Single().Data;
        Assert.That(data, Does.Contain("report.pdf").And.Contain("application/pdf"));
        Assert.That(data, Does.Contain("Content-Id: <logo>").And.Contain("image/png"));
    }

    [Test]
    public async Task Several_recipients_get_individual_messages_that_never_mention_each_other()
    {
        var response = await Create().SendAsync(Email("a@example.com", "b@example.com", "c@example.com"));

        Assert.That(response.Success, Is.True);
        Assert.That(response.Recipients, Has.Count.EqualTo(3));
        Assert.That(_server.Mails, Has.Count.EqualTo(3));
        foreach (var mail in _server.Mails)
        {
            Assert.That(mail.Recipients, Has.Count.EqualTo(1));
            var others = new[] { "a@example.com", "b@example.com", "c@example.com" }.Except(mail.Recipients);
            Assert.That(others.Any(other => mail.Data.Contains(other, StringComparison.Ordinal)), Is.False);
        }
    }

    [Test]
    public async Task One_rejected_recipient_does_not_block_the_others_and_is_reported_separately()
    {
        _server.RejectRecipient = address => address.StartsWith("bad", StringComparison.Ordinal) ? "550 5.1.1 no such user" : null;

        var response = await Create().SendAsync(Email("a@example.com", "bad@example.com", "c@example.com"));

        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        Assert.That(_server.Mails.SelectMany(m => m.Recipients), Is.EquivalentTo(new[] { "a@example.com", "c@example.com" }));
        var failed = response.Recipients.Single(r => !r.Accepted);
        Assert.That(failed.Recipient, Is.EqualTo("bad@example.com"));
        Assert.That(failed.Error!.Value.Kind, Is.EqualTo(NotifierErrorKind.Permanent));
        Assert.That(failed.Error.Value.Code, Is.EqualTo("smtp.recipient_rejected"));
    }

    [Test]
    public async Task A_temporary_server_refusal_is_transient()
    {
        _server.RejectRecipient = _ => "450 4.2.0 mailbox busy, try later";

        var response = await Create().SendAsync(Email("a@example.com"));

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Transient));
        Assert.That(response.Errors[0].Message, Does.Not.Contain("a@example.com"));
    }

    [Test]
    public async Task A_permanent_refusal_of_a_single_message_is_permanent()
    {
        _server.RejectRecipient = _ => "550 5.1.1 unknown user";

        var response = await Create().SendAsync(Email("a@example.com"));

        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Permanent));
    }

    [Test]
    public async Task Correct_credentials_are_accepted()
    {
        _server.RequiredLogin = ("user", "s3cret");

        var response = await Create(o =>
        {
            o.UserName = "user";
            o.Password = "s3cret";
            o.AllowInsecureAuthentication = true;
        }).SendAsync(Email("a@example.com"));

        Assert.That(response.Success, Is.True, string.Join(';', response.Errors));
    }

    [Test]
    public async Task Wrong_credentials_are_a_configuration_error_and_never_retried()
    {
        _server.RequiredLogin = ("user", "s3cret");

        var response = await Create(o =>
        {
            o.UserName = "user";
            o.Password = "wrong";
            o.AllowInsecureAuthentication = true;
        }).SendAsync(Email("a@example.com"));

        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Configuration));
        Assert.That(response.Errors[0].Code, Is.EqualTo("smtp.auth_failed"));
        Assert.That(response.Errors[0].Message, Does.Not.Contain("wrong"));
        Assert.That(_server.Mails, Is.Empty);
    }

    [Test]
    public async Task An_unreachable_server_is_transient_because_nothing_was_sent()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var response = await Create(o => o.Port = closedPort).SendAsync(Email("a@example.com"));

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Transient));
        Assert.That(response.Errors[0].Code, Is.EqualTo("smtp.unreachable"));
    }

    [Test]
    public async Task Invalid_messages_are_rejected_before_connecting()
    {
        var response = await Create().SendAsync(new EmailMessage { To = ["bad address"], Subject = "s", TextBody = "b" });

        Assert.That(response.Success, Is.False);
        Assert.That(_server.Mails, Is.Empty);
    }

    [Test]
    public void Credentials_over_an_unencrypted_connection_are_refused_by_default()
    {
        Assert.Throws<ArgumentException>(() => Create(o =>
        {
            o.UserName = "user";
            o.Password = "pw";
        }));
    }

    [Test]
    public void Invalid_options_are_rejected()
    {
        var bad = new[]
        {
            new SmtpOptions { From = "a@example.com" },
            new SmtpOptions { Host = "h", Port = 0, From = "a@example.com" },
            new SmtpOptions { Host = "h" },
            new SmtpOptions { Host = "h", From = "a@example.com", UserName = "u" },
            new SmtpOptions { Host = "h", From = "a@example.com", Timeout = TimeSpan.Zero },
        };

        foreach (var options in bad)
        {
            Assert.Throws<ArgumentException>(() => new SmtpNotifier(options, new NotifierContext()));
        }
    }

    [Test]
    public void Options_never_print_the_password() =>
        Assert.That(new SmtpOptions { Host = "h", Password = "s3cret" }.ToString(), Does.Not.Contain("s3cret"));

    [Test]
    public async Task It_registers_through_dependency_injection_and_sends_through_the_dispatcher()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddSmtp(o =>
            {
                o.Host = "127.0.0.1";
                o.Port = _server.Port;
                o.Security = SmtpSecurity.None;
                o.From = "noreply@example.com";
            }))
            .BuildServiceProvider();

        var result = await provider.GetRequiredService<INotificationService>()
            .SendAsync(Notification.To("ada@example.com").Email("Welcome {{Name}}", text: "Hi {{Name}}").WithData(new { Name = "Ada" }));

        Assert.That(result.Success, Is.True);
        Assert.That(_server.Mails.Single().Data, Does.Contain("Subject: Welcome Ada").And.Contain("Hi Ada"));
        Assert.That(provider.GetRequiredKeyedService<IEmailNotifier>("smtp"), Is.SameAs(provider.GetRequiredService<IEmailNotifier>()));
    }

    [Test]
    public void Invalid_settings_are_reported_by_the_options_system()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddSmtp(o => o.Host = "h"))
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IEmailNotifier>());
    }
}
