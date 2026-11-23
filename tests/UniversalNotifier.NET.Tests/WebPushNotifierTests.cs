using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET.WebPush;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class WebPushNotifierTests
{
    private static readonly VapidKeys Keys = VapidKeys.Generate();

    private static WebPushOptions Options(Action<WebPushOptions>? configure = null)
    {
        var options = new WebPushOptions { PublicKey = Keys.PublicKey, PrivateKey = Keys.PrivateKey, Subject = "mailto:ops@example.com" };
        configure?.Invoke(options);
        return options;
    }

    private static WebPushNotifier Create(StubHttpHandler handler, Action<WebPushOptions>? configure = null) =>
        new(Options(configure), new NotifierContext(), handler);

    // A real browser subscription: a P-256 public key and a 16-byte auth secret, both base64url.
    internal static WebPushSubscription Subscription(string endpoint = "https://push.example.com/send/abc")
    {
        using var browserKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = browserKey.ExportParameters(false).Q;
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        q.X!.CopyTo(publicKey, 1);
        q.Y!.CopyTo(publicKey, 33);
        return new WebPushSubscription(endpoint, Base64Url.Encode(publicKey), Base64Url.Encode(RandomNumberGenerator.GetBytes(16)));
    }

    private static WebPushMessage Push(params WebPushSubscription[] subscriptions) =>
        new() { Subscriptions = subscriptions, Title = "Hello", Body = "World" };

    [Test]
    public async Task Pushes_an_encrypted_vapid_signed_request_to_the_subscription_endpoint()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created);

        var response = await Create(handler).SendAsync(Push(Subscription()) with
        {
            TimeToLive = TimeSpan.FromMinutes(10),
            Urgency = WebPushUrgency.High,
            Topic = "news",
        });

        Assert.That(response.Success, Is.True, string.Join(';', response.Errors));
        var request = handler.OnlyRequest;
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(request.Uri.ToString(), Is.EqualTo("https://push.example.com/send/abc"));
        Assert.That(request.Headers["Authorization"], Does.StartWith("vapid t=").And.Contain("k=" + Keys.PublicKey));
        Assert.That(request.Headers["TTL"], Is.EqualTo("600"));
        Assert.That(request.Headers["Urgency"], Is.EqualTo("high"));
        Assert.That(request.Headers["Topic"], Is.EqualTo("news"));
        Assert.That(request.Body, Does.Not.Contain("Hello"), "the payload must be encrypted");
    }

    [Test]
    public async Task Each_subscription_gets_its_own_push_and_result()
    {
        var handler = new StubHttpHandler(r => new HttpResponseMessage(
            r.Uri.AbsolutePath.EndsWith("/gone", StringComparison.Ordinal) ? HttpStatusCode.Gone : HttpStatusCode.Created));

        var response = await Create(handler).SendAsync(Push(
            Subscription("https://push.example.com/a"),
            Subscription("https://push.example.com/gone"),
            Subscription("https://push.example.com/c")));

        Assert.That(handler.Requests, Has.Count.EqualTo(3));
        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        var gone = response.Recipients.Single(r => !r.Accepted);
        Assert.That(gone.Recipient, Is.EqualTo("https://push.example.com/gone"));
        Assert.That(gone.Error!.Value.Code, Is.EqualTo("webpush.gone"));
        Assert.That(gone.Error.Value.Kind, Is.EqualTo(NotifierErrorKind.Permanent));
    }

    [TestCase(HttpStatusCode.NotFound, NotifierErrorKind.Permanent, "webpush.gone")]
    [TestCase(HttpStatusCode.Gone, NotifierErrorKind.Permanent, "webpush.gone")]
    [TestCase(HttpStatusCode.Unauthorized, NotifierErrorKind.Configuration, "webpush.unauthorized")]
    [TestCase(HttpStatusCode.Forbidden, NotifierErrorKind.Configuration, "webpush.unauthorized")]
    [TestCase(HttpStatusCode.RequestEntityTooLarge, NotifierErrorKind.Permanent, "webpush.payload_too_large")]
    [TestCase(HttpStatusCode.TooManyRequests, NotifierErrorKind.Transient, "webpush.rate_limited")]
    [TestCase(HttpStatusCode.BadRequest, NotifierErrorKind.Permanent, "webpush.rejected")]
    [TestCase(HttpStatusCode.InternalServerError, NotifierErrorKind.Unknown, "webpush.error")]
    public async Task Push_service_errors_are_classified(HttpStatusCode status, NotifierErrorKind kind, string code)
    {
        var response = await Create(new StubHttpHandler(status)).SendAsync(Push(Subscription()));

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors[0].Kind, Is.EqualTo(kind));
        Assert.That(response.Errors[0].Code, Is.EqualTo(code));
    }

    [Test]
    public async Task The_retry_after_header_is_reported_and_the_client_does_not_retry_on_its_own()
    {
        var handler = new StubHttpHandler(HttpStatusCode.TooManyRequests, "", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2)));

        var response = await Create(handler).SendAsync(Push(Subscription()));

        Assert.That(response.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(2)));
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task A_network_failure_is_a_failed_result_not_an_exception()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));

        var response = await Create(handler).SendAsync(Push(Subscription()));

        Assert.That(response.Success, Is.False);
        Assert.That(response.Recipients[0].Error, Is.Not.Null);
    }

    [Test]
    public async Task An_oversized_payload_is_rejected_before_any_request()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created);

        var response = await Create(handler).SendAsync(Push(Subscription()) with { Body = new string('x', 5000) });

        Assert.That(response.Errors[0].Code, Is.EqualTo("webpush.payload_too_large"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task Invalid_messages_never_reach_the_network()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created);

        var response = await Create(handler).SendAsync(new WebPushMessage());

        Assert.That(response.Success, Is.False);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public void The_payload_is_json_for_the_service_worker_unless_a_raw_payload_is_given()
    {
        var json = WebPushNotifier.BuildPayload(new WebPushMessage
        {
            Title = "T",
            Body = "B",
            Data = new Dictionary<string, string> { ["url"] = "/orders/42" },
        });

        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty("title").GetString(), Is.EqualTo("T"));
        Assert.That(document.RootElement.GetProperty("body").GetString(), Is.EqualTo("B"));
        Assert.That(document.RootElement.GetProperty("data").GetProperty("url").GetString(), Is.EqualTo("/orders/42"));
        Assert.That(WebPushNotifier.BuildPayload(new WebPushMessage { Payload = "raw", Title = "ignored" }), Is.EqualTo("raw"));
        Assert.That(WebPushNotifier.BuildPayload(new WebPushMessage { Title = "only" }), Is.EqualTo("""{"title":"only"}"""));
    }

    [Test]
    public void Generated_vapid_keys_have_the_right_shape_and_work_as_options()
    {
        var keys = VapidKeys.Generate();

        Assert.That(Base64Url.TryDecode(keys.PublicKey, out var publicKey) && publicKey.Length == 65 && publicKey[0] == 0x04, Is.True);
        Assert.That(Base64Url.TryDecode(keys.PrivateKey, out var privateKey) && privateKey.Length == 32, Is.True);
        Assert.That(keys.PublicKey, Does.Not.Contain("+").And.Not.Contain("/").And.Not.Contain("="));
        Assert.That(keys.ToString(), Does.Not.Contain(keys.PrivateKey));
        Assert.That(VapidKeys.Generate().PrivateKey, Is.Not.EqualTo(keys.PrivateKey));
    }

    [Test]
    public void Invalid_options_are_rejected()
    {
        var bad = new[]
        {
            Options(o => o.PublicKey = "nope"),
            Options(o => o.PrivateKey = ""),
            Options(o => o.Subject = "ops@example.com"),
            Options(o => o.MaxConcurrency = 0),
            Options(o => o.Timeout = TimeSpan.Zero),
        };

        foreach (var options in bad)
        {
            Assert.Throws<ArgumentException>(() => new WebPushNotifier(options, new NotifierContext()));
        }
    }

    [Test]
    public void Options_never_print_the_private_key() =>
        Assert.That(Options().ToString(), Does.Not.Contain(Keys.PrivateKey));

    [Test]
    public async Task It_registers_through_dependency_injection_and_works_with_the_dispatcher()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddWebPush(o =>
            {
                o.PublicKey = Keys.PublicKey;
                o.PrivateKey = Keys.PrivateKey;
                o.Subject = "mailto:ops@example.com";
            }))
            .BuildServiceProvider();

        Assert.That(provider.GetRequiredService<IWebPushNotifier>(), Is.InstanceOf<WebPushNotifier>());
        Assert.That(provider.GetRequiredKeyedService<IWebPushNotifier>("webpush"), Is.SameAs(provider.GetRequiredService<IWebPushNotifier>()));
        await Task.CompletedTask;
    }

    [Test]
    public void Invalid_settings_are_reported_by_the_options_system()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddWebPush())
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IWebPushNotifier>());
    }
}
