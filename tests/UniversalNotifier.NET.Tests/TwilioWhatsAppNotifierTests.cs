using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using UniversalNotifier.NET.Testing;
using UniversalNotifier.NET.Twilio;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class TwilioWhatsAppNotifierTests
{
    private const string AccountSid = "AC00000000000000000000000000000000";
    private const string Created = """{"sid":"SM123","status":"queued"}""";

    private static TwilioOptions Options(Action<TwilioOptions>? configure = null)
    {
        var options = new TwilioOptions { AccountSid = AccountSid, AuthToken = "token-secret", WhatsAppFrom = "+14155238886" };
        configure?.Invoke(options);
        return options;
    }

    private static TwilioWhatsAppNotifier Create(StubHttpHandler handler, Action<TwilioOptions>? configure = null) =>
        new(Options(configure), new HttpClient(handler), new NotifierContext());

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Test]
    public async Task Sends_a_text_to_a_whatsapp_address_from_the_sandbox_style_sender()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created, Created);

        var response = await Create(handler).SendAsync(WhatsAppMessage.Create("+14165551234", "Hello & welcome"));

        Assert.That(response.Success, Is.True);
        Assert.That(response.ProviderMessageId, Is.EqualTo("SM123"));
        var request = handler.OnlyRequest;
        Assert.That(request.Uri.ToString(), Is.EqualTo($"https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Messages.json"));
        Assert.That(request.Body, Is.EqualTo("To=whatsapp%3A%2B14165551234&From=whatsapp%3A%2B14155238886&Body=Hello+%26+welcome"));
    }

    [Test]
    public async Task A_sender_that_already_has_the_whatsapp_prefix_is_not_prefixed_twice()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created, Created);

        await Create(handler, o => o.WhatsAppFrom = "whatsapp:+14155238886").SendAsync(WhatsAppMessage.Create("+14165551234", "Hi"));

        Assert.That(handler.OnlyRequest.Body, Does.Contain("From=whatsapp%3A%2B14155238886").And.Not.Contain("whatsapp%3Awhatsapp"));
    }

    [Test]
    public async Task A_messaging_service_sender_and_a_per_message_sender_are_supported()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created, Created);
        var notifier = Create(handler, o => o.WhatsAppFrom = "MG00000000000000000000000000000000");

        await notifier.SendAsync(WhatsAppMessage.Create("+14165551234", "Hi"));
        await notifier.SendAsync(WhatsAppMessage.Create("+14165551234", "Hi") with { From = "+15551230000" });

        Assert.That(handler.Requests[0].Body, Does.Contain("MessagingServiceSid=MG").And.Not.Contain("From="));
        Assert.That(handler.Requests[1].Body, Does.Contain("From=whatsapp%3A%2B15551230000"));
    }

    [Test]
    public async Task An_approved_template_is_sent_as_a_content_sid_with_json_variables()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created, Created);

        var response = await Create(handler).SendAsync(
            WhatsAppMessage.FromTemplate("+14165551234", "HX00000000000000000000000000000000", new Dictionary<string, string> { ["1"] = "Ada", ["2"] = "Monday" }));

        Assert.That(response.Success, Is.True);
        var body = Uri.UnescapeDataString(handler.OnlyRequest.Body.Replace('+', ' '));
        Assert.That(body, Does.Contain("ContentSid=HX00000000000000000000000000000000"));
        Assert.That(body, Does.Contain("""ContentVariables={"1":"Ada","2":"Monday"}"""));
        Assert.That(body, Does.Not.Contain("Body="));
    }

    [Test]
    public async Task Each_recipient_gets_its_own_request_and_result()
    {
        var handler = new StubHttpHandler(r => Json(
            r.Body.Contains("14165550002", StringComparison.Ordinal) ? HttpStatusCode.BadRequest : HttpStatusCode.Created,
            r.Body.Contains("14165550002", StringComparison.Ordinal) ? """{"code":63016,"message":"Failed to send freeform message because you are outside the allowed window."}""" : Created));
        var message = new WhatsAppMessage { To = ["+14165550001", "+14165550002", "+14165550003"], Body = "Hi" };

        var response = await Create(handler).SendAsync(message);

        Assert.That(handler.Requests, Has.Count.EqualTo(3));
        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        var failed = response.Recipients.Single(r => !r.Accepted);
        Assert.That(failed.Recipient, Is.EqualTo("+14165550002"));
        Assert.That(failed.Error!.Value.Message, Does.Contain("63016"));
    }

    [Test]
    public async Task A_missing_sender_is_a_configuration_error_and_nothing_is_sent()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created, Created);
        var notifier = new TwilioWhatsAppNotifier(
            new TwilioOptions { AccountSid = AccountSid, AuthToken = "t", From = "+15005550006" }, new HttpClient(handler), new NotifierContext());

        var response = await notifier.SendAsync(WhatsAppMessage.Create("+14165551234", "Hi"));

        Assert.That(response.Errors[0].Code, Is.EqualTo("twilio.whatsapp.sender_missing"));
        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Configuration));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public void Whatsapp_messages_are_validated()
    {
        Assert.That(WhatsAppMessage.Create("+14165551234", "Hi").Validate(), Is.Empty);
        Assert.That(WhatsAppMessage.FromTemplate("+14165551234", "HX1").Validate(), Is.Empty);
        Assert.That(new WhatsAppMessage().Validate().Select(e => e.Code), Is.EquivalentTo(new[] { "whatsapp.recipients.required", "whatsapp.content.required" }));
        Assert.That((WhatsAppMessage.Create("4165551234", "Hi")).Validate().Select(e => e.Code), Does.Contain("whatsapp.number.invalid"));
        Assert.That((WhatsAppMessage.Create("+14165551234", "Hi") with { TemplateId = "HX1" }).Validate().Select(e => e.Code), Does.Contain("whatsapp.content.ambiguous"));
        Assert.That((WhatsAppMessage.Create("+14165551234", "Hi") with { TemplateVariables = new Dictionary<string, string> { ["1"] = "x" } }).Validate().Select(e => e.Code),
            Does.Contain("whatsapp.variables.invalid"));
    }

    [Test]
    public void Twilio_needs_at_least_one_sender_and_registers_the_channels_it_is_asked_for()
    {
        Assert.Throws<ArgumentException>(() => new TwilioWhatsAppNotifier(
            new TwilioOptions { AccountSid = AccountSid, AuthToken = "t" }, new HttpClient(), new NotifierContext()));

        using var both = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddTwilio(o => { o.AccountSid = AccountSid; o.AuthToken = "t"; o.From = "+15005550006"; o.WhatsAppFrom = "+14155238886"; }))
            .BuildServiceProvider();
        Assert.That(both.GetRequiredService<ISmsNotifier>(), Is.InstanceOf<TwilioSmsNotifier>());
        Assert.That(both.GetRequiredService<IWhatsAppNotifier>(), Is.InstanceOf<TwilioWhatsAppNotifier>());
        Assert.That(both.GetRequiredKeyedService<IWhatsAppNotifier>("twilio"), Is.SameAs(both.GetRequiredService<IWhatsAppNotifier>()));

        using var whatsAppOnly = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddTwilio(o => { o.AccountSid = AccountSid; o.AuthToken = "t"; o.WhatsAppFrom = "+14155238886"; }, channels: TwilioChannels.WhatsApp))
            .BuildServiceProvider();
        Assert.That(whatsAppOnly.GetService<ISmsNotifier>(), Is.Null, "Twilio must not become the SMS default when only WhatsApp was asked for");
        Assert.That(whatsAppOnly.GetRequiredService<IWhatsAppNotifier>(), Is.Not.Null);
    }

    [Test]
    public async Task The_dispatcher_sends_whatsapp_to_the_recipients_phone_next_to_sms()
    {
        var handler = new StubHttpHandler(r => Json(HttpStatusCode.Created, Created));
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddNotifier<IWhatsAppNotifier>("twilio", null, (_, _) => Create(handler)).AddFakeNotifiers())
            .BuildServiceProvider();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To(new Recipient { Phone = "+14165551234" }).WhatsApp("Hi {{Name}}").Sms("Hi {{Name}}").WithData(new { Name = "Ada" }));

        Assert.That(result.Success, Is.True);
        Assert.That(Uri.UnescapeDataString(handler.OnlyRequest.Body.Replace('+', ' ')), Does.Contain("To=whatsapp:+14165551234").And.Contain("Body=Hi Ada"));
        Assert.That(provider.GetRequiredService<FakeSmsNotifier>().Sent.Single().Body, Is.EqualTo("Hi Ada"));
    }

    [Test]
    public async Task A_recipient_without_a_phone_fails_only_the_whatsapp_channel()
    {
        using var provider = new ServiceCollection().AddUniversalNotifier(n => n.AddFakeNotifiers()).BuildServiceProvider();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com").Email("Hi", text: "x").WhatsApp("Hi"));

        Assert.That(result[NotificationChannel.Email]!.Success, Is.True);
        Assert.That(result[NotificationChannel.WhatsApp]!.Errors[0].Code, Is.EqualTo("whatsapp.address.missing").Or.EqualTo("whatsapp.provider.missing"));
    }
}
