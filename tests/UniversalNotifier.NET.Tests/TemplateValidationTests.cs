using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UniversalNotifier.NET.Email.Mailgun;
using UniversalNotifier.NET.Email.Smtp;
using UniversalNotifier.NET.Templates.Fluid;
using UniversalNotifier.NET.Templates.Scriban;
using UniversalNotifier.NET.Testing;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class TemplateValidationTests
{
    private static readonly Assembly ThisAssembly = typeof(TemplateValidationTests).Assembly;
    private const string Templates = "UniversalNotifier.NET.Tests.Templates";
    private const string Broken = "UniversalNotifier.NET.Tests.BrokenTemplates";

    [Test]
    public async Task Embedded_templates_load_by_name_and_channel_and_render_through_the_dispatcher()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddFakeNotifiers().AddEmbeddedTemplates(ThisAssembly, Templates))
            .BuildServiceProvider();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To(new Recipient { Email = "ada@example.com", Phone = "+14165551234" }).Email().Sms().UseTemplate("welcome").WithData(new { Name = "Ada" }));

        Assert.That(result.Success, Is.True);
        var email = provider.GetRequiredService<FakeEmailNotifier>().Sent.Single();
        Assert.That(email.Subject, Is.EqualTo("Welcome, Ada!"));
        Assert.That(email.HtmlBody, Is.EqualTo("<p>Hello <b>Ada</b></p>"));
        Assert.That(provider.GetRequiredService<FakeSmsNotifier>().Sent.Single().Body, Is.EqualTo("Hi Ada, welcome aboard!"));
    }

    [Test]
    public async Task Hyphenated_template_names_match_msbuilds_underscored_resource_names()
    {
        var store = new EmbeddedTemplateStore(ThisAssembly, Templates);

        var content = await store.TryGetAsync("order-shipped", NotificationChannel.Email);

        Assert.That(content!.Subject, Is.EqualTo("Order {{Number}} shipped"));
        Assert.That(await store.TryGetAsync("order-shipped", NotificationChannel.Sms), Is.Null);
        Assert.That(await store.TryGetAsync("missing", NotificationChannel.Email), Is.Null);
    }

    [Test]
    public void The_embedded_store_lists_what_it_holds()
    {
        var entries = new EmbeddedTemplateStore(ThisAssembly, Templates).List().Select(e => $"{e.Name}/{e.Channel}").OrderBy(x => x).ToArray();

        Assert.That(entries, Is.EqualTo(new[] { "order_shipped/email", "welcome/email", "welcome/sms" }));
    }

    [Test]
    public void The_built_in_renderer_accepts_everything_so_validation_has_nothing_to_report()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddEmbeddedTemplates(ThisAssembly, Broken))
            .BuildServiceProvider();

        Assert.That(TemplateValidator.Validate(provider), Is.Empty);
        Assert.DoesNotThrow(() => TemplateValidator.ThrowIfInvalid(provider));
    }

    [Test]
    public void A_syntax_error_in_a_scriban_template_is_found_at_startup_not_at_the_first_send()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.UseScriban().AddEmbeddedTemplates(ThisAssembly, Broken).AddEmbeddedTemplates(ThisAssembly, Templates))
            .BuildServiceProvider();

        var problems = TemplateValidator.Validate(provider);

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0].Template, Is.EqualTo("broken"));
        Assert.That(problems[0].Part, Is.EqualTo("Subject"));
        Assert.That(problems[0].ToString(), Does.Contain("broken/email"));
        var ex = Assert.Throws<TemplateRenderException>(() => TemplateValidator.ThrowIfInvalid(provider));
        Assert.That(ex!.Message, Does.Contain("broken"));
    }

    [Test]
    public void The_same_check_works_for_fluid_and_for_templates_registered_in_code()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n
                .UseFluid()
                .AddTemplate("bad", NotificationChannel.Sms, new ChannelContent { Text = "{% if %}", Data = new Dictionary<string, string> { ["k"] = "{% for %}" } })
                .AddTemplate("good", NotificationChannel.Sms, new ChannelContent { Text = "Hi {{ Name }}" }))
            .BuildServiceProvider();

        var problems = TemplateValidator.Validate(provider);

        Assert.That(problems.Select(p => p.Part).OrderBy(p => p), Is.EqualTo(new[] { "Data[k]", "Text" }));
        Assert.That(problems.All(p => p.Template == "bad"));
    }

    [Test]
    public void Directory_templates_are_validated_too_and_the_standalone_builder_fails_fast()
    {
        var root = Path.Join(Path.GetTempPath(), "un-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(root, "welcome"));
        File.WriteAllText(Path.Join(root, "welcome", "email.subject.txt"), "{{ if }}");

        try
        {
            Assert.Throws<TemplateRenderException>(() => new NotifierBuilder().AddFakeNotifiers().UseScriban().AddTemplatesFromDirectory(root).Build());

            File.WriteAllText(Path.Join(root, "welcome", "email.subject.txt"), "Hi {{ Name }}");
            using var notifier = new NotifierBuilder().AddFakeNotifiers().UseScriban().AddTemplatesFromDirectory(root).Build();
            Assert.That(notifier, Is.Not.Null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Provider_options_bind_from_an_appsettings_json_section_as_the_configuration_guide_shows()
    {
        const string json = """
        {
          "Notifications": {
            "Smtp": { "Host": "smtp.example.com", "Port": 465, "Security": "SslOnConnect", "UserName": "mailer", "Password": "pw", "From": { "Address": "noreply@example.com", "Name": "Example" } },
            "Mailgun": { "ApiKey": "key-1", "Domain": "mg.example.com", "Region": "Eu", "From": { "Address": "noreply@mg.example.com" } }
          }
        }
        """;
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();
        var services = new ServiceCollection();
        services.Configure<SmtpOptions>("smtp", configuration.GetSection("Notifications:Smtp"));
        services.Configure<MailgunOptions>("mailgun", configuration.GetSection("Notifications:Mailgun"));

        using var provider = services.AddUniversalNotifier(n => n.AddSmtp(name: "smtp-backup").AddMailgun()).BuildServiceProvider();

        var mailgun = provider.GetRequiredService<IEmailNotifier>();
        Assert.That(mailgun, Is.InstanceOf<MailgunNotifier>());
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<SmtpOptions>>().Get("smtp");
        Assert.That(options.Host, Is.EqualTo("smtp.example.com"));
        Assert.That(options.Port, Is.EqualTo(465));
        Assert.That(options.Security, Is.EqualTo(SmtpSecurity.SslOnConnect));
        Assert.That(options.From.Address, Is.EqualTo("noreply@example.com"));
        Assert.That(options.From.Name, Is.EqualTo("Example"));
        Assert.That(provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<MailgunOptions>>().Get("mailgun").Region, Is.EqualTo(MailgunRegion.Eu));
    }
}
