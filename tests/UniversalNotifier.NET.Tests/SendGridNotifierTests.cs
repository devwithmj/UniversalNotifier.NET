using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET.Email.SendGrid;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class SendGridNotifierTests
{
    private static SendGridNotifier Create(StubHttpHandler handler, SendGridRegion region = SendGridRegion.Us) =>
        new(new SendGridOptions { ApiKey = "SG.SECRET-key", From = new EmailAddress("noreply@example.com", "Example"), Region = region },
            new HttpClient(handler), new NotifierContext());

    private static HttpResponseMessage Accepted() =>
        new(HttpStatusCode.Accepted) { Headers = { { "X-Message-Id", "msg-123" } }, Content = new StringContent(string.Empty) };

    private static EmailMessage Email(params string[] to) => new()
    {
        To = [.. to.Select(t => new EmailAddress(t))],
        Subject = "Hello",
        TextBody = "Plain",
        HtmlBody = "<p>Rich</p>",
    };

    [Test]
    public async Task Posts_json_with_bearer_auth_and_reports_the_message_id()
    {
        var handler = new StubHttpHandler(_ => Accepted());

        var response = await Create(handler).SendAsync(Email("ada@example.com"));

        Assert.That(response.Success, Is.True);
        Assert.That(response.ProviderMessageId, Is.EqualTo("msg-123"));
        Assert.That(response.HttpStatusCode, Is.EqualTo(202));
        var request = handler.OnlyRequest;
        Assert.That(request.Uri.ToString(), Is.EqualTo("https://api.sendgrid.com/v3/mail/send"));
        Assert.That(request.Headers["Authorization"], Is.EqualTo("Bearer SG.SECRET-key"));

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.That(root.GetProperty("from").GetProperty("email").GetString(), Is.EqualTo("noreply@example.com"));
        Assert.That(root.GetProperty("from").GetProperty("name").GetString(), Is.EqualTo("Example"));
        Assert.That(root.GetProperty("subject").GetString(), Is.EqualTo("Hello"));
        var content = root.GetProperty("content");
        Assert.That(content[0].GetProperty("type").GetString(), Is.EqualTo("text/plain"), "SendGrid needs plain text first");
        Assert.That(content[1].GetProperty("type").GetString(), Is.EqualTo("text/html"));
        Assert.That(root.GetProperty("personalizations")[0].GetProperty("to")[0].GetProperty("email").GetString(), Is.EqualTo("ada@example.com"));
        Assert.That(root.TryGetProperty("attachments", out _), Is.False);
    }

    [Test]
    public async Task The_eu_region_uses_the_eu_endpoint()
    {
        var handler = new StubHttpHandler(_ => Accepted());

        await Create(handler, SendGridRegion.Eu).SendAsync(Email("ada@example.com"));

        Assert.That(handler.OnlyRequest.Uri.Host, Is.EqualTo("api.eu.sendgrid.com"));
    }

    [Test]
    public async Task Each_recipient_gets_its_own_personalization_so_nobody_sees_the_others()
    {
        var handler = new StubHttpHandler(_ => Accepted());

        await Create(handler).SendAsync(Email("a@example.com", "b@example.com", "A@example.com"));

        using var body = JsonDocument.Parse(handler.OnlyRequest.Body);
        var personalizations = body.RootElement.GetProperty("personalizations");
        Assert.That(personalizations.GetArrayLength(), Is.EqualTo(2), "the repeated address is sent once");
        Assert.That(personalizations[0].GetProperty("to").GetArrayLength(), Is.EqualTo(1));
        Assert.That(personalizations[1].GetProperty("to").GetArrayLength(), Is.EqualTo(1));
        Assert.That(personalizations[0].TryGetProperty("cc", out _), Is.False);
    }

    [Test]
    public async Task More_than_1000_recipients_are_split_into_requests_with_per_recipient_results()
    {
        var calls = 0;
        var handler = new StubHttpHandler(_ => Interlocked.Increment(ref calls) == 2
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"errors":[{"message":"x","field":"from.email"}]}""") }
            : Accepted());

        var response = await Create(handler).SendAsync(Email([.. Enumerable.Range(0, 1200).Select(i => $"user{i}@example.com")]));

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        Assert.That(response.Recipients.Count(r => r.Accepted), Is.EqualTo(1000));
        Assert.That(response.Recipients.Count(r => !r.Accepted), Is.EqualTo(200));
    }

    [Test]
    public async Task Cc_bcc_reply_to_headers_categories_and_metadata_are_forwarded()
    {
        var handler = new StubHttpHandler(_ => Accepted());
        var message = Email("a@example.com") with
        {
            Cc = ["c@example.com"],
            Bcc = ["d@example.com"],
            ReplyTo = new EmailAddress("support@example.com", "Support"),
            Headers = new Dictionary<string, string> { ["X-Campaign"] = "spring" },
            Tags = ["welcome", "v2"],
            Metadata = new Dictionary<string, string> { ["order"] = "42" },
            From = "alerts@example.com",
        };

        await Create(handler).SendAsync(message);

        using var body = JsonDocument.Parse(handler.OnlyRequest.Body);
        var root = body.RootElement;
        var personalization = root.GetProperty("personalizations")[0];
        Assert.That(personalization.GetProperty("cc")[0].GetProperty("email").GetString(), Is.EqualTo("c@example.com"));
        Assert.That(personalization.GetProperty("bcc")[0].GetProperty("email").GetString(), Is.EqualTo("d@example.com"));
        Assert.That(root.GetProperty("reply_to").GetProperty("name").GetString(), Is.EqualTo("Support"));
        Assert.That(root.GetProperty("from").GetProperty("email").GetString(), Is.EqualTo("alerts@example.com"));
        Assert.That(root.GetProperty("headers").GetProperty("X-Campaign").GetString(), Is.EqualTo("spring"));
        Assert.That(root.GetProperty("categories").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "welcome", "v2" }));
        Assert.That(root.GetProperty("custom_args").GetProperty("order").GetString(), Is.EqualTo("42"));
    }

    [Test]
    public async Task Attachments_are_base64_and_inline_images_carry_their_content_id()
    {
        var handler = new StubHttpHandler(_ => Accepted());
        var message = Email("a@example.com") with
        {
            Attachments =
            [
                new EmailAttachment("report.pdf", new byte[] { 1, 2, 3 }, "application/pdf"),
                new EmailAttachment("logo.png", new byte[] { 9 }, "image/png", ContentId: "logo"),
            ],
        };

        await Create(handler).SendAsync(message);

        using var body = JsonDocument.Parse(handler.OnlyRequest.Body);
        var attachments = body.RootElement.GetProperty("attachments");
        Assert.That(attachments[0].GetProperty("content").GetString(), Is.EqualTo(Convert.ToBase64String(new byte[] { 1, 2, 3 })));
        Assert.That(attachments[0].GetProperty("disposition").GetString(), Is.EqualTo("attachment"));
        Assert.That(attachments[0].GetProperty("filename").GetString(), Is.EqualTo("report.pdf"));
        Assert.That(attachments[1].GetProperty("disposition").GetString(), Is.EqualTo("inline"));
        Assert.That(attachments[1].GetProperty("content_id").GetString(), Is.EqualTo("logo"));
    }

    [TestCase(HttpStatusCode.Unauthorized, NotifierErrorKind.Configuration, "sendgrid.unauthorized")]
    [TestCase(HttpStatusCode.Forbidden, NotifierErrorKind.Configuration, "sendgrid.unauthorized")]
    [TestCase(HttpStatusCode.TooManyRequests, NotifierErrorKind.Transient, "sendgrid.rate_limited")]
    [TestCase(HttpStatusCode.ServiceUnavailable, NotifierErrorKind.Transient, "sendgrid.rejected")]
    [TestCase(HttpStatusCode.RequestEntityTooLarge, NotifierErrorKind.Permanent, "sendgrid.rejected")]
    [TestCase(HttpStatusCode.InternalServerError, NotifierErrorKind.Unknown, "sendgrid.rejected")]
    public async Task Http_errors_are_classified(HttpStatusCode status, NotifierErrorKind kind, string code)
    {
        var response = await Create(new StubHttpHandler(status, "{}")).SendAsync(Email("a@example.com"));

        Assert.That(response.Errors[0].Kind, Is.EqualTo(kind));
        Assert.That(response.Errors[0].Code, Is.EqualTo(code));
    }

    [Test]
    public async Task A_bad_request_names_the_offending_fields_but_never_echoes_addresses()
    {
        var body = """{"errors":[{"message":"The to email address ada@example.com is invalid","field":"personalizations.0.to.0.email"},{"message":"x","field":null}]}""";

        var response = await Create(new StubHttpHandler(HttpStatusCode.BadRequest, body)).SendAsync(Email("ada@example.com"));

        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Permanent));
        Assert.That(response.Errors[0].Message, Does.Contain("personalizations.0.to.0.email").And.Not.Contain("ada@example.com"));
    }

    [Test]
    public async Task Empty_and_non_json_error_bodies_do_not_throw()
    {
        foreach (var body in new[] { "", "<html/>", "[]", "null" })
        {
            var response = await Create(new StubHttpHandler(HttpStatusCode.BadGateway, body)).SendAsync(Email("a@example.com"));

            Assert.That(response.Success, Is.False, body);
        }
    }

    [Test]
    public async Task More_than_ten_categories_are_rejected_before_sending()
    {
        var handler = new StubHttpHandler(_ => Accepted());

        var response = await Create(handler).SendAsync(Email("a@example.com") with { Tags = [.. Enumerable.Range(0, 11).Select(i => "t" + i)] });

        Assert.That(response.Errors[0].Code, Is.EqualTo("sendgrid.tags.limit"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public void Invalid_options_are_rejected_and_never_print_the_key()
    {
        Assert.Throws<ArgumentException>(() => new SendGridNotifier(new SendGridOptions { From = "a@example.com" }, new HttpClient(), new NotifierContext()));
        Assert.Throws<ArgumentException>(() => new SendGridNotifier(new SendGridOptions { ApiKey = "k" }, new HttpClient(), new NotifierContext()));
        Assert.That(new SendGridOptions { ApiKey = "SG.SECRET" }.ToString(), Does.Not.Contain("SECRET"));
    }

    [Test]
    public void It_registers_through_dependency_injection()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddSendGrid(o => { o.ApiKey = "k"; o.From = "a@example.com"; }))
            .BuildServiceProvider();

        Assert.That(provider.GetRequiredService<IEmailNotifier>(), Is.InstanceOf<SendGridNotifier>());
        Assert.That(provider.GetRequiredKeyedService<IEmailNotifier>("sendgrid"), Is.SameAs(provider.GetRequiredService<IEmailNotifier>()));
    }

    [Test]
    public void Invalid_settings_are_reported_by_the_options_system()
    {
        using var provider = new ServiceCollection().AddUniversalNotifier(n => n.AddSendGrid()).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IEmailNotifier>());
    }
}
