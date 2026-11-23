using System.Globalization;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class TokenTemplateRendererTests
{
    private static TokenTemplateRenderer Renderer(MissingTokenBehavior missing = MissingTokenBehavior.Throw) =>
        new(new TemplateOptions { MissingToken = missing });

    [Test]
    public void Replaces_tokens_from_an_object()
    {
        var result = Renderer().Render("Hello {{Name}}, order {{ Order }}!", TemplateData.From(new { Name = "John", Order = "A1" }));

        Assert.That(result, Is.EqualTo("Hello John, order A1!"));
    }

    [Test]
    public void Replaces_tokens_from_a_dictionary_case_insensitively_for_objects_and_exactly_for_dictionaries()
    {
        var data = TemplateData.From(new Dictionary<string, object?> { ["Name"] = "Ada" });

        Assert.That(Renderer().Render("{{Name}}", data), Is.EqualTo("Ada"));
        Assert.That(Renderer().Render("{{NAME}}", TemplateData.From(new { name = "Bob" })), Is.EqualTo("Bob"));
    }

    [Test]
    public void Supports_string_valued_dictionaries()
    {
        var data = TemplateData.From(new Dictionary<string, string> { ["Code"] = "123456" });

        Assert.That(Renderer().Render("Code: {{Code}}", data), Is.EqualTo("Code: 123456"));
    }

    [Test]
    public void Supports_value_typed_dictionaries()
    {
        var data = TemplateData.From(new Dictionary<string, int> { ["Count"] = 3 });

        Assert.That(Renderer().Render("{{Count}} items", data), Is.EqualTo("3 items"));
    }

    [Test]
    public void Supports_nested_properties()
    {
        var data = TemplateData.From(new { User = new { Name = "Ada", Address = new { City = "London" } } });

        Assert.That(Renderer().Render("{{User.Name}} from {{User.Address.City}}", data), Is.EqualTo("Ada from London"));
    }

    [Test]
    public void Html_format_encodes_values_but_not_the_template()
    {
        var data = TemplateData.From(new { Name = "<script>alert(1)</script>" });

        var result = Renderer().Render("<b>{{Name}}</b>", data, TemplateFormat.Html);

        Assert.That(result, Is.EqualTo("<b>&lt;script&gt;alert(1)&lt;/script&gt;</b>"));
    }

    [Test]
    public void Triple_braces_insert_raw_html()
    {
        var data = TemplateData.From(new { Snippet = "<i>ok</i>" });

        Assert.That(Renderer().Render("{{{Snippet}}}", data, TemplateFormat.Html), Is.EqualTo("<i>ok</i>"));
    }

    [Test]
    public void Text_format_does_not_encode()
    {
        var data = TemplateData.From(new { Name = "a < b" });

        Assert.That(Renderer().Render("{{Name}}", data), Is.EqualTo("a < b"));
    }

    [Test]
    public void Values_are_never_scanned_for_tokens()
    {
        var data = TemplateData.From(new { A = "{{B}}", B = "secret" });

        Assert.That(Renderer().Render("{{A}}", data), Is.EqualTo("{{B}}"));
    }

    [Test]
    public void Missing_token_throws_by_default()
    {
        var ex = Assert.Throws<TemplateRenderException>(() => Renderer().Render("Hi {{Name}}", TemplateData.Empty))!;

        Assert.That(ex.Token, Is.EqualTo("Name"));
    }

    [Test]
    public void Missing_token_can_be_kept_or_emptied()
    {
        Assert.That(Renderer(MissingTokenBehavior.KeepToken).Render("Hi {{Name}}!", TemplateData.Empty), Is.EqualTo("Hi {{Name}}!"));
        Assert.That(Renderer(MissingTokenBehavior.Empty).Render("Hi {{Name}}!", TemplateData.Empty), Is.EqualTo("Hi !"));
    }

    [Test]
    public void A_present_null_value_renders_empty_and_is_not_missing()
    {
        var data = TemplateData.From(new Dictionary<string, object?> { ["Name"] = null });

        Assert.That(Renderer().Render("[{{Name}}]", data), Is.EqualTo("[]"));
    }

    [Test]
    public void A_null_intermediate_value_counts_as_missing()
    {
        var data = TemplateData.From(new Dictionary<string, object?> { ["User"] = null });

        Assert.Throws<TemplateRenderException>(() => Renderer().Render("{{User.Name}}", data));
    }

    [Test]
    public void Text_without_tokens_is_returned_unchanged_and_without_allocating()
    {
        const string text = "No tokens here { just braces }";

        Assert.That(Renderer().Render(text, TemplateData.Empty), Is.SameAs(text));
    }

    [Test]
    public void Css_and_unterminated_braces_stay_literal()
    {
        const string template = "p {{ color: red }} and {{ broken";

        Assert.That(Renderer().Render(template, TemplateData.Empty), Is.EqualTo(template));
    }

    [Test]
    public void Numbers_and_dates_use_the_configured_culture()
    {
        var renderer = new TokenTemplateRenderer(new TemplateOptions { Culture = new CultureInfo("de-DE") });

        var result = renderer.Render("{{Price}}", TemplateData.From(new { Price = 1234.5m }));

        Assert.That(result, Is.EqualTo("1234,5"));
    }

    [Test]
    public void The_same_template_renders_repeatedly_and_after_the_cache_is_cleared()
    {
        var renderer = new TokenTemplateRenderer(new TemplateOptions { MaxCachedTemplates = 1 });

        for (var i = 0; i < 5; i++)
        {
            Assert.That(renderer.Render("a{{X}}", TemplateData.From(new { X = i })), Is.EqualTo($"a{i}"));
            Assert.That(renderer.Render("b{{X}}", TemplateData.From(new { X = i })), Is.EqualTo($"b{i}"));
        }
    }

    [Test]
    public void Concurrent_rendering_is_safe()
    {
        var renderer = Renderer();

        Parallel.For(0, 2000, i =>
        {
            var result = renderer.Render("Hello {{Name}} #{{N}}", TemplateData.From(new { Name = "x", N = i }));
            Assert.That(result, Is.EqualTo($"Hello x #{i}"));
        });
    }
}
