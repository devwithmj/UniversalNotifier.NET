using Microsoft.Extensions.DependencyInjection;
using UniversalNotifier.NET.Testing;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class NotificationServiceTests
{
    private static ServiceProvider Build(Action<INotifierBuilder>? configure = null) =>
        new ServiceCollection()
            .AddUniversalNotifier(n =>
            {
                n.AddFakeNotifiers();
                configure?.Invoke(n);
            })
            .BuildServiceProvider();

    private static Recipient Everyone() => new()
    {
        Email = "ada@example.com",
        Phone = "+14165551234",
        DeviceTokens = ["device-1"],
        TelegramChatId = "42",
        WebPushSubscriptions = [new WebPushSubscription("https://push.example.com/x", "k", "a")],
    };

    [Test]
    public async Task Sends_inline_content_on_every_requested_channel()
    {
        using var provider = Build();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To(Everyone())
                .Email("Welcome", html: "<b>Hi {{Name}}</b>", text: "Hi {{Name}}")
                .Sms("Hi {{Name}}")
                .Push("Welcome", "Hi {{Name}}")
                .Telegram("Hi {{Name}}")
                .WebPush("Welcome", "Hi {{Name}}")
                .WithData(new { Name = "Ada" })
                .WithId("n-1"));

        Assert.That(result.Success, Is.True);
        Assert.That(result.NotificationId, Is.EqualTo("n-1"));
        Assert.That(result.Channels.Select(c => c.Channel.Name), Is.EqualTo(new[] { "email", "sms", "push", "telegram", "webpush" }));

        var email = provider.GetRequiredService<FakeEmailNotifier>().Sent.Single();
        Assert.That(email.To.Single().Address, Is.EqualTo("ada@example.com"));
        Assert.That(email.Subject, Is.EqualTo("Welcome"));
        Assert.That(email.HtmlBody, Is.EqualTo("<b>Hi Ada</b>"));
        Assert.That(email.TextBody, Is.EqualTo("Hi Ada"));
        Assert.That(email.Id, Is.EqualTo("n-1"));

        Assert.That(provider.GetRequiredService<FakeSmsNotifier>().Sent.Single().Body, Is.EqualTo("Hi Ada"));
        Assert.That(provider.GetRequiredService<FakePushNotifier>().Sent.Single().Tokens, Is.EqualTo(new[] { "device-1" }));
        Assert.That(provider.GetRequiredService<FakeTelegramNotifier>().Sent.Single().ChatIds, Is.EqualTo(new[] { "42" }));
        Assert.That(provider.GetRequiredService<FakeWebPushNotifier>().Sent.Single().Title, Is.EqualTo("Welcome"));
    }

    [Test]
    public async Task Html_content_is_encoded_but_text_content_is_not()
    {
        using var provider = Build();

        await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com")
                .Email("Hi", html: "<p>{{Name}}</p>", text: "{{Name}}")
                .WithData(new { Name = "<b>Ada</b>" }));

        var email = provider.GetRequiredService<FakeEmailNotifier>().Sent.Single();
        Assert.That(email.HtmlBody, Is.EqualTo("<p>&lt;b&gt;Ada&lt;/b&gt;</p>"));
        Assert.That(email.TextBody, Is.EqualTo("<b>Ada</b>"));
    }

    [Test]
    public async Task A_named_template_supplies_content_per_channel()
    {
        using var provider = Build(n => n
            .AddTemplate("welcome", NotificationChannel.Email, new ChannelContent { Subject = "Welcome {{Name}}", Html = "<p>Hello {{Name}}</p>" })
            .AddTemplate("welcome", NotificationChannel.Sms, new ChannelContent { Text = "Hello {{Name}}" }));

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To(Everyone()).Email().Sms().UseTemplate("welcome").WithData(new { Name = "Ada" }));

        Assert.That(result.Success, Is.True);
        Assert.That(provider.GetRequiredService<FakeEmailNotifier>().Sent.Single().Subject, Is.EqualTo("Welcome Ada"));
        Assert.That(provider.GetRequiredService<FakeSmsNotifier>().Sent.Single().Body, Is.EqualTo("Hello Ada"));
    }

    [Test]
    public async Task Inline_content_overrides_only_the_parts_it_sets()
    {
        using var provider = Build(n => n
            .AddTemplate("welcome", NotificationChannel.Email, new ChannelContent { Subject = "From template", Text = "Template body" }));

        await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com").Email(subject: "Override").UseTemplate("welcome"));

        var email = provider.GetRequiredService<FakeEmailNotifier>().Sent.Single();
        Assert.That(email.Subject, Is.EqualTo("Override"));
        Assert.That(email.TextBody, Is.EqualTo("Template body"));
    }

    [Test]
    public async Task A_channel_the_template_does_not_define_fails_with_a_clear_error_and_does_not_stop_the_others()
    {
        using var provider = Build(n => n
            .AddTemplate("welcome", NotificationChannel.Email, new ChannelContent { Subject = "S", Text = "B" }));

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To(Everyone()).Email().Sms().UseTemplate("welcome"));

        Assert.That(result.Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        Assert.That(result[NotificationChannel.Email]!.Success, Is.True);
        Assert.That(result[NotificationChannel.Sms]!.Errors[0].Code, Is.EqualTo("sms.content.missing"));
    }

    [Test]
    public async Task A_missing_template_token_fails_that_channel_as_a_validation_error()
    {
        using var provider = Build();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com").Email("Hi {{Name}}", text: "x"));

        Assert.That(result.Success, Is.False);
        Assert.That(result[NotificationChannel.Email]!.Errors[0].Code, Is.EqualTo("template.render"));
        Assert.That(result[NotificationChannel.Email]!.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Validation));
        Assert.That(provider.GetRequiredService<FakeEmailNotifier>().Sent, Is.Empty);
    }

    [Test]
    public async Task A_recipient_without_an_address_for_the_channel_fails_that_channel_only()
    {
        using var provider = Build();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com").Email("Hi", text: "x").Sms("Hi"));

        Assert.That(result[NotificationChannel.Email]!.Success, Is.True);
        Assert.That(result[NotificationChannel.Sms]!.Errors[0].Code, Is.EqualTo("sms.address.missing"));
    }

    [Test]
    public async Task A_channel_without_a_registered_provider_reports_a_configuration_error()
    {
        using var provider = new ServiceCollection().AddUniversalNotifier().BuildServiceProvider();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com").Email("Hi", text: "x"));

        var error = result[NotificationChannel.Email]!.Errors[0];
        Assert.That(error.Code, Is.EqualTo("email.provider.missing"));
        Assert.That(error.Kind, Is.EqualTo(NotifierErrorKind.Configuration));
    }

    [Test]
    public async Task An_unregistered_custom_channel_reports_a_configuration_error()
    {
        using var provider = Build();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com").Via(new NotificationChannel("slack"), new ChannelContent { Text = "x" }));

        Assert.That(result[new NotificationChannel("slack")]!.Errors[0].Code, Is.EqualTo("slack.channel.unknown"));
    }

    private sealed class SlackChannel : INotificationChannel
    {
        public NotificationChannel Channel => new("slack");

        public Task<NotifierResponse> SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken) =>
            Task.FromResult(NotifierResponse.Accepted("slack:" + delivery.Content.Text));
    }

    [Test]
    public async Task Custom_channels_can_be_added()
    {
        using var provider = Build(n => n.AddChannel<SlackChannel>());

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com").Via(new NotificationChannel("Slack"), new ChannelContent { Text = "hi {{N}}" }).WithData(new { N = 1 }));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Channels[0].Response.ProviderMessageId, Is.EqualTo("slack:hi 1"));
    }

    [Test]
    public async Task A_named_provider_can_be_chosen_per_channel()
    {
        var backup = new FakeEmailNotifier();
        using var provider = Build(n => n.AddNotifier<IEmailNotifier>("backup", "backup", (_, _) => backup));

        await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com").Email("Hi", text: "x", provider: "backup"));
        await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com").Email("Hi", text: "x"));

        Assert.That(backup.Sent, Has.Count.EqualTo(1));
        Assert.That(provider.GetRequiredService<FakeEmailNotifier>().Sent, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Asking_for_an_unknown_provider_name_reports_it()
    {
        using var provider = Build();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com").Email("Hi", text: "x", provider: "nope"));

        Assert.That(result.Channels[0].Response.Errors[0].Message, Does.Contain("'nope'"));
    }

    [Test]
    public async Task The_last_unnamed_registration_is_the_default_and_both_stay_reachable_by_name()
    {
        var first = new FakeSmsNotifier();
        var second = new FakeSmsNotifier();
        using var provider = Build(n => n
            .AddNotifier<ISmsNotifier>("first", null, (_, _) => first)
            .AddNotifier<ISmsNotifier>("second", null, (_, _) => second));

        await provider.GetRequiredService<INotificationService>().SendAsync(Notification.To("+14165551234").Sms("a"));
        await provider.GetRequiredService<INotificationService>().SendAsync(Notification.To("+14165551234").Sms("b", provider: "first"));

        Assert.That(second.Sent.Select(m => m.Body), Is.EqualTo(new[] { "a" }));
        Assert.That(first.Sent.Select(m => m.Body), Is.EqualTo(new[] { "b" }));
    }

    [Test]
    public async Task A_provider_failure_is_a_result_not_an_exception()
    {
        using var provider = Build();
        provider.GetRequiredService<FakeEmailNotifier>().Responder = _ =>
            NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Permanent, "rejected", "no"));

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com").Email("Hi", text: "x"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Status, Is.EqualTo(DeliveryStatus.Failed));
    }

    [Test]
    public void A_notification_without_channels_is_a_programmer_error()
    {
        using var provider = Build();

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await provider.GetRequiredService<INotificationService>().SendAsync(Notification.To("ada@example.com")));
    }

    [Test]
    public void Cancellation_propagates()
    {
        using var provider = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await provider.GetRequiredService<INotificationService>().SendAsync(
                Notification.To("ada@example.com").Email("Hi", text: "x"), cts.Token));
    }

    [Test]
    public void Recipient_parsing()
    {
        Assert.That(Recipient.Parse("ada@example.com").Email!.Value.Address, Is.EqualTo("ada@example.com"));
        Assert.That(Recipient.Parse("+14165551234").Phone!.Value.Value, Is.EqualTo("+14165551234"));
        Assert.Throws<ArgumentException>(() => Recipient.Parse("somebody"));
    }

    [Test]
    public void Notifications_are_immutable_values()
    {
        var original = Notification.To("ada@example.com");
        var extended = original.Email("Hi", text: "x");

        Assert.That(original.Channels, Is.Empty);
        Assert.That(extended.Channels, Has.Count.EqualTo(1));
    }

    [Test]
    public void Data_is_copied_when_set_so_later_changes_do_not_affect_a_queued_notification()
    {
        var values = new Dictionary<string, object?> { ["Name"] = "Ada" };
        var notification = Notification.To("ada@example.com").WithData(values);

        values["Name"] = "Changed";

        Assert.That(notification.Data!["Name"], Is.EqualTo("Ada"));
    }

    [Test]
    public void Channel_names_are_case_insensitive()
    {
        Assert.That(new NotificationChannel("EMAIL"), Is.EqualTo(NotificationChannel.Email));
        Assert.That(default(NotificationChannel).Name, Is.Empty);
    }
}
