using System.Net;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;
using UniversalNotifier.NET.Email.Mailgun;
using UniversalNotifier.NET.Email.SendGrid;
using UniversalNotifier.NET.Email.Smtp;
using UniversalNotifier.NET.Push.Fcm;
using UniversalNotifier.NET.Twilio;
using UniversalNotifier.NET.Telegram;
using UniversalNotifier.NET.Webhook;
using UniversalNotifier.NET.WebPush;

namespace UniversalNotifier.NET.Tests;

/// <summary>What the provider's backend does in a contract test.</summary>
public enum ContractScenario
{
    /// <summary>Accepts the message.</summary>
    Accepts,

    /// <summary>Refuses it for good (a client error such as 400 or an SMTP 550).</summary>
    RejectsPermanently,

    /// <summary>Refuses it for now, safe to repeat (429, 503, SMTP 4xx).</summary>
    RejectsTransiently,

    /// <summary>Cannot be reached.</summary>
    NetworkDown,
}

/// <summary>
/// The behaviors every provider of a channel must show, whatever the vendor. Each official provider has a small
/// fixture below that supplies a notifier wired to a stub backend; all the assertions live here, once.
/// To check a new provider against the contract, add a fixture.
/// </summary>
public abstract class ProviderContractTests<TMessage> where TMessage : NotifierMessage
{
    private CapturingLoggerProvider _logs = null!;
    private ILoggerFactory _loggerFactory = null!;

    /// <summary>A context whose logs are captured.</summary>
    protected NotifierContext Context { get; private set; } = null!;

    protected abstract TMessage Valid { get; }

    protected abstract TMessage Invalid { get; }

    /// <summary>Credentials that must never appear in an error, a log line or a result.</summary>
    protected abstract IReadOnlyList<string> Secrets { get; }

    /// <summary>The recipient's identifying text (address, number, token...) that must never appear in an error or a log line.</summary>
    protected abstract IReadOnlyList<string> PersonalData { get; }

    /// <summary>How many calls reached the backend since the notifier was created.</summary>
    protected abstract int BackendCalls { get; }

    protected abstract INotifier<TMessage> Create(ContractScenario scenario);

    [SetUp]
    public void CreateContext()
    {
        _logs = new CapturingLoggerProvider();
        _loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(_logs));
        Context = new NotifierContext(loggerFactory: _loggerFactory);
    }

    [TearDown]
    public void CleanUp()
    {
        OnCleanUp();
        _loggerFactory.Dispose();
    }

    protected virtual void OnCleanUp()
    {
    }

    [Test]
    public async Task Accepts_a_valid_message()
    {
        var response = await Create(ContractScenario.Accepts).SendAsync(Valid);

        Assert.That(response.Success, Is.True, string.Join(';', response.Errors));
        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.Accepted));
        Assert.That(response.Errors, Is.Empty);
        Assert.That(BackendCalls, Is.GreaterThan(0));
    }

    [Test]
    public async Task Rejects_an_invalid_message_before_calling_the_backend()
    {
        var response = await Create(ContractScenario.Accepts).SendAsync(Invalid);

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors, Is.Not.Empty);
        Assert.That(response.Errors.All(e => e.Kind == NotifierErrorKind.Validation));
        Assert.That(BackendCalls, Is.Zero);
    }

    [Test]
    public void A_null_message_is_a_programmer_error() =>
        Assert.ThrowsAsync<ArgumentNullException>(async () => await Create(ContractScenario.Accepts).SendAsync(null!));

    [Test]
    public async Task A_cancelled_token_throws_without_calling_the_backend()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var notifier = Create(ContractScenario.Accepts);

        await Assert.CatchAsync<OperationCanceledException>(async () => await notifier.SendAsync(Valid, cts.Token));
        Assert.That(BackendCalls, Is.Zero);
    }

    [Test]
    public async Task A_permanent_refusal_is_a_failed_permanent_result_not_an_exception()
    {
        var response = await Create(ContractScenario.RejectsPermanently).SendAsync(Valid);

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors, Is.Not.Empty);
        Assert.That(response.Errors.All(e => e.Kind == NotifierErrorKind.Permanent));
    }

    [Test]
    public async Task A_temporary_refusal_is_classified_as_transient_so_callers_know_repeating_is_safe()
    {
        var response = await Create(ContractScenario.RejectsTransiently).SendAsync(Valid);

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors, Is.Not.Empty);
        Assert.That(response.Errors.All(e => e.Kind == NotifierErrorKind.Transient));
    }

    [Test]
    public async Task An_unreachable_backend_is_a_failed_result_not_an_exception()
    {
        var response = await Create(ContractScenario.NetworkDown).SendAsync(Valid);

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors, Is.Not.Empty);
        Assert.That(response.Errors.All(e => e.Kind is NotifierErrorKind.Transient or NotifierErrorKind.Unknown));
    }

    [Test]
    public async Task Concurrent_sends_are_independent()
    {
        var notifier = Create(ContractScenario.Accepts);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => notifier.SendAsync(Valid)));

        Assert.That(responses.All(r => r.Success), Is.True);
    }

    [Test]
    public async Task Errors_and_logs_never_contain_credentials_or_personal_data()
    {
        foreach (var scenario in Enum.GetValues<ContractScenario>())
        {
            var response = await Create(scenario).SendAsync(Valid);
            var text = string.Join('\n', response.Errors.Select(e => e + " " + e.Message));

            foreach (var sensitive in Secrets.Concat(PersonalData))
            {
                Assert.That(text, Does.Not.Contain(sensitive), $"{scenario}: an error exposed '{sensitive}'");
            }
        }

        await Create(ContractScenario.Accepts).SendAsync(Invalid);

        Assert.That(_logs.Lines, Is.Not.Empty, "the provider should log what it does");
        foreach (var sensitive in Secrets.Concat(PersonalData))
        {
            Assert.That(_logs.Lines.Any(l => l.Contains(sensitive, StringComparison.Ordinal)), Is.False, $"a log line exposed '{sensitive}'");
        }
    }
}

