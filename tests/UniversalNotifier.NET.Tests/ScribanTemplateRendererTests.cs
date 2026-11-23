using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UniversalNotifier.NET.Templates.Scriban;
using UniversalNotifier.NET.Testing;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class ScribanTemplateRendererTests
{
    private static ScribanTemplateRenderer Renderer(MissingTokenBehavior missing = MissingTokenBehavior.Throw) =>
        new(new TemplateOptions { MissingToken = missing });

    private static string Render(string template, object data, TemplateFormat format = TemplateFormat.Text) =>
        Renderer().Render(template, TemplateData.From(data), format);

    [Test]
    public void Existing_token_templates_keep_working()
    {
        Assert.That(Render("Hello {{Name}}, {{User.City}}!", new { Name = "Ada", User = new { City = "London" } }), Is.EqualTo("Hello Ada, London!"));
    }

    [Test]
    public void Conditionals_loops_and_filters_are_available()
    {
        var data = new { Name = "ada", Total = 150, Items = new[] { "tea", "cake" } };

        var result = Render("{{ Name | string.capitalize }}: {{ if Total > 100 }}big{{ else }}small{{ end }}; {{ for i in Items }}[{{ i }}]{{ end }}", data);

        Assert.That(result, Is.EqualTo("Ada: big; [tea][cake]"));
    }

    [Test]
    public void Lists_of_objects_and_dictionaries_work()
    {
        var data = new Dictionary<string, object?>
        {
            ["Lines"] = new List<object> { new { Sku = "A1", Qty = 2 }, new Dictionary<string, object?> { ["Sku"] = "B2", ["Qty"] = 5 } },
        };

        Assert.That(Render("{{ for l in Lines }}{{ l.Sku }}x{{ l.Qty }} {{ end }}", data), Is.EqualTo("A1x2 B2x5 "));
    }

    [Test]
    public void Html_parts_encode_every_string_value_but_text_parts_do_not()
    {
        var data = new { Name = "<script>alert(1)</script>", Items = new[] { "a&b" } };

        Assert.That(Render("<p>{{Name}} {{ for i in Items }}{{ i }}{{ end }}</p>", data, TemplateFormat.Html),
            Is.EqualTo("<p>&lt;script&gt;alert(1)&lt;/script&gt; a&amp;b</p>"));
        Assert.That(Render("{{Name}}", data), Is.EqualTo("<script>alert(1)</script>"));
    }

    [Test]
    public void Values_are_never_treated_as_template_code()
    {
        Assert.That(Render("{{A}}", new { A = "{{ B }}", B = "secret" }), Is.EqualTo("{{ B }}"));
    }

    [Test]
    public void An_undefined_variable_is_an_error_by_default_and_empty_when_configured()
    {
        Assert.Throws<TemplateRenderException>(() => Render("Hi {{Missing}}", new { Name = "x" }));
        Assert.That(Renderer(MissingTokenBehavior.Empty).Render("Hi {{Missing}}!", TemplateData.Empty), Is.EqualTo("Hi !"));
    }

    [Test]
    public void Templates_cannot_include_files()
    {
        Assert.Throws<TemplateRenderException>(() => Render("{{ include 'secrets.txt' }}", new { }));
    }

    [Test]
    public void Runaway_loops_are_stopped()
    {
        var ex = Assert.Throws<TemplateRenderException>(() => Render("{{ for i in 1..100000 }}x{{ end }}", new { }));

        Assert.That(ex!.Message, Does.Contain("limit").IgnoreCase);
    }

    [Test]
    public void Runaway_recursion_is_stopped()
    {
        Assert.Throws<TemplateRenderException>(() => Render("{{ func f; ret f; end; f }}", new { }));
    }

    [Test]
    public void Huge_output_is_stopped()
    {
        Assert.Throws<TemplateRenderException>(() => Render("{{ for i in 1..900 }}{{ for j in 1..900 }}{{ 'xxxxxxxxxxxx' }}{{ end }}{{ end }}", new { }));
    }

    [Test]
    public void Templates_see_plain_data_only_not_dotnet_members()
    {
        // GetType and ToString exist on every .NET object; the data model exposes properties only, so these render as nothing.
        var data = new { User = new { Name = "x" }, Text = "hello" };

        Assert.That(Render("[{{ User.GetType }}][{{ User.ToString }}][{{ Text.GetType }}][{{ User.Name }}]", data), Is.EqualTo("[][][][x]"));
    }

    [Test]
    public void Invalid_templates_are_reported_without_throwing_other_exceptions()
    {
        var ex = Assert.Throws<TemplateRenderException>(() => Render("{{ if }}", new { }));

        Assert.That(ex!.Message, Does.Contain("not valid"));
    }

    [Test]
    public void Numbers_and_dates_use_the_configured_culture()
    {
        var renderer = new ScribanTemplateRenderer(new TemplateOptions { Culture = new CultureInfo("de-DE") });

        Assert.That(renderer.Render("{{ Price }}", TemplateData.From(new { Price = 1234.5m })), Is.EqualTo("1234,5"));
    }

    [Test]
    public void Data_that_came_back_from_json_works_including_nested_values_and_lists()
    {
        var json = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{"Name":"Ada","Tags":["x","y"],"Order":{"Total":12.5,"Paid":true}}""")!;

        var result = Render("{{ Name }} {{ for t in Tags }}{{ t }}{{ end }} {{ Order.Total }} {{ Order.Paid }}", json);

        Assert.That(result, Is.EqualTo("Ada xy 12.5 true"));
    }

    [Test]
    public void Data_nested_too_deeply_is_rejected()
    {
        object deep = "leaf";
        for (var i = 0; i < 12; i++)
        {
            deep = new Dictionary<string, object?> { ["n"] = deep };
        }

        Assert.Throws<TemplateRenderException>(() => Render("{{ n }}", deep));
    }

    [Test]
    public void The_same_template_renders_concurrently_and_after_the_cache_is_cleared()
    {
        var renderer = new ScribanTemplateRenderer(new TemplateOptions { MaxCachedTemplates = 2 });

        Parallel.For(0, 1000, i =>
        {
            var template = $"{{{{ if N > 0 }}}}v{i % 7}:{{{{ N }}}}{{{{ end }}}}";
            Assert.That(renderer.Render(template, TemplateData.From(new { N = i + 1 })), Is.EqualTo($"v{i % 7}:{i + 1}"));
        });
    }

    [Test]
    public async Task It_replaces_the_built_in_renderer_and_drives_a_real_multi_channel_notification()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddFakeNotifiers().UseScriban())
            .BuildServiceProvider();

        var result = await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To(new Recipient { Email = "ada@example.com", Phone = "+14165551234" })
                .Email("Order {{ Number }}", html: "<ul>{{ for l in Lines }}<li>{{ l }}</li>{{ end }}</ul>", text: "{{ if Total > 100 }}Free shipping{{ end }}")
                .Sms("{{ Lines | array.size }} items")
                .WithData(new { Number = 7, Total = 120, Lines = new[] { "tea & cake", "jam" } }));

        Assert.That(result.Success, Is.True);
        var email = provider.GetRequiredService<FakeEmailNotifier>().Sent.Single();
        Assert.That(email.Subject, Is.EqualTo("Order 7"));
        Assert.That(email.HtmlBody, Is.EqualTo("<ul><li>tea &amp; cake</li><li>jam</li></ul>"));
        Assert.That(email.TextBody, Is.EqualTo("Free shipping"));
        Assert.That(provider.GetRequiredService<ITemplateRenderer>(), Is.InstanceOf<ScribanTemplateRenderer>());
        Assert.That(provider.GetRequiredService<FakeSmsNotifier>().Sent.Single().Body, Is.EqualTo("2 items"));
    }
}
