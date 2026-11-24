using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UniversalNotifier.NET.Email.Ses;
using UniversalNotifier.NET.Testing;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class SesNotifierTests
{
    private const string Ok = """{"MessageId":"0100-abc"}""";

    private static SesOptions Options(Action<SesOptions>? configure = null)
    {
        var options = new SesOptions
        {
            Region = "eu-west-1",
            AccessKeyId = "AKIDEXAMPLE",
            SecretAccessKey = "secret-KEY",
            From = new EmailAddress("noreply@example.com", "Acme"),
        };
        configure?.Invoke(options);
        return options;
    }

    private static SesNotifier Create(StubHttpHandler handler, Action<SesOptions>? configure = null) =>
        new(Options(configure), new HttpClient(handler), new NotifierContext());

    [Test]
    public void Signing_matches_the_published_aws_example()
    {
        // The worked example from the AWS Signature Version 4 documentation (GET, iam, us-east-1).
        var signed = AwsSigV4.Sign(
            "GET", "iam.amazonaws.com", "/", "Action=ListUsers&Version=2010-05-08", [],
            "application/x-www-form-urlencoded; charset=utf-8", "us-east-1", "iam",
            "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", null, new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero));

        Assert.That(signed.AmzDate, Is.EqualTo("20150830T123600Z"));
        Assert.That(signed.Authorization, Is.EqualTo(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/iam/aws4_request, SignedHeaders=content-type;host;x-amz-date, " +
            "Signature=5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7"));
    }

    [Test]
    public async Task Sends_a_signed_request_to_the_regional_endpoint_with_the_message_as_simple_content()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Ok);
        var message = EmailMessage.Create("ada@example.com", "Hello", textBody: "plain", htmlBody: "<b>rich</b>") with { ReplyTo = "help@example.com" };

        var response = await Create(handler).SendAsync(message);

        Assert.That(response.Success, Is.True);
        Assert.That(response.ProviderMessageId, Is.EqualTo("0100-abc"));
        var request = handler.OnlyRequest;
        Assert.That(request.Uri.ToString(), Is.EqualTo("https://email.eu-west-1.amazonaws.com/v2/email/outbound-emails"));
        Assert.That(request.Headers["Authorization"], Does.StartWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/").And.Contain("/eu-west-1/ses/aws4_request"));
        Assert.That(request.Headers, Does.ContainKey("X-Amz-Date"));
        Assert.That(request.Headers, Does.Not.ContainKey("X-Amz-Security-Token"));

        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.That(root.GetProperty("FromEmailAddress").GetString(), Is.EqualTo("\"Acme\" <noreply@example.com>"));
        Assert.That(root.GetProperty("Destination").GetProperty("ToAddresses")[0].GetString(), Is.EqualTo("ada@example.com"));
        Assert.That(root.GetProperty("ReplyToAddresses")[0].GetString(), Is.EqualTo("help@example.com"));
        var simple = root.GetProperty("Content").GetProperty("Simple");
        Assert.That(simple.GetProperty("Subject").GetProperty("Data").GetString(), Is.EqualTo("Hello"));
        Assert.That(simple.GetProperty("Body").GetProperty("Text").GetProperty("Data").GetString(), Is.EqualTo("plain"));
        Assert.That(simple.GetProperty("Body").GetProperty("Html").GetProperty("Data").GetString(), Is.EqualTo("<b>rich</b>"));
        Assert.That(root.TryGetProperty("ConfigurationSetName", out _), Is.False, "unset options are not sent");
    }

    [Test]
    public async Task A_session_token_is_sent_and_signed_and_a_configuration_set_is_applied()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Ok);

        await Create(handler, o => { o.SessionToken = "tok"; o.ConfigurationSetName = "tracking"; }).SendAsync(EmailMessage.Create("ada@example.com", "Hi", textBody: "x"));

        var request = handler.OnlyRequest;
        Assert.That(request.Headers["X-Amz-Security-Token"], Is.EqualTo("tok"));
        Assert.That(request.Headers["Authorization"], Does.Contain("SignedHeaders=content-type;host;x-amz-date;x-amz-security-token"));
        Assert.That(request.Body, Does.Contain("\"ConfigurationSetName\":\"tracking\""));
    }

    [Test]
    public async Task Attachments_and_inline_images_are_base64_with_the_right_disposition()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Ok);
        var message = EmailMessage.Create("ada@example.com", "Hi", htmlBody: "<img src=\"cid:logo\">") with
        {
            Attachments = [new EmailAttachment("a.txt", "hi"u8.ToArray(), "text/plain"), new EmailAttachment("logo.png", new byte[] { 1, 2 }, "image/png", "logo")],
        };

        await Create(handler).SendAsync(message);

        using var json = JsonDocument.Parse(handler.OnlyRequest.Body);
        var attachments = json.RootElement.GetProperty("Content").GetProperty("Simple").GetProperty("Attachments");
        Assert.That(attachments[0].GetProperty("RawContent").GetString(), Is.EqualTo(Convert.ToBase64String("hi"u8)));
        Assert.That(attachments[0].GetProperty("ContentDisposition").GetString(), Is.EqualTo("ATTACHMENT"));
        Assert.That(attachments[1].GetProperty("ContentDisposition").GetString(), Is.EqualTo("INLINE"));
        Assert.That(attachments[1].GetProperty("ContentId").GetString(), Is.EqualTo("logo"));
    }

    [Test]
    public async Task Each_recipient_gets_an_individual_request_and_their_own_result()
    {
        var handler = new StubHttpHandler(r => new HttpResponseMessage(r.Body.Contains("bad@example.com", StringComparison.Ordinal) ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
        {
            Content = new StringContent(Ok, Encoding.UTF8, "application/json"),
        });
        var message = new EmailMessage { To = ["a@example.com", "bad@example.com", "c@example.com"], Subject = "Hi", TextBody = "x" };

        var response = await Create(handler).SendAsync(message);

        Assert.That(handler.Requests, Has.Count.EqualTo(3));
        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        Assert.That(response.Recipients.Single(r => !r.Accepted).Recipient, Is.EqualTo("bad@example.com"));
        var sizes = handler.Requests.Select(request =>
        {
            using var json = JsonDocument.Parse(request.Body);
            return json.RootElement.GetProperty("Destination").GetProperty("ToAddresses").GetArrayLength();
        });
        Assert.That(sizes, Has.All.EqualTo(1), "recipients must not see each other");
    }

    [Test]
    public async Task The_error_type_decides_the_kind_and_the_message_text_is_never_surfaced()
    {
        var handler = new StubHttpHandler(r => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"message":"Email address is not verified: ada@example.com"}""", Encoding.UTF8, "application/json"),
            Headers = { { "x-amzn-ErrorType", "MessageRejected:http://internal.amazon.com/coral/" } },
        });

        var rejected = await Create(handler).SendAsync(EmailMessage.Create("ada@example.com", "Hi", textBody: "x"));

        Assert.That(rejected.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Permanent));
        Assert.That(rejected.Errors[0].Message, Does.Contain("MessageRejected").And.Not.Contain("ada@example.com"));

        var paused = await Create(new StubHttpHandler(r => new HttpResponseMessage(HttpStatusCode.BadRequest) { Headers = { { "x-amzn-ErrorType", "SendingPausedException" } }, Content = new StringContent("{}") }))
            .SendAsync(EmailMessage.Create("ada@example.com", "Hi", textBody: "x"));
        Assert.That(paused.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Configuration));

        var throttled = await Create(new StubHttpHandler(HttpStatusCode.TooManyRequests, "{}")).SendAsync(EmailMessage.Create("ada@example.com", "Hi", textBody: "x"));
        Assert.That(throttled.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Transient));
    }

    [Test]
    public void Options_are_validated_and_the_ses_provider_registers_as_the_email_default()
    {
        Assert.Throws<ArgumentException>(() => Create(new StubHttpHandler(HttpStatusCode.OK), o => o.Region = "not a region"));
        Assert.Throws<ArgumentException>(() => Create(new StubHttpHandler(HttpStatusCode.OK), o => o.SecretAccessKey = ""));
        Assert.That(Options().ToString(), Does.Not.Contain("secret-KEY"));

        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddSes(o => { o.Region = "us-east-1"; o.AccessKeyId = "AK"; o.SecretAccessKey = "s"; o.From = "a@example.com"; }))
            .BuildServiceProvider();
        Assert.That(provider.GetRequiredService<IEmailNotifier>(), Is.InstanceOf<SesNotifier>());
        Assert.That(provider.GetRequiredKeyedService<IEmailNotifier>("ses"), Is.SameAs(provider.GetRequiredService<IEmailNotifier>()));
    }
}

[TestFixture]
public class SesContractTests : ProviderContractTests<EmailMessage>
{
    private StubHttpHandler _handler = new(HttpStatusCode.OK);

    protected override EmailMessage Valid => EmailMessage.Create("ada@example.com", "Hello", textBody: "Body");

    protected override EmailMessage Invalid => new();

    protected override IReadOnlyList<string> Secrets => ["secret-KEY"];

    protected override IReadOnlyList<string> PersonalData => ["ada@example.com"];

    protected override int BackendCalls => _handler.Requests.Count;

    protected override INotifier<EmailMessage> Create(ContractScenario scenario)
    {
        _handler = scenario switch
        {
            ContractScenario.Accepts => new StubHttpHandler(HttpStatusCode.OK, """{"MessageId":"m1"}"""),
            ContractScenario.RejectsPermanently => new StubHttpHandler(HttpStatusCode.BadRequest, """{"message":"bad ada@example.com"}"""),
            ContractScenario.RejectsTransiently => new StubHttpHandler(HttpStatusCode.ServiceUnavailable, "{}"),
            _ => new StubHttpHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "down")),
        };

        return new SesNotifier(
            new SesOptions { Region = "eu-west-1", AccessKeyId = "AKIDEXAMPLE", SecretAccessKey = "secret-KEY", From = "noreply@example.com" },
            new HttpClient(_handler),
            Context);
    }
}
