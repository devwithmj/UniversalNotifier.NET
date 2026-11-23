// Shows the two extension points: writing your own provider for an existing channel, and your own template renderer.
// Runs offline: the provider talks to a stand-in HTTP handler instead of a real SMS gateway.

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;

var services = new ServiceCollection();
services.AddUniversalNotifier(n => n
    .AddAcmeSms(o => o.ApiKey = "demo-key")
    .UseTemplateRenderer<ShoutingRenderer>());

await using var provider = services.BuildServiceProvider();

var result = await provider.GetRequiredService<INotificationService>().SendAsync(
    Notification.To("+14165551234").Sms("Your code is {{Code}}").WithData(new { Code = 4242 }));

var sms = result[NotificationChannel.Sms]!;
Console.WriteLine($"Accepted: {sms.Success}, provider id: {sms.ProviderMessageId}");

// A provider for an existing channel: derive from NotifierBase, implement one method, report outcomes honestly.
public sealed class AcmeSmsNotifier(AcmeOptions options, HttpClient http, NotifierContext context)
    : NotifierBase<SmsMessage>(context), ISmsNotifier
{
    protected override string ProviderName => "acme";

    protected override async Task<NotifierResponse> SendCoreAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.acme.example/sms")
        {
            Content = JsonContent.Create(new { to = message.To[0].Value, text = message.Body }),
        };
        request.Headers.Add("X-Api-Key", options.ApiKey);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return NotifierResponse.Accepted((await response.Content.ReadAsStringAsync(cancellationToken)).Trim('"'), (int)response.StatusCode);
        }

        // Say whether repeating is safe: 429 is Transient, other client errors are Permanent.
        var kind = (int)response.StatusCode == 429 ? NotifierErrorKind.Transient : NotifierErrorKind.Permanent;
        return NotifierResponse.Failed(new NotifierError(kind, "acme.rejected", $"Acme returned HTTP {(int)response.StatusCode}."), (int)response.StatusCode);
    }
}

public sealed class AcmeOptions
{
    public string ApiKey { get; set; } = string.Empty;
}

public static class AcmeBuilderExtensions
{
    public static INotifierBuilder AddAcmeSms(this INotifierBuilder builder, Action<AcmeOptions> configure)
    {
        builder.Services.AddOptions<AcmeOptions>("acme").Configure(configure);

        // The factory is called once; the unnamed registration becomes the default SMS provider.
        return builder.AddNotifier<ISmsNotifier>(
            "acme",
            name: null,
            (services, key) => new AcmeSmsNotifier(
                services.GetRequiredService<IOptionsMonitor<AcmeOptions>>().Get(key),
                new HttpClient(new StandInGateway()),
                services.GetRequiredService<NotifierContext>()));
    }
}

// A custom renderer: anything that turns a template and data into text. Here, upper-cases the template and fills tokens.
public sealed class ShoutingRenderer : ITemplateRenderer
{
    private readonly TokenTemplateRenderer _inner = new();

    public string Render(string templateText, TemplateData data, TemplateFormat format = TemplateFormat.Text) =>
        _inner.Render(templateText, data, format).ToUpperInvariant();
}

// Stands in for the real gateway so the sample runs offline.
internal sealed class StandInGateway : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("\"acme-msg-001\"") });
}
