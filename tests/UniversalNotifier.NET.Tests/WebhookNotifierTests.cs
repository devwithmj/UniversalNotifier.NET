using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET.Testing;
using UniversalNotifier.NET.Webhook;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class WebhookNotifierTests
{
    private const string SlackUrl = "https://hooks.slack.com/services/T0/B0/xyz";

    private static WebhookNotifier Create(StubHttpHandler handler, Action<WebhookOptions>? configure = null)
    {
        var options = new WebhookOptions()
            .Add("slack", SlackUrl, WebhookFormat.Slack)
            .Add("teams", "https://prod.workflows.example.com/teams/abc", WebhookFormat.Teams)
            .Add("generic", "https://example.com/hooks/notify", WebhookFormat.Json, signingSecret: "s3cret");
        configure?.Invoke(options);
        return new WebhookNotifier(options, new NotifierContext(), handler);
    }

    [Test]
    public async Task Slack_messages_are_mrkdwn_text_with_special_characters_escaped()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "ok");
        var message = new WebhookMessage
        {
            Endpoint = "slack",
            Title = "Deploy <prod>",
            Text = "Build #42 & tests passed",
            Data = new Dictionary<string, string> { ["version"] = "1.2.3" },
        };

        var response = await Create(handler).SendAsync(message);

        Assert.That(response.Success, Is.True);
        Assert.That(handler.OnlyRequest.Uri.ToString(), Is.EqualTo(SlackUrl));
        using var body = JsonDocument.Parse(handler.OnlyRequest.Body);
        Assert.That(body.RootElement.GetProperty("text").GetString(), Is.EqualTo("*Deploy &lt;prod&gt;*\nBuild #42 &amp; tests passed\n• version: 1.2.3"));
    }

    [Test]
    public async Task Teams_messages_are_adaptive_card_messages()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted);
        var message = new WebhookMessage
        {
            Endpoint = "teams",
            Title = "Alert",
            Text = "Disk almost full",
            Data = new Dictionary<string, string> { ["host"] = "web-1" },
        };

        var response = await Create(handler).SendAsync(message);

        Assert.That(response.Success, Is.True);
        using var body = JsonDocument.Parse(handler.OnlyRequest.Body);
        var root = body.RootElement;
        Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("message"));
        var attachment = root.GetProperty("attachments")[0];
        Assert.That(attachment.GetProperty("contentType").GetString(), Is.EqualTo("application/vnd.microsoft.card.adaptive"));
        var card = attachment.GetProperty("content");
        Assert.That(card.GetProperty("$schema").GetString(), Does.Contain("adaptive-card"));
        Assert.That(card.GetProperty("type").GetString(), Is.EqualTo("AdaptiveCard"));
        var blocks = card.GetProperty("body");
        Assert.That(blocks[0].GetProperty("text").GetString(), Is.EqualTo("Alert"));
        Assert.That(blocks[1].GetProperty("text").GetString(), Is.EqualTo("Disk almost full"));
        Assert.That(blocks[2].GetProperty("facts")[0].GetProperty("title").GetString(), Is.EqualTo("host"));
    }

    [Test]
    public async Task Generic_json_endpoints_get_the_message_and_a_verifiable_signature()
    {
        var handler = new StubHttpHandler(HttpStatusCode.NoContent);

        var response = await Create(handler).SendAsync(
            new WebhookMessage { Endpoint = "generic", Id = "n-7", Title = "T", Text = "Body", Data = new Dictionary<string, string> { ["k"] = "v" } });

        Assert.That(response.Success, Is.True);
        var request = handler.OnlyRequest;
        using var body = JsonDocument.Parse(request.Body);
        Assert.That(body.RootElement.GetProperty("id").GetString(), Is.EqualTo("n-7"));
        Assert.That(body.RootElement.GetProperty("title").GetString(), Is.EqualTo("T"));
        Assert.That(body.RootElement.GetProperty("text").GetString(), Is.EqualTo("Body"));
        Assert.That(body.RootElement.GetProperty("data").GetProperty("k").GetString(), Is.EqualTo("v"));

        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), Encoding.UTF8.GetBytes(request.Body)));
        Assert.That(request.Headers["X-Signature-256"], Is.EqualTo(expected));
    }

    [Test]
    public async Task Endpoints_without_a_signing_secret_send_no_signature()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "ok");

        await Create(handler).SendAsync(WebhookMessage.Create("slack", "x"));

        Assert.That(handler.OnlyRequest.Headers.ContainsKey("X-Signature-256"), Is.False);
    }

    [Test]
    public async Task Endpoint_names_are_case_insensitive_and_unknown_names_are_a_configuration_error()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "ok");
        var notifier = Create(handler);

        var found = await notifier.SendAsync(WebhookMessage.Create("SLACK", "x"));
        var missing = await notifier.SendAsync(WebhookMessage.Create("nope", "x"));

        Assert.That(found.Success, Is.True);
        Assert.That(missing.Errors[0].Code, Is.EqualTo("webhook.endpoint.unknown"));
        Assert.That(missing.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Configuration));
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [TestCase(HttpStatusCode.Unauthorized, NotifierErrorKind.Configuration)]
    [TestCase(HttpStatusCode.NotFound, NotifierErrorKind.Permanent)]
    [TestCase(HttpStatusCode.TooManyRequests, NotifierErrorKind.Transient)]
    [TestCase(HttpStatusCode.InternalServerError, NotifierErrorKind.Unknown)]
    public async Task Http_errors_are_classified_and_never_contain_the_url(HttpStatusCode status, NotifierErrorKind kind)
    {
        var response = await Create(new StubHttpHandler(status, "no_service")).SendAsync(WebhookMessage.Create("slack", "x"));

        Assert.That(response.Errors[0].Kind, Is.EqualTo(kind));
        Assert.That(response.Errors[0].Message, Does.Not.Contain("T0/B0").And.Not.Contain("hooks.slack.com").And.Not.Contain("no_service"));
    }

    [Test]
    public void Invalid_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new WebhookNotifier(new WebhookOptions(), new NotifierContext()));
        Assert.Throws<ArgumentException>(() => new WebhookNotifier(new WebhookOptions().Add("x", "http://insecure.example.com/hook"), new NotifierContext()));
        var noUrl = new WebhookOptions();
        noUrl.Endpoints["x"] = new WebhookEndpoint();
        Assert.Throws<ArgumentException>(() => new WebhookNotifier(noUrl, new NotifierContext()));
    }

    [Test]
    public void Options_never_print_urls_or_secrets()
    {
        var options = new WebhookOptions().Add("x", SlackUrl, signingSecret: "s3cret");

        Assert.That(options.ToString(), Does.Not.Contain("slack").And.Not.Contain("s3cret"));
        Assert.That(options.Endpoints["x"].ToString(), Does.Not.Contain("slack").And.Not.Contain("s3cret"));
    }

    [Test]
    public async Task It_works_through_the_dispatcher_with_a_recipient_endpoint()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "ok");
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddNotifier<IWebhookNotifier>("webhook", null, (_, _) => Create(handler)))
            .BuildServiceProvider();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To(new Recipient { WebhookEndpoint = "slack" }).Webhook("Deploy {{Version}}", "Done").WithData(new { Version = "2.0" }));

        Assert.That(result.Success, Is.True);
        using var body = JsonDocument.Parse(handler.OnlyRequest.Body);
        Assert.That(body.RootElement.GetProperty("text").GetString(), Is.EqualTo("*Deploy 2.0*\nDone"));
    }

    [Test]
    public async Task A_recipient_without_a_webhook_endpoint_fails_only_that_channel()
    {
        using var provider = new ServiceCollection().AddUniversalNotifier(n => n.AddFakeNotifiers()).BuildServiceProvider();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To("ada@example.com").Email("Hi", text: "x").Webhook("T", "x"));

        Assert.That(result[NotificationChannel.Email]!.Success, Is.True);
        Assert.That(result[NotificationChannel.Webhook]!.Errors[0].Code, Is.EqualTo("webhook.provider.missing").Or.EqualTo("webhook.address.missing"));
    }

    [Test]
    public void It_registers_through_dependency_injection()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddWebhook(o => o.Add("ops", SlackUrl, WebhookFormat.Slack)))
            .BuildServiceProvider();

        Assert.That(provider.GetRequiredService<IWebhookNotifier>(), Is.InstanceOf<WebhookNotifier>());
        Assert.That(provider.GetRequiredKeyedService<IWebhookNotifier>("webhook"), Is.SameAs(provider.GetRequiredService<IWebhookNotifier>()));
    }

    [Test]
    public void Invalid_settings_are_reported_by_the_options_system()
    {
        using var provider = new ServiceCollection().AddUniversalNotifier(n => n.AddWebhook()).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IWebhookNotifier>());
    }
}
