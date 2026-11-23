using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET.Testing;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class ConfigurationTests
{
    [Test]
    public async Task The_builder_works_without_a_host()
    {
        await using var notifier = new NotifierBuilder().AddFakeNotifiers().Build();

        var direct = await notifier.Email.SendAsync(EmailMessage.Create("ada@example.com", "Hi", "Body"));
        var routed = await notifier.SendAsync(Notification.To("ada@example.com").Email("Hi", text: "x"));

        Assert.That(direct.Success, Is.True);
        Assert.That(routed.Success, Is.True);
        Assert.That(notifier.GetNotifier<IEmailNotifier>("fake"), Is.SameAs(notifier.Email));
    }

    [Test]
    public void Asking_for_a_channel_with_no_provider_explains_what_to_do()
    {
        using var notifier = new NotifierBuilder().Build();

        var ex = Assert.Throws<InvalidOperationException>(() => _ = notifier.Sms)!;

        Assert.That(ex.Message, Does.Contain("SMS").And.Contain("Add one on the builder"));
        Assert.Throws<InvalidOperationException>(() => notifier.GetNotifier<IEmailNotifier>("missing"));
    }

    [Test]
    public void Invalid_retry_settings_fail_when_the_notifier_is_built()
    {
        var builder = new NotifierBuilder().WithRetries(maxAttempts: 50);

        Assert.Throws<OptionsValidationException>(() => builder.Build());
    }

    [Test]
    public void Valid_retry_settings_are_applied()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.WithRetries(4, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), retryUnknownFailures: true))
            .BuildServiceProvider();

        var retry = provider.GetRequiredService<NotifierContext>().Retry;

        Assert.That(retry.MaxAttempts, Is.EqualTo(4));
        Assert.That(retry.BaseDelay, Is.EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(retry.MaxDelay, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(retry.RetryUnknownFailures, Is.True);
    }

    [Test]
    public void Provider_options_can_be_supplied_through_named_options_for_configuration_binding()
    {
        using var provider = new ServiceCollection()
            .Configure<UniversalNotifier.NET.Email.Mailgun.MailgunOptions>("mailgun", o =>
            {
                o.ApiKey = "k";
                o.Domain = "mg.example.com";
                o.From = "a@mg.example.com";
            })
            .AddUniversalNotifier(n => n.AddMailgun())
            .BuildServiceProvider();

        Assert.That(provider.GetRequiredService<IEmailNotifier>(), Is.InstanceOf<UniversalNotifier.NET.Email.Mailgun.MailgunNotifier>());
    }

    [Test]
    public void Calling_AddUniversalNotifier_twice_does_not_duplicate_services()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier()
            .AddUniversalNotifier()
            .BuildServiceProvider();

        Assert.That(provider.GetServices<INotificationChannel>().Count(), Is.EqualTo(7));
        Assert.That(provider.GetServices<ITemplateStore>().Count(), Is.EqualTo(1));
    }

    private sealed class UpperRenderer : ITemplateRenderer
    {
        public string Render(string templateText, TemplateData data, TemplateFormat format = TemplateFormat.Text) =>
            templateText.ToUpperInvariant();
    }

    [Test]
    public async Task The_template_renderer_can_be_replaced()
    {
        await using var notifier = new NotifierBuilder().AddFakeNotifiers().UseTemplateRenderer<UpperRenderer>().Build();

        await notifier.SendAsync(Notification.To("ada@example.com").Email("hi", text: "body"));

        Assert.That(notifier.GetNotifier<IEmailNotifier>(), Is.InstanceOf<FakeEmailNotifier>());
        Assert.That(((FakeEmailNotifier)notifier.Email).Sent.Single().Subject, Is.EqualTo("HI"));
    }

    [Test]
    public async Task Templates_can_be_loaded_from_a_directory_and_path_traversal_is_blocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "un-templates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "welcome"));
        await File.WriteAllTextAsync(Path.Combine(root, "welcome", "email.subject.txt"), "Hello {{Name}}");
        await File.WriteAllTextAsync(Path.Combine(root, "welcome", "email.html"), "<p>{{Name}}</p>");
        await File.WriteAllTextAsync(Path.Combine(root, "secret.email.text.txt"), "outside");

        try
        {
            var store = new DirectoryTemplateStore(root);

            var found = await store.TryGetAsync("welcome", NotificationChannel.Email);
            Assert.That(found!.Subject, Is.EqualTo("Hello {{Name}}"));
            Assert.That(found.Html, Is.EqualTo("<p>{{Name}}</p>"));

            Assert.That(await store.TryGetAsync("welcome", NotificationChannel.Sms), Is.Null);
            Assert.That(await store.TryGetAsync("nope", NotificationChannel.Email), Is.Null);
            Assert.That(await store.TryGetAsync("../" + Path.GetFileName(root), NotificationChannel.Email), Is.Null);
            Assert.That(await store.TryGetAsync("welcome", new NotificationChannel("../x")), Is.Null);

            await using var notifier = new NotifierBuilder().AddFakeNotifiers().AddTemplatesFromDirectory(root).Build();
            var result = await notifier.SendAsync(Notification.To("ada@example.com").Email().UseTemplate("welcome").WithData(new { Name = "Ada" }));
            Assert.That(result.Success, Is.True);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Fake_notifiers_validate_like_real_providers()
    {
        var fake = new FakeEmailNotifier();

        var response = fake.SendAsync(new EmailMessage()).GetAwaiter().GetResult();

        Assert.That(response.Success, Is.False);
        Assert.That(fake.Sent, Has.Count.EqualTo(1));
        fake.Clear();
        Assert.That(fake.Sent, Is.Empty);
    }
}
