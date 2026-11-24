using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET.Twilio;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class TwilioNotifierTests
{
    private const string AccountSid = "AC00000000000000000000000000000000";
    private const string ServiceSid = "MG00000000000000000000000000000000";
    private const string Created = """{"sid":"SM123","status":"queued"}""";

    private static TwilioOptions Options(Action<TwilioOptions>? configure = null)
    {
        var options = new TwilioOptions { AccountSid = AccountSid, AuthToken = "token-secret", From = "+15005550006" };
        configure?.Invoke(options);
        return options;
    }

    private static TwilioSmsNotifier Create(StubHttpHandler handler, Action<TwilioOptions>? configure = null) =>
        new(Options(configure), new HttpClient(handler), new NotifierContext());

    private static SmsMessage Sms(params string[] to) => new() { To = [.. to.Select(t => new PhoneNumber(t))], Body = "Hello & welcome" };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Test]
    public async Task Posts_a_form_to_the_account_messages_endpoint_with_basic_auth()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created, Created);

        var response = await Create(handler).SendAsync(Sms("+14165551234"));

        Assert.That(response.Success, Is.True);
        Assert.That(response.ProviderMessageId, Is.EqualTo("SM123"));
        Assert.That(response.HttpStatusCode, Is.EqualTo(201));

        var request = handler.OnlyRequest;
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(request.Uri.ToString(), Is.EqualTo($"https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Messages.json"));
        Assert.That(request.Headers["Authorization"], Is.EqualTo("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AccountSid}:token-secret"))));
        Assert.That(request.Body, Is.EqualTo("To=%2B14165551234&From=%2B15005550006&Body=Hello+%26+welcome"));
    }

    [Test]
    public async Task An_api_key_is_used_for_auth_while_the_url_keeps_the_account_sid()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created, Created);

        await Create(handler, o => { o.AuthToken = null; o.ApiKeySid = "SKkey"; o.ApiKeySecret = "secret"; }).SendAsync(Sms("+14165551234"));

        Assert.That(handler.OnlyRequest.Headers["Authorization"], Is.EqualTo("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("SKkey:secret"))));
        Assert.That(handler.OnlyRequest.Uri.AbsolutePath, Does.Contain(AccountSid));
    }

    [Test]
    public async Task A_messaging_service_sid_is_sent_as_such_and_a_message_can_override_the_sender()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created, Created);
        var notifier = Create(handler, o => o.From = ServiceSid);

        await notifier.SendAsync(Sms("+14165551234"));
        await notifier.SendAsync(Sms("+14165551234") with { From = "MyBrand" });

        Assert.That(handler.Requests[0].Body, Does.Contain("MessagingServiceSid=" + ServiceSid).And.Not.Contain("From="));
        Assert.That(handler.Requests[1].Body, Does.Contain("From=MyBrand").And.Not.Contain("MessagingServiceSid"));
    }

    [Test]
    public async Task A_regional_base_address_is_used()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created, Created);

        await Create(handler, o => o.BaseAddress = new Uri("https://api.dublin.ie1.twilio.com")).SendAsync(Sms("+14165551234"));

        Assert.That(handler.OnlyRequest.Uri.Host, Is.EqualTo("api.dublin.ie1.twilio.com"));
    }

    [Test]
    public async Task Each_recipient_gets_its_own_request_and_result()
    {
        var handler = new StubHttpHandler(r => Json(
            r.Body.Contains("%2B14165550002", StringComparison.Ordinal) ? HttpStatusCode.BadRequest : HttpStatusCode.Created,
            r.Body.Contains("%2B14165550002", StringComparison.Ordinal)
                ? """{"code":21211,"message":"The 'To' number +14165550002 is not a valid phone number.","more_info":"https://www.twilio.com/docs/errors/21211","status":400}"""
                : Created));

        var response = await Create(handler).SendAsync(Sms("+14165550001", "+14165550002", "+14165550003"));

        Assert.That(handler.Requests, Has.Count.EqualTo(3));
        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        Assert.That(response.Recipients.Select(r => r.Recipient), Is.EqualTo(new[] { "+14165550001", "+14165550002", "+14165550003" }));
        var failed = response.Recipients.Single(r => !r.Accepted);
        Assert.That(failed.Recipient, Is.EqualTo("+14165550002"));
        Assert.That(failed.Error!.Value.Kind, Is.EqualTo(NotifierErrorKind.Permanent));
        Assert.That(failed.Error.Value.Message, Does.Contain("21211").And.Not.Contain("+1416555"));
    }

    [Test]
    public async Task Parallelism_is_bounded_by_the_configured_limit()
    {
        var inFlight = 0;
        var peak = 0;
        var handler = new StubHttpHandler(_ =>
        {
            var now = Interlocked.Increment(ref inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
                // Retry until the peak is recorded.
            }

            Thread.Sleep(20);
            Interlocked.Decrement(ref inFlight);
            return Json(HttpStatusCode.Created, Created);
        });
        var numbers = Enumerable.Range(0, 24).Select(i => $"+1416555{i:0000}").ToArray();

        var response = await Create(handler, o => o.MaxConcurrency = 3).SendAsync(Sms(numbers));

        Assert.That(response.Success, Is.True);
        Assert.That(handler.Requests, Has.Count.EqualTo(24));
        Assert.That(peak, Is.LessThanOrEqualTo(3));
    }

    [TestCase(HttpStatusCode.Unauthorized, NotifierErrorKind.Configuration, "twilio.unauthorized")]
    [TestCase(HttpStatusCode.TooManyRequests, NotifierErrorKind.Transient, "twilio.rate_limited")]
    [TestCase(HttpStatusCode.BadRequest, NotifierErrorKind.Permanent, "twilio.rejected")]
    [TestCase(HttpStatusCode.ServiceUnavailable, NotifierErrorKind.Transient, "twilio.rejected")]
    [TestCase(HttpStatusCode.InternalServerError, NotifierErrorKind.Unknown, "twilio.rejected")]
    public async Task Http_errors_are_classified(HttpStatusCode status, NotifierErrorKind kind, string code)
    {
        var response = await Create(new StubHttpHandler(status, """{"code":20003,"message":"x","status":1}""")).SendAsync(Sms("+14165551234"));

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors[0].Kind, Is.EqualTo(kind));
        Assert.That(response.Errors[0].Code, Is.EqualTo(code));
        Assert.That(response.Errors[0].Message, Does.Contain("20003"));
    }

    [Test]
    public async Task Empty_or_non_json_error_bodies_do_not_throw()
    {
        foreach (var body in new[] { "", "<html/>", "[]", "{\"code\":\"not-a-number\"}" })
        {
            var response = await Create(new StubHttpHandler(HttpStatusCode.BadGateway, body)).SendAsync(Sms("+14165551234"));

            Assert.That(response.Success, Is.False, body);
        }
    }

    [Test]
    public async Task Retry_after_is_reported()
    {
        var handler = new StubHttpHandler(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3)));

        var response = await Create(handler).SendAsync(Sms("+14165551234"));

        Assert.That(response.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(3)));
    }

    [Test]
    public async Task A_network_failure_for_one_recipient_is_reported_for_that_recipient_only()
    {
        var calls = 0;
        var handler = new StubHttpHandler(r => Interlocked.Increment(ref calls) == 2
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")
            : Json(HttpStatusCode.Created, Created));

        var response = await Create(handler, o => o.MaxConcurrency = 1).SendAsync(Sms("+14165550001", "+14165550002", "+14165550003"));

        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        Assert.That(response.Recipients.Count(r => r.Accepted), Is.EqualTo(2));
    }

    [Test]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHttpHandler(_ =>
        {
            cts.Cancel();
            return Json(HttpStatusCode.Created, Created);
        });

        await Assert.CatchAsync<OperationCanceledException>(async () =>
            await Create(handler, o => o.MaxConcurrency = 1).SendAsync(Sms("+14165550001", "+14165550002"), cts.Token));
    }

    [Test]
    public async Task Invalid_messages_never_reach_the_network()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Created, Created);

        var response = await Create(handler).SendAsync(Sms("4165551234"));

        Assert.That(response.Success, Is.False);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public void Invalid_options_are_rejected()
    {
        var bad = new[]
        {
            Options(o => o.AccountSid = "nope"),
            Options(o => o.AuthToken = null),
            Options(o => o.From = ""),
            Options(o => o.BaseAddress = new Uri("http://api.twilio.com")),
            Options(o => o.MaxConcurrency = 0),
        };

        foreach (var options in bad)
        {
            Assert.Throws<ArgumentException>(() => new TwilioSmsNotifier(options, new HttpClient(), new NotifierContext()));
        }
    }

    [Test]
    public void Options_never_print_secrets() =>
        Assert.That(Options().ToString(), Does.Not.Contain("token-secret"));

    [Test]
    public async Task It_registers_through_dependency_injection_and_works_with_the_dispatcher()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddTwilio(o =>
            {
                o.AccountSid = AccountSid;
                o.AuthToken = "t";
                o.From = "+15005550006";
            }))
            .BuildServiceProvider();

        Assert.That(provider.GetRequiredService<ISmsNotifier>(), Is.InstanceOf<TwilioSmsNotifier>());
        Assert.That(provider.GetRequiredKeyedService<ISmsNotifier>("twilio"), Is.SameAs(provider.GetRequiredService<ISmsNotifier>()));
        await Task.CompletedTask;
    }

    [Test]
    public void Invalid_settings_are_reported_by_the_options_system()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddTwilio(o => o.AccountSid = AccountSid))
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<ISmsNotifier>());
    }
}
