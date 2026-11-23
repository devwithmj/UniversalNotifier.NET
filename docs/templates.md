# Templates

A template is the text of a notification with placeholders. You can pass it inline, register it by name, or load it from files or from the assembly.

## Inline

```csharp
Notification.To(user)
    .Email("Welcome, {{Name}}!", html: "<p>Hello <b>{{Name}}</b></p>", text: "Hello {{Name}}")
    .Sms("Hi {{Name}}")
    .WithData(new { Name = "Ada" });
```

Every part (subject, title, text, HTML and the values of data entries) is rendered with the data. Text without placeholders is returned as it is.

## The built-in syntax

| You write | You get |
|---|---|
| `{{Name}}` | the value, HTML-encoded in HTML parts |
| `{{User.Address.City}}` | nested values |
| `{{{Snippet}}}` | the value raw (for HTML you trust) |
| `{{ color: red }}` or an unclosed `{{` | left as text (CSS and JavaScript braces are safe) |

Values are written in a single pass and never scanned for placeholders, so data cannot inject new ones. A missing value is an error by default (`MissingTokenBehavior.Throw`), so a half-filled email never goes out; set `KeepToken` or `Empty` to be lenient. Numbers and dates use `TemplateOptions.Culture` (invariant by default). A boolean renders as `true`/`false`.

Prefer dictionaries (`WithData(new Dictionary<string, object?> { ... })`) in hot paths; objects are read by reflection once per type.

## Named templates, per channel

```csharp
n.AddTemplate("welcome", NotificationChannel.Email, new ChannelContent { Subject = "Welcome, {{Name}}!", Html = "<p>...</p>", Text = "..." });
n.AddTemplate("welcome", NotificationChannel.Sms, new ChannelContent { Text = "Welcome, {{Name}}!" });

Notification.To(user).Email().Sms().UseTemplate("welcome").WithData(new { Name = "Ada" });
```

Inline content wins over the template for the parts it sets, so you can override just the subject: `.Email(subject: "Override").UseTemplate("welcome")`. A channel the template does not define fails with `<channel>.content.missing` and does not block the others.

## Files

```
templates/
  welcome/
    email.subject.txt
    email.html
    email.text.txt
    sms.text.txt
    push.title.txt
    push.text.txt
```

```csharp
n.AddTemplatesFromDirectory("templates");
```

Names become path segments, so only letters, digits, `-`, `_` and `.` are accepted (no `..`, no absolute paths).

## Embedded in your assembly

```xml
<EmbeddedResource Include="Templates\**\*.*" />
```

```csharp
n.AddEmbeddedTemplates(typeof(Program).Assembly, "MyApp.Templates");   // root namespace + folder
```

MSBuild writes `order-shipped` as `order_shipped` in the resource name; lookups treat `-` and `_` alike.

## Richer engines

```csharp
n.UseScriban();   // UniversalNotifier.NET.Templates.Scriban
n.UseFluid();     // UniversalNotifier.NET.Templates.Fluid (Liquid syntax)
n.UseHandlebars(); // UniversalNotifier.NET.Templates.Handlebars
```

```
{{ if Total > 100 }}Free shipping{{ end }}
{{ for line in Lines }}<li>{{ line.Sku }} x {{ line.Qty }}</li>{{ end }}
```

Handlebars (`{{#if IsLarge}}Free shipping{{/if}}`, `{{#each Lines}}<li>{{Sku}} x {{Qty}}</li>{{/each}}`) has no comparison operators, so compute flags like `IsLarge` in your data. It disables partials and files, and HTML-encodes every string value in HTML parts, so `{{{raw}}}` does not bypass the encoding. Handlebars.Net has no step or output limits, but without partials a template can only loop over the data you pass.

Scriban and Fluid limit execution (loops, recursion, output size), have no `include`, give templates plain data (no .NET method calls), HTML-encode string values in HTML parts and treat an undefined variable as an error by default. Names are case-sensitive in the engines. To plug in anything else, implement `ITemplateRenderer` (see `samples/CustomExtensions`); for a database, implement `ITemplateStore`.

## Check templates at startup

The built-in renderer accepts any text, but a syntax error in a Scriban or Fluid template would otherwise show up the first time that notification is sent:

```csharp
TemplateValidator.ThrowIfInvalid(app.Services);   // in a host
// NotifierBuilder.Build() already does this
```

It checks every template the in-memory, directory and embedded stores hold.