[TestFixture]
public class MailgunContractTests : ProviderContractTests<EmailMessage>
{
    private StubHttpHandler _handler = new(HttpStatusCode.OK);

    protected override EmailMessage Valid => EmailMessage.Create("contract-recipient@example.com", "Subject", "Body");

    protected override EmailMessage Invalid => new();

    protected override IReadOnlyList<string> Secrets => ["key-SECRET"];

    protected override IReadOnlyList<string> PersonalData => ["contract-recipient@example.com"];

    protected override int BackendCalls => _handler.Requests.Count;

    protected override INotifier<EmailMessage> Create(ContractScenario scenario)
    {
        _handler = scenario switch
        {
            ContractScenario.Accepts => new StubHttpHandler(HttpStatusCode.OK, """{"id":"<1@mg.example.com>","message":"Queued."}"""),
            ContractScenario.RejectsPermanently => new StubHttpHandler(HttpStatusCode.BadRequest, """{"message":"invalid"}"""),
            ContractScenario.RejectsTransiently => new StubHttpHandler(HttpStatusCode.ServiceUnavailable, "{}"),
            _ => new StubHttpHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "down")),
        };

        return new MailgunNotifier(
            new MailgunOptions { ApiKey = "key-SECRET", Domain = "mg.example.com", From = "from@mg.example.com" },
            new HttpClient(_handler),
            Context);
    }
}

[TestFixture]
public class SmtpContractTests : ProviderContractTests<EmailMessage>
{
    private FakeSmtpServer? _server;
    private int _closedPort;

    protected override EmailMessage Valid => EmailMessage.Create("contract-recipient@example.com", "Subject", "Body");

    protected override EmailMessage Invalid => new();

    protected override IReadOnlyList<string> Secrets => ["pass-SECRET"];

    protected override IReadOnlyList<string> PersonalData => ["contract-recipient@example.com"];

    protected override int BackendCalls => _server?.Mails.Count ?? 0;

    protected override INotifier<EmailMessage> Create(ContractScenario scenario)
    {
        _server?.Dispose();
        _server = new FakeSmtpServer { RequiredLogin = ("user", "pass-SECRET") };
        _server.RejectRecipient = scenario switch
        {
            ContractScenario.RejectsPermanently => _ => "550 5.1.1 no such user",
            ContractScenario.RejectsTransiently => _ => "450 4.2.0 try again later",
            _ => null,
        };

        if (scenario == ContractScenario.NetworkDown)
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
        }

        return new SmtpNotifier(
            new SmtpOptions
            {
                Host = "127.0.0.1",
                Port = scenario == ContractScenario.NetworkDown ? _closedPort : _server.Port,
                Security = SmtpSecurity.None,
                UserName = "user",
                Password = "pass-SECRET",
                AllowInsecureAuthentication = true,
                From = "from@example.com",
                Timeout = TimeSpan.FromSeconds(5),
            },
            Context);
    }

    protected override void OnCleanUp() => _server?.Dispose();
}

[TestFixture]
public class TwilioContractTests : ProviderContractTests<SmsMessage>
{
    private StubHttpHandler _handler = new(HttpStatusCode.OK);

    protected override SmsMessage Valid => SmsMessage.Create("+14165550199", "Hello");

    protected override SmsMessage Invalid => new();

    protected override IReadOnlyList<string> Secrets => ["token-SECRET"];

    protected override IReadOnlyList<string> PersonalData => ["+14165550199"];

