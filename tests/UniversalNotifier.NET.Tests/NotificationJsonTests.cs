using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UniversalNotifier.NET.Testing;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class NotificationJsonTests
{
    private static Notification Sample() =>
        Notification.To(new Recipient
        {
            Email = new EmailAddress("ada@example.com", "Ada Lovelace"),
            Phone = "+14165551234",
            DeviceTokens = ["device-1"],
            TelegramChatId = "42",
            WebPushSubscriptions = [new WebPushSubscription("https://push.example.com/x", "k", "a")],
        })
            .Email("Hi {{User.Name}}", html: "<p>{{User.Name}} owes {{Amount}}</p>", text: "{{Active}}")
            .Sms("Code {{Code}}", provider: "backup")
            .Push("Title", "Body", new Dictionary<string, string> { ["k"] = "{{Code}}" })
            .UseTemplate("welcome")
            .WithId("n-1")
            .WithData(new { User = new { Name = "Ada" }, Amount = 12.5m, Code = 123456, Active = true });

    [Test]
    public void A_notification_survives_a_json_round_trip()
    {
        var original = Sample();

        var copy = NotificationJson.Deserialize(NotificationJson.Serialize(original));

        Assert.That(copy.Id, Is.EqualTo("n-1"));
        Assert.That(copy.TemplateName, Is.EqualTo("welcome"));
        Assert.That(copy.Recipient.Email, Is.EqualTo(original.Recipient.Email));
        Assert.That(copy.Recipient.Phone, Is.EqualTo(original.Recipient.Phone));
        Assert.That(copy.Recipient.DeviceTokens, Is.EqualTo(new[] { "device-1" }));
        Assert.That(copy.Recipient.TelegramChatId, Is.EqualTo("42"));
        Assert.That(copy.Recipient.WebPushSubscriptions.Single().Endpoint, Is.EqualTo("https://push.example.com/x"));
        Assert.That(copy.Channels.Select(c => (c.Channel.Name, c.Provider)), Is.EqualTo(new[] { ("email", (string?)null), ("sms", "backup"), ("push", null) }));
        Assert.That(copy.Channels[2].Content!.Data!["k"], Is.EqualTo("{{Code}}"));
    }

    [Test]
    public async Task A_deserialized_notification_renders_exactly_like_the_original_including_nested_data()
    {
        using var provider = new ServiceCollection().AddUniversalNotifier(n => n.AddFakeNotifiers()).BuildServiceProvider();
        var service = provider.GetRequiredService<INotificationService>();
        var email = provider.GetRequiredService<FakeEmailNotifier>();
        var original = Sample();

        await service.SendAsync(original);
        var fromOriginal = email.Sent.Single();
        email.Clear();
        await service.SendAsync(NotificationJson.Deserialize(NotificationJson.Serialize(original)));
        var fromJson = email.Sent.Single();

        Assert.That(fromJson.Subject, Is.EqualTo("Hi Ada").And.EqualTo(fromOriginal.Subject));
        Assert.That(fromJson.HtmlBody, Is.EqualTo("<p>Ada owes 12.5</p>").And.EqualTo(fromOriginal.HtmlBody));
        Assert.That(fromJson.TextBody, Is.EqualTo("true").And.EqualTo(fromOriginal.TextBody));
        Assert.That(provider.GetRequiredService<FakeSmsNotifier>().Sent, Is.Empty, "the sms asked for provider 'backup', which is not registered");
        Assert.That(provider.GetRequiredService<FakePushNotifier>().Sent.Select(m => m.Data!["k"]), Is.EqualTo(new[] { "123456", "123456" }));
    }

    [Test]
    public void Json_values_render_by_kind_and_html_is_still_encoded()
    {
        var data = TemplateData.From(JsonDocument.Parse("""{"S":"<b>","N":3,"B":false,"Z":null,"O":{"Inner":"x"}}""").RootElement);
        var renderer = new TokenTemplateRenderer();

        Assert.That(renderer.Render("{{S}}|{{N}}|{{B}}|{{Z}}|{{O.Inner}}|{{o.inner}}", data), Is.EqualTo("<b>|3|false||x|x"));
        Assert.That(renderer.Render("{{S}}", data, TemplateFormat.Html), Is.EqualTo("&lt;b&gt;"));
        Assert.Throws<TemplateRenderException>(() => renderer.Render("{{Missing}}", data));
        Assert.Throws<TemplateRenderException>(() => renderer.Render("{{N.Inner}}", data));
    }

    [Test]
    public void The_json_uses_plain_channel_names()
    {
        var json = NotificationJson.Serialize(Notification.To("ada@example.com").Via(new NotificationChannel("Slack"), new ChannelContent { Text = "x" }));

        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty("channels")[0].GetProperty("channel").GetString(), Is.EqualTo("slack"));
    }

    [Test]
    public void Invalid_json_is_reported_as_a_json_exception()
    {
        Assert.Throws<JsonException>(() => NotificationJson.Deserialize("{ not json"));
        Assert.Throws<JsonException>(() => NotificationJson.Deserialize("null"));
        Assert.Throws<ArgumentException>(() => NotificationJson.Deserialize(" "));
    }
}
