using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using UniversalNotifier.NET.Receipts;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class ReceiptEndpointTests
{
    private const string MailgunSignature = "45b8d354e238e6cbc36e94643e634bc0f6b73b8ed11d50a0cbe90387a5da32c3";

    private sealed class Collector : IDeliveryReceiptHandler
    {
        public List<DeliveryEvent> Events { get; } = [];

        public ValueTask HandleAsync(DeliveryEvent deliveryEvent, CancellationToken cancellationToken)
        {
            lock (Events)
            {
                Events.Add(deliveryEvent);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class Throwing : IDeliveryReceiptHandler
    {
        public ValueTask HandleAsync(DeliveryEvent deliveryEvent, CancellationToken cancellationToken) => throw new InvalidOperationException("database down");
    }

    private static async Task<(WebApplication App, HttpClient Client, Collector Collector)> StartAsync(IDeliveryWebhook webhook, string pattern, bool throwing = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var collector = new Collector();
        builder.Services.AddSingleton<IDeliveryReceiptHandler>(collector);
        if (throwing)
        {
            builder.Services.AddSingleton<IDeliveryReceiptHandler, Throwing>();
        }

        var app = builder.Build();
        app.MapDeliveryReceipts(pattern, webhook);
        await app.StartAsync();
        return (app, app.GetTestClient(), collector);
    }

    private static string MailgunBody(string signature = MailgunSignature) =>
        "{\"signature\":{\"timestamp\":\"1700000000\",\"token\":\"abcdef\",\"signature\":\"" + signature + "\"},\"event-data\":{\"event\":\"delivered\",\"recipient\":\"ada@example.com\",\"message\":{\"headers\":{\"message-id\":\"m1@mg.example.com\"}}}}";

    private static MailgunDeliveryWebhook Mailgun() =>
        new("key-secret", new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)));

    [Test]
    public async Task A_verified_call_answers_200_and_reaches_every_handler()
    {
        var (app, client, collector) = await StartAsync(Mailgun(), "/hooks/mailgun");
        await using var _ = app;

        var response = await client.PostAsync("/hooks/mailgun", new StringContent(MailgunBody(), Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var e = collector.Events.Single();
        Assert.That(e.Outcome, Is.EqualTo(DeliveryOutcome.Delivered));
        Assert.That(e.ProviderMessageId, Is.EqualTo("<m1@mg.example.com>"));
    }

    [Test]
    public async Task A_call_with_a_wrong_signature_answers_401_and_reaches_no_handler()
    {
        var (app, client, collector) = await StartAsync(Mailgun(), "/hooks/mailgun");
        await using var _ = app;

        var response = await client.PostAsync("/hooks/mailgun", new StringContent(MailgunBody(new string('0', 64)), Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(collector.Events, Is.Empty);
    }

    [Test]
    public async Task An_oversized_body_answers_413_before_anything_is_verified()
    {
        var (app, client, collector) = await StartAsync(Mailgun(), "/hooks/mailgun");
        await using var _ = app;

        var response = await client.PostAsync("/hooks/mailgun", new StringContent(new string('x', (1 << 20) + 1), Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(collector.Events, Is.Empty);
    }

    [Test]
    public async Task A_handler_that_throws_is_not_swallowed_so_the_host_answers_500_and_the_provider_retries()
    {
        var (app, client, _) = await StartAsync(Mailgun(), "/hooks/mailgun", throwing: true);
        await using var __ = app;

        // The test server re-throws what Kestrel would turn into a 500 response.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.PostAsync("/hooks/mailgun", new StringContent(MailgunBody(), Encoding.UTF8, "application/json")));

        Assert.That(thrown!.Message, Is.EqualTo("database down"));
    }

    [Test]
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Twilio signs callbacks with HMAC-SHA1; the test signs the same way.")]
    public async Task The_signed_url_is_the_real_request_url_including_its_query()
    {
        var (app, client, collector) = await StartAsync(new TwilioDeliveryWebhook("token"), "/hooks/twilio");
        await using var _ = app;
        const string Form = "MessageSid=SM1&MessageStatus=delivered&To=%2B14165551234";
        var url = new Uri(client.BaseAddress!, "/hooks/twilio?tenant=7").AbsoluteUri;
        var signed = url + "MessageSidSM1MessageStatusdeliveredTo+14165551234";
        var signature = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes("token"), Encoding.UTF8.GetBytes(signed)));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/hooks/twilio?tenant=7")
        {
            Content = new StringContent(Form, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.Add("X-Twilio-Signature", signature);
        var good = await client.SendAsync(request);

        using var wrongUrl = new HttpRequestMessage(HttpMethod.Post, "/hooks/twilio?tenant=8")
        {
            Content = new StringContent(Form, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        wrongUrl.Headers.Add("X-Twilio-Signature", signature);
        var bad = await client.SendAsync(wrongUrl);

        Assert.That(good.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(collector.Events.Single().ProviderMessageId, Is.EqualTo("SM1"));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "the same signature on a different URL must fail");
    }

    [Test]
    public void The_endpoint_validates_its_arguments()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapDeliveryReceipts(" ", Mailgun()));
        Assert.Throws<ArgumentNullException>(() => app.MapDeliveryReceipts("/h", null!));
    }
}
