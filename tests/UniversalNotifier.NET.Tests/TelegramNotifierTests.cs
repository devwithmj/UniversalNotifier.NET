using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET.Telegram;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class TelegramNotifierTests
{
    private const string Token = "123456:SECRET-token";
    private const string Ok = """{"ok":true,"result":{"message_id":77,"chat":{"id":1}}}""";

    private static TelegramNotifier Create(StubHttpHandler handler, Action<TelegramOptions>? configure = null, NotifierContext? context = null)
    {
        var options = new TelegramOptions { BotToken = Token };
        configure?.Invoke(options);
        return new TelegramNotifier(options, context ?? new NotifierContext(), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Test]
    public async Task Posts_json_to_the_bot_endpoint()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Ok);

        var response = await Create(handler).SendAsync(TelegramMessage.Create("42", "Hello <b>there</b>"));

        Assert.That(response.Success, Is.True);
        Assert.That(response.ProviderMessageId, Is.EqualTo("77"));
        var request = handler.OnlyRequest;
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(request.Uri.ToString(), Is.EqualTo($"https://api.telegram.org/bot{Token}/sendMessage"));
        using var body = JsonDocument.Parse(request.Body);
        Assert.That(body.RootElement.GetProperty("chat_id").GetString(), Is.EqualTo("42"));
        Assert.That(body.RootElement.GetProperty("text").GetString(), Is.EqualTo("Hello <b>there</b>"));
        Assert.That(body.RootElement.TryGetProperty("parse_mode", out _), Is.False);
        Assert.That(body.RootElement.TryGetProperty("disable_notification", out _), Is.False);
    }

    [Test]
    public async Task Parse_mode_and_silent_are_forwarded()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Ok);
        var notifier = Create(handler);

        await notifier.SendAsync(TelegramMessage.Create("42", "x") with { ParseMode = TelegramParseMode.Html, Silent = true });
        await notifier.SendAsync(TelegramMessage.Create("42", "x") with { ParseMode = TelegramParseMode.MarkdownV2 });

        using var first = JsonDocument.Parse(handler.Requests[0].Body);
        using var second = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.That(first.RootElement.GetProperty("parse_mode").GetString(), Is.EqualTo("HTML"));
        Assert.That(first.RootElement.GetProperty("disable_notification").GetBoolean(), Is.True);
        Assert.That(second.RootElement.GetProperty("parse_mode").GetString(), Is.EqualTo("MarkdownV2"));
    }

    [Test]
    public async Task Each_chat_gets_its_own_request_and_result()
    {
        var handler = new StubHttpHandler(r => r.Body.Contains("\"chat_id\":\"2\"", StringComparison.Ordinal)
            ? Json(HttpStatusCode.Forbidden, """{"ok":false,"error_code":403,"description":"Forbidden: bot was blocked by the user"}""")
            : Json(HttpStatusCode.OK, Ok));

        var response = await Create(handler).SendAsync(new TelegramMessage { ChatIds = ["1", "2", "3"], Text = "Hi" });

        Assert.That(handler.Requests, Has.Count.EqualTo(3));
        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        Assert.That(response.Recipients.Select(r => r.Recipient), Is.EqualTo(new[] { "1", "2", "3" }));
        var blocked = response.Recipients.Single(r => !r.Accepted);
        Assert.That(blocked.Recipient, Is.EqualTo("2"));
        Assert.That(blocked.Error!.Value.Code, Is.EqualTo("telegram.blocked"));
        Assert.That(blocked.Error.Value.Kind, Is.EqualTo(NotifierErrorKind.Permanent));
        Assert.That(blocked.Error.Value.Message, Does.Contain("blocked by the user"));
    }

    [Test]
    public async Task Rate_limits_report_the_wait_telegram_asks_for()
    {
        var handler = new StubHttpHandler(HttpStatusCode.TooManyRequests,
            """{"ok":false,"error_code":429,"description":"Too Many Requests: retry after 5","parameters":{"retry_after":5}}""");

        var response = await Create(handler).SendAsync(TelegramMessage.Create("1", "x"));

        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Transient));
        Assert.That(response.Errors[0].Code, Is.EqualTo("telegram.rate_limited"));
        Assert.That(response.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(5)));
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.NotFound)]
    public async Task A_bad_token_is_a_configuration_error_that_never_repeats_the_token(HttpStatusCode status)
    {
        var response = await Create(new StubHttpHandler(status, """{"ok":false,"description":"Unauthorized"}""")).SendAsync(TelegramMessage.Create("1", "x"));

        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Configuration));
        Assert.That(response.Errors[0].Message, Does.Not.Contain("SECRET"));
    }

    [Test]
    public async Task A_bad_request_is_permanent_with_telegrams_reason()
    {
        var handler = new StubHttpHandler(HttpStatusCode.BadRequest, """{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}""");

        var response = await Create(handler).SendAsync(TelegramMessage.Create("nope", "x"));

        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Permanent));
        Assert.That(response.Errors[0].Message, Does.Contain("chat not found"));
    }

    [Test]
    public async Task Empty_and_non_json_bodies_do_not_throw()
    {
        foreach (var body in new[] { "", "<html/>", "[]" })
        {
            var response = await Create(new StubHttpHandler(HttpStatusCode.BadGateway, body)).SendAsync(TelegramMessage.Create("1", "x"));

            Assert.That(response.Success, Is.False, body);
        }
    }

    [Test]
    public async Task Text_longer_than_telegram_allows_is_rejected_before_sending()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Ok);

        var response = await Create(handler).SendAsync(TelegramMessage.Create("1", new string('x', 4097)));

        Assert.That(response.Errors[0].Code, Is.EqualTo("telegram.text.too_long"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task Invalid_messages_never_reach_the_network()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Ok);

        var response = await Create(handler).SendAsync(new TelegramMessage());

        Assert.That(response.Success, Is.False);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task The_bot_token_never_reaches_the_logs()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var handler = new StubHttpHandler(HttpStatusCode.Unauthorized, """{"ok":false,"description":"Unauthorized"}""");

        await Create(handler, context: new NotifierContext(loggerFactory: factory)).SendAsync(TelegramMessage.Create("1", "private text"));

        Assert.That(logs.Lines, Is.Not.Empty);
        Assert.That(logs.Lines.Any(l => l.Contains("SECRET", StringComparison.Ordinal) || l.Contains("private text", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public void Invalid_options_are_rejected()
    {
        var bad = new[]
        {
            new TelegramOptions(),
            new TelegramOptions { BotToken = "a/b" },
            new TelegramOptions { BotToken = Token, BaseAddress = new Uri("http://api.telegram.org") },
            new TelegramOptions { BotToken = Token, MaxConcurrency = 0 },
        };

        foreach (var options in bad)
        {
            Assert.Throws<ArgumentException>(() => new TelegramNotifier(options, new NotifierContext()));
        }
    }

    [Test]
    public void Options_never_print_the_token() =>
        Assert.That(new TelegramOptions { BotToken = Token }.ToString(), Does.Not.Contain("SECRET"));

    [Test]
    public void It_registers_through_dependency_injection()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddTelegram(o => o.BotToken = Token))
            .BuildServiceProvider();

        Assert.That(provider.GetRequiredService<ITelegramNotifier>(), Is.InstanceOf<TelegramNotifier>());
        Assert.That(provider.GetRequiredKeyedService<ITelegramNotifier>("telegram"), Is.SameAs(provider.GetRequiredService<ITelegramNotifier>()));
    }

    [Test]
    public void Invalid_settings_are_reported_by_the_options_system()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddTelegram())
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<ITelegramNotifier>());
    }
}
