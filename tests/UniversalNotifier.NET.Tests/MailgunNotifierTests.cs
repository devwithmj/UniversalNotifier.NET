using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET.Email.Mailgun;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class MailgunNotifierTests
{
    private const string Accepted = """{"id":"<20260101.abc@mg.example.com>","message":"Queued. Thank you."}""";

    private static MailgunOptions Options(MailgunRegion region = MailgunRegion.Us) => new()
    {
        ApiKey = "key-secret",
        Domain = "mg.example.com",
        From = new EmailAddress("noreply@mg.example.com", "Example"),
        Region = region,
    };

    private static MailgunNotifier Create(StubHttpHandler handler, MailgunOptions? options = null) =>
        new(options ?? Options(), new HttpClient(handler), new NotifierContext());

    private static EmailMessage Email(params string[] to) => new()
    {
        To = [.. to.Select(t => new EmailAddress(t))],
        Subject = "Hello",
        TextBody = "Plain",
        HtmlBody = "<p>Rich</p>",
    };

    [Test]
    public async Task Sends_a_message_to_the_domain_endpoint_with_basic_auth()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Accepted);

        var response = await Create(handler).SendAsync(Email("ada@example.com"));

        Assert.That(response.Success, Is.True);
        Assert.That(response.ProviderMessageId, Is.EqualTo("<20260101.abc@mg.example.com>"));
        Assert.That(response.HttpStatusCode, Is.EqualTo(200));

        var request = handler.OnlyRequest;
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(request.Uri.ToString(), Is.EqualTo("https://api.mailgun.net/v3/mg.example.com/messages"));
        Assert.That(request.Headers["Authorization"], Is.EqualTo("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("api:key-secret"))));
        Assert.That(request.Body, Does.Contain("name=from").And.Contain("\"Example\" <noreply@mg.example.com>"));
        Assert.That(request.Body, Does.Contain("name=to").And.Contain("ada@example.com"));
        Assert.That(request.Body, Does.Contain("name=subject").And.Contain("Hello"));
        Assert.That(request.Body, Does.Contain("name=text").And.Contain("Plain"));
        Assert.That(request.Body, Does.Contain("name=html").And.Contain("<p>Rich</p>"));
        Assert.That(request.Body, Does.Not.Contain("recipient-variables"));
    }

    [Test]
    public async Task The_eu_region_uses_the_eu_endpoint()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Accepted);

        await Create(handler, Options(MailgunRegion.Eu)).SendAsync(Email("ada@example.com"));

        Assert.That(handler.OnlyRequest.Uri.Host, Is.EqualTo("api.eu.mailgun.net"));
    }

    [Test]
    public async Task Several_recipients_are_sent_as_a_batch_so_they_cannot_see_each_other()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Accepted);

        var response = await Create(handler).SendAsync(Email("a@example.com", "b@example.com"));

        Assert.That(response.Success, Is.True);
        var body = handler.OnlyRequest.Body;
        Assert.That(body, Does.Contain("recipient-variables"));
        Assert.That(body, Does.Contain("\"a@example.com\":{\"id\":1}").And.Contain("\"b@example.com\":{\"id\":2}"));
    }

    [Test]
    public async Task More_than_1000_recipients_are_split_into_several_requests_with_per_recipient_results()
    {
        var calls = 0;
        var handler = new StubHttpHandler(_ => Interlocked.Increment(ref calls) == 2
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"message":"bad"}""") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Accepted) });
        var recipients = Enumerable.Range(0, 1500).Select(i => $"user{i}@example.com").ToArray();

        var response = await Create(handler).SendAsync(Email(recipients));

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        Assert.That(response.Recipients.Count(r => r.Accepted), Is.EqualTo(1000));
        Assert.That(response.Recipients.Count(r => !r.Accepted), Is.EqualTo(500));
    }

    [Test]
    public async Task Cc_bcc_reply_to_headers_tags_and_metadata_are_forwarded()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Accepted);
        var message = Email("a@example.com") with
        {
            Cc = ["c@example.com"],
            Bcc = ["d@example.com"],
            ReplyTo = new EmailAddress("support@example.com", "Support"),
            Headers = new Dictionary<string, string> { ["X-Campaign"] = "spring" },
            Tags = ["welcome", "v2"],
            Metadata = new Dictionary<string, string> { ["order"] = "42" },
        };

        await Create(handler).SendAsync(message);

        var body = handler.OnlyRequest.Body;
        Assert.That(body, Does.Contain("name=cc").And.Contain("c@example.com"));
        Assert.That(body, Does.Contain("name=bcc").And.Contain("d@example.com"));
        Assert.That(body, Does.Contain("h:Reply-To").And.Contain("\"Support\" <support@example.com>"));
        Assert.That(body, Does.Contain("h:X-Campaign").And.Contain("spring"));
        Assert.That(body, Does.Contain("o:tag").And.Contain("welcome").And.Contain("v2"));
        Assert.That(body, Does.Contain("v:order").And.Contain("42"));
    }

    [Test]
    public async Task Attachments_and_inline_images_are_sent_as_files()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Accepted);
        var message = Email("a@example.com") with
        {
            Attachments =
            [
                new EmailAttachment("report.pdf", Encoding.ASCII.GetBytes("PDFDATA"), "application/pdf"),
                new EmailAttachment("logo.png", Encoding.ASCII.GetBytes("PNGDATA"), "image/png", ContentId: "logo"),
            ],
        };

        await Create(handler).SendAsync(message);

        var body = handler.OnlyRequest.Body;
        Assert.That(body, Does.Contain("name=attachment").And.Contain("filename=report.pdf").And.Contain("PDFDATA"));
        Assert.That(body, Does.Contain("name=inline").And.Contain("filename=logo").And.Contain("PNGDATA"));
    }

    [Test]
    public async Task A_message_can_override_the_sender()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Accepted);

        await Create(handler).SendAsync(Email("a@example.com") with { From = "alerts@mg.example.com" });

        Assert.That(handler.OnlyRequest.Body, Does.Contain("alerts@mg.example.com").And.Not.Contain("noreply@"));
    }

    [Test]
    public async Task A_rejected_request_is_a_failed_response_with_the_providers_message()
    {
        var handler = new StubHttpHandler(HttpStatusCode.BadRequest, """{"message":"'to' parameter is not a valid address."}""");

        var response = await Create(handler).SendAsync(Email("ada@example.com"));

        Assert.That(response.Success, Is.False);
        Assert.That(response.HttpStatusCode, Is.EqualTo(400));
        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Permanent));
        Assert.That(response.Errors[0].Message, Does.Contain("not a valid address"));
    }

    [TestCase(HttpStatusCode.Unauthorized, NotifierErrorKind.Configuration, "mailgun.unauthorized")]
    [TestCase(HttpStatusCode.Forbidden, NotifierErrorKind.Configuration, "mailgun.unauthorized")]
    [TestCase(HttpStatusCode.TooManyRequests, NotifierErrorKind.Transient, "mailgun.rate_limited")]
    [TestCase(HttpStatusCode.ServiceUnavailable, NotifierErrorKind.Transient, "mailgun.rejected")]
    [TestCase(HttpStatusCode.InternalServerError, NotifierErrorKind.Unknown, "mailgun.rejected")]
    [TestCase(HttpStatusCode.NotFound, NotifierErrorKind.Permanent, "mailgun.rejected")]
    public async Task Http_errors_are_classified_so_callers_know_whether_to_retry(HttpStatusCode status, NotifierErrorKind kind, string code)
    {
        var handler = new StubHttpHandler(status, "Forbidden");

        var response = await Create(handler).SendAsync(Email("ada@example.com"));

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors[0].Kind, Is.EqualTo(kind));
        Assert.That(response.Errors[0].Code, Is.EqualTo(code));
    }

    [Test]
    public async Task An_empty_or_non_json_error_body_does_not_throw()
    {
        foreach (var body in new[] { "", "<html>gateway</html>", "[1,2]", "null" })
        {
            var response = await Create(new StubHttpHandler(HttpStatusCode.BadGateway, body)).SendAsync(Email("a@example.com"));

            Assert.That(response.Success, Is.False, body);
            Assert.That(response.HttpStatusCode, Is.EqualTo(502));
        }
    }

    [Test]
    public async Task A_success_with_an_unexpected_body_is_still_accepted()
    {
        var response = await Create(new StubHttpHandler(HttpStatusCode.OK, "")).SendAsync(Email("a@example.com"));

        Assert.That(response.Success, Is.True);
        Assert.That(response.ProviderMessageId, Is.Null);
    }

    [Test]
    public async Task The_retry_after_header_is_reported()
    {
        var handler = new StubHttpHandler(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7)));

        var response = await Create(handler).SendAsync(Email("a@example.com"));

        Assert.That(response.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(7)));
    }

    [Test]
    public async Task A_network_failure_is_a_failed_response_not_an_exception()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "dns"));

        var response = await Create(handler).SendAsync(Email("a@example.com"));

        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Transient));
    }

    [Test]
    public async Task More_than_three_tags_are_rejected_before_calling_mailgun()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Accepted);

        var response = await Create(handler).SendAsync(Email("a@example.com") with { Tags = ["1", "2", "3", "4"] });

        Assert.That(response.Errors[0].Code, Is.EqualTo("mailgun.tags.limit"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task Invalid_messages_never_reach_the_network()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Accepted);

        var response = await Create(handler).SendAsync(new EmailMessage());

        Assert.That(response.Success, Is.False);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task Transient_failures_are_retried_when_retries_are_enabled()
    {
        var calls = 0;
        var handler = new StubHttpHandler(_ => Interlocked.Increment(ref calls) < 3
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Accepted) });
        var settings = new NotifierOptions();
        settings.Retry.MaxAttempts = 3;
        settings.Retry.BaseDelay = TimeSpan.FromMilliseconds(1);
        var notifier = new MailgunNotifier(Options(), new HttpClient(handler), new NotifierContext(settings));

        var response = await notifier.SendAsync(Email("a@example.com"));

        Assert.That(response.Success, Is.True);
        Assert.That(calls, Is.EqualTo(3));
    }

    [Test]
    public void Invalid_options_are_rejected()
    {
        var bad = new[]
        {
            new MailgunOptions { Domain = "d.com", From = "a@d.com" },
            new MailgunOptions { ApiKey = "k", From = "a@d.com" },
            new MailgunOptions { ApiKey = "k", Domain = "d.com/evil", From = "a@d.com" },
            new MailgunOptions { ApiKey = "k", Domain = "d.com" },
        };

        foreach (var options in bad)
        {
            Assert.Throws<ArgumentException>(() => new MailgunNotifier(options, new HttpClient(), new NotifierContext()));
        }
    }

    [Test]
    public void Options_never_print_the_api_key() =>
        Assert.That(Options().ToString(), Does.Not.Contain("key-secret"));

    [Test]
    public async Task It_registers_as_the_default_email_provider_through_dependency_injection()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddMailgun(o =>
            {
                o.ApiKey = "k";
                o.Domain = "mg.example.com";
                o.From = "a@mg.example.com";
            }))
            .BuildServiceProvider();

        Assert.That(provider.GetRequiredService<IEmailNotifier>(), Is.InstanceOf<MailgunNotifier>());
        Assert.That(provider.GetRequiredKeyedService<IEmailNotifier>("mailgun"), Is.SameAs(provider.GetRequiredService<IEmailNotifier>()));
        await Task.CompletedTask;
    }

    [Test]
    public void Invalid_settings_are_reported_by_the_options_system()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddMailgun(o => o.ApiKey = "k"))
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IEmailNotifier>());
    }

    [Test]
    public void A_second_named_account_does_not_replace_the_default()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n
                .AddMailgun(o => { o.ApiKey = "k1"; o.Domain = "one.example.com"; o.From = "a@one.example.com"; })
                .AddMailgun(o => { o.ApiKey = "k2"; o.Domain = "two.example.com"; o.From = "a@two.example.com"; }, name: "marketing"))
            .BuildServiceProvider();

        Assert.That(provider.GetRequiredService<IEmailNotifier>(), Is.SameAs(provider.GetRequiredKeyedService<IEmailNotifier>("mailgun")));
        Assert.That(provider.GetRequiredKeyedService<IEmailNotifier>("marketing"), Is.Not.SameAs(provider.GetRequiredService<IEmailNotifier>()));
    }

    [Test]
    public async Task It_works_without_dependency_injection()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Accepted);
        using var notifier = new NotifierBuilder()
            .AddMailgun(o => { o.ApiKey = "k"; o.Domain = "mg.example.com"; o.From = "a@mg.example.com"; })
            .Build();

        Assert.That(notifier.Email, Is.InstanceOf<MailgunNotifier>());
        await Task.CompletedTask;
        _ = handler;
    }
}