    protected override int BackendCalls => _handler.Requests.Count;

    protected override INotifier<SmsMessage> Create(ContractScenario scenario)
    {
        _handler = scenario switch
        {
            ContractScenario.Accepts => new StubHttpHandler(HttpStatusCode.Created, """{"sid":"SM1","status":"queued"}"""),
            ContractScenario.RejectsPermanently => new StubHttpHandler(HttpStatusCode.BadRequest, """{"code":21211,"message":"The 'To' number +14165550199 is not a valid phone number."}"""),
            ContractScenario.RejectsTransiently => new StubHttpHandler(HttpStatusCode.ServiceUnavailable, "{}"),
            _ => new StubHttpHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "down")),
        };

        return new TwilioSmsNotifier(
            new TwilioOptions { AccountSid = "AC00000000000000000000000000000000", AuthToken = "token-SECRET", From = "+15005550006" },
            new HttpClient(_handler),
            Context);
    }
}

[TestFixture]
public class FcmContractTests : ProviderContractTests<PushMessage>
{
    private sealed class ScriptedGateway(Func<int, IReadOnlyList<FcmResult>> respond) : IFcmGateway
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<FcmResult>> SendEachAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(respond(messages.Count));
        }

        public void Dispose()
        {
        }
    }

    private ScriptedGateway _gateway = new(_ => []);

    protected override PushMessage Valid => PushMessage.Create("contract-device-token", "Title", "Body");

    protected override PushMessage Invalid => new();

    protected override IReadOnlyList<string> Secrets => ["SECRET-KEY"];

    protected override IReadOnlyList<string> PersonalData => ["contract-device-token"];

    protected override int BackendCalls => _gateway.Calls;

    protected override INotifier<PushMessage> Create(ContractScenario scenario)
    {
        _gateway = scenario switch
        {
            ContractScenario.Accepts => new ScriptedGateway(n => [.. Enumerable.Range(0, n).Select(i => new FcmResult("projects/p/messages/" + i, null))]),
            ContractScenario.RejectsPermanently => new ScriptedGateway(n => [.. Enumerable.Range(0, n).Select(_ => new FcmResult(null, MessagingErrorCode.InvalidArgument))]),
            ContractScenario.RejectsTransiently => new ScriptedGateway(n => [.. Enumerable.Range(0, n).Select(_ => new FcmResult(null, MessagingErrorCode.Unavailable))]),
            _ => new ScriptedGateway(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "down")),
        };

        return new FcmPushNotifier(new FcmPushOptions { CredentialJson = "SECRET-KEY" }, _gateway, Context);
    }
}

[TestFixture]
public class TelegramContractTests : ProviderContractTests<TelegramMessage>
{
    private StubHttpHandler _handler = new(HttpStatusCode.OK);

    protected override TelegramMessage Valid => TelegramMessage.Create("424242", "Hello");

    protected override TelegramMessage Invalid => new();

    protected override IReadOnlyList<string> Secrets => ["123456:SECRET-token"];

    protected override IReadOnlyList<string> PersonalData => ["424242"];

    protected override int BackendCalls => _handler.Requests.Count;

    protected override INotifier<TelegramMessage> Create(ContractScenario scenario)
    {
        _handler = scenario switch
        {
            ContractScenario.Accepts => new StubHttpHandler(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":1}}"""),
            ContractScenario.RejectsPermanently => new StubHttpHandler(HttpStatusCode.BadRequest, """{"ok":false,"description":"Bad Request: chat not found"}"""),
            ContractScenario.RejectsTransiently => new StubHttpHandler(HttpStatusCode.TooManyRequests, """{"ok":false,"parameters":{"retry_after":1}}"""),
            _ => new StubHttpHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "down")),
        };

        return new TelegramNotifier(new TelegramOptions { BotToken = "123456:SECRET-token" }, Context, _handler);
    }
}

[TestFixture]
public class WebPushContractTests : ProviderContractTests<WebPushMessage>
{
    private static readonly VapidKeys Keys = VapidKeys.Generate();
    private static readonly WebPushSubscription Subscription = WebPushNotifierTests.Subscription("https://push.example.com/send/contract-endpoint");

    private StubHttpHandler _handler = new(HttpStatusCode.Created);

    protected override WebPushMessage Valid => new() { Subscriptions = [Subscription], Title = "Hello", Body = "World" };

    protected override WebPushMessage Invalid => new();

    protected override IReadOnlyList<string> Secrets => [Keys.PrivateKey];

    protected override IReadOnlyList<string> PersonalData => ["contract-endpoint"];

    protected override int BackendCalls => _handler.Requests.Count;

    protected override INotifier<WebPushMessage> Create(ContractScenario scenario)
    {
        _handler = scenario switch
        {
            ContractScenario.Accepts => new StubHttpHandler(HttpStatusCode.Created),
            ContractScenario.RejectsPermanently => new StubHttpHandler(HttpStatusCode.BadRequest),
            ContractScenario.RejectsTransiently => new StubHttpHandler(HttpStatusCode.TooManyRequests),
            _ => new StubHttpHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "down")),
        };

        return new WebPushNotifier(
            new WebPushOptions { PublicKey = Keys.PublicKey, PrivateKey = Keys.PrivateKey, Subject = "mailto:ops@example.com" },
            Context,
            _handler);
    }
}

[TestFixture]
public class WebhookContractTests : ProviderContractTests<WebhookMessage>
{
    private StubHttpHandler _handler = new(HttpStatusCode.OK);

    protected override WebhookMessage Valid => WebhookMessage.Create("ops", "Deploy finished", "Release");

    protected override WebhookMessage Invalid => new();

    protected override IReadOnlyList<string> Secrets => ["T0SECRET/B0SECRET/xyzSECRET", "signing-SECRET"];

    protected override IReadOnlyList<string> PersonalData => [];

    protected override int BackendCalls => _handler.Requests.Count;

    protected override INotifier<WebhookMessage> Create(ContractScenario scenario)
    {
        _handler = scenario switch
        {
            ContractScenario.Accepts => new StubHttpHandler(HttpStatusCode.OK, "ok"),
            ContractScenario.RejectsPermanently => new StubHttpHandler(HttpStatusCode.BadRequest, "invalid_payload"),
            ContractScenario.RejectsTransiently => new StubHttpHandler(HttpStatusCode.TooManyRequests, "rate_limited"),
            _ => new StubHttpHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "down")),
        };

        var options = new WebhookOptions().Add("ops", "https://hooks.slack.com/services/T0SECRET/B0SECRET/xyzSECRET", WebhookFormat.Slack, "signing-SECRET");
        return new WebhookNotifier(options, Context, _handler);
    }
}

[TestFixture]
public class SendGridContractTests : ProviderContractTests<EmailMessage>
{
    private StubHttpHandler _handler = new(HttpStatusCode.OK);

    protected override EmailMessage Valid => EmailMessage.Create("contract-recipient@example.com", "Subject", "Body");

    protected override EmailMessage Invalid => new();

    protected override IReadOnlyList<string> Secrets => ["SG.SECRET-key"];

    protected override IReadOnlyList<string> PersonalData => ["contract-recipient@example.com"];

    protected override int BackendCalls => _handler.Requests.Count;

    protected override INotifier<EmailMessage> Create(ContractScenario scenario)
    {
        _handler = scenario switch
        {
            ContractScenario.Accepts => new StubHttpHandler(HttpStatusCode.Accepted, string.Empty),
            ContractScenario.RejectsPermanently => new StubHttpHandler(HttpStatusCode.BadRequest, """{"errors":[{"message":"The to email address contract-recipient@example.com is invalid","field":"personalizations.0.to.0.email"}]}"""),
            ContractScenario.RejectsTransiently => new StubHttpHandler(HttpStatusCode.TooManyRequests, "{}"),
            _ => new StubHttpHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "down")),
        };

        return new SendGridNotifier(
            new SendGridOptions { ApiKey = "SG.SECRET-key", From = "from@example.com" },
            new HttpClient(_handler),
            Context);
    }
}

[TestFixture]
public class TwilioWhatsAppContractTests : ProviderContractTests<WhatsAppMessage>
{
    private StubHttpHandler _handler = new(HttpStatusCode.OK);

    protected override WhatsAppMessage Valid => WhatsAppMessage.Create("+14165550199", "Hello");

    protected override WhatsAppMessage Invalid => new();

    protected override IReadOnlyList<string> Secrets => ["token-SECRET"];

    protected override IReadOnlyList<string> PersonalData => ["+14165550199"];

    protected override int BackendCalls => _handler.Requests.Count;

    protected override INotifier<WhatsAppMessage> Create(ContractScenario scenario)
    {
        _handler = scenario switch
        {
            ContractScenario.Accepts => new StubHttpHandler(HttpStatusCode.Created, """{"sid":"SM1","status":"queued"}"""),
            ContractScenario.RejectsPermanently => new StubHttpHandler(HttpStatusCode.BadRequest, """{"code":63016,"message":"outside the window for +14165550199"}"""),
            ContractScenario.RejectsTransiently => new StubHttpHandler(HttpStatusCode.ServiceUnavailable, "{}"),
            _ => new StubHttpHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "down")),
        };

        return new TwilioWhatsAppNotifier(
            new TwilioOptions { AccountSid = "AC00000000000000000000000000000000", AuthToken = "token-SECRET", WhatsAppFrom = "+14155238886" },
            new HttpClient(_handler),
            Context);
    }
}
