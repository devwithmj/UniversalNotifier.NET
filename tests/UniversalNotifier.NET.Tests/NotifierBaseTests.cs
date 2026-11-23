using Microsoft.Extensions.Time.Testing;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class NotifierBaseTests
{
    private sealed class ScriptedNotifier(NotifierContext context, params Func<NotifierResponse>[] script)
        : NotifierBase<SmsMessage>(context)
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<NotifierResponse> SendCoreAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            var step = script[Math.Min(_calls++, script.Length - 1)];
            return Task.FromResult(step());
        }
    }

    private sealed class ThrowingNotifier(NotifierContext context, Exception exception) : NotifierBase<SmsMessage>(context)
    {
        protected override Task<NotifierResponse> SendCoreAsync(SmsMessage message, CancellationToken cancellationToken) =>
            throw exception;
    }

    private static readonly SmsMessage Valid = SmsMessage.Create("+14165551234", "Hi");

    private static NotifierResponse Fail(NotifierErrorKind kind, TimeSpan? retryAfter = null) =>
        NotifierResponse.Failed(new NotifierError(kind, "test", "failed"), retryAfter: retryAfter);

    private static NotifierContext Retrying(FakeTimeProvider time, bool retryUnknown = false, int attempts = 3) =>
        new(Options(attempts, retryUnknown), timeProvider: time);

    private static NotifierOptions Options(int attempts, bool retryUnknown)
    {
        var options = new NotifierOptions();
        options.Retry.MaxAttempts = attempts;
        options.Retry.RetryUnknownFailures = retryUnknown;
        return options;
    }

    // Completes the notifier's retry delays by moving the fake clock forward until the send finishes.
    private static async Task<NotifierResponse> SendAsync(ScriptedNotifier notifier, FakeTimeProvider time)
    {
        var send = notifier.SendAsync(Valid);
        while (!send.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(31));
            await Task.Yield();
        }

        return await send;
    }

    [Test]
    public async Task Invalid_messages_fail_validation_without_calling_the_provider()
    {
        var notifier = new ScriptedNotifier(new NotifierContext(), () => NotifierResponse.Accepted());

        var response = await notifier.SendAsync(new SmsMessage());

        Assert.That(response.Success, Is.False);
        Assert.That(response.Errors.All(e => e.Kind == NotifierErrorKind.Validation));
        Assert.That(notifier.Calls, Is.Zero);
    }

    [Test]
    public async Task Accepted_messages_are_returned_as_they_are()
    {
        var notifier = new ScriptedNotifier(new NotifierContext(), () => NotifierResponse.Accepted("id-1", 200));

        var response = await notifier.SendAsync(Valid);

        Assert.That(response.Success, Is.True);
        Assert.That(response.ProviderMessageId, Is.EqualTo("id-1"));
        Assert.That(notifier.Calls, Is.EqualTo(1));
    }

    [Test]
    public void A_null_message_is_a_programmer_error() =>
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await new ScriptedNotifier(new NotifierContext(), () => NotifierResponse.Accepted()).SendAsync(null!));

    [Test]
    public void A_cancelled_token_throws_before_any_work()
    {
        var notifier = new ScriptedNotifier(new NotifierContext(), () => NotifierResponse.Accepted());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await notifier.SendAsync(Valid, cts.Token));
        Assert.That(notifier.Calls, Is.Zero);
    }

    [Test]
    public async Task Exceptions_from_the_provider_become_failed_responses()
    {
        var notifier = new ThrowingNotifier(new NotifierContext(), new InvalidOperationException("boom"));

        var response = await notifier.SendAsync(Valid);

        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.Failed));
        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Unknown));
        Assert.That(response.Errors[0].Code, Is.EqualTo("provider.error"));
    }

    [Test]
    public async Task Connection_failures_are_transient_and_other_http_failures_are_unknown()
    {
        var unreachable = new ThrowingNotifier(new NotifierContext(),
            new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        var broken = new ThrowingNotifier(new NotifierContext(),
            new HttpRequestException(HttpRequestError.InvalidResponse, "bad"));

        Assert.That((await unreachable.SendAsync(Valid)).Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Transient));
        Assert.That((await broken.SendAsync(Valid)).Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Unknown));
    }

    [Test]
    public async Task A_timeout_that_is_not_the_callers_cancellation_is_unknown_not_thrown()
    {
        var notifier = new ThrowingNotifier(new NotifierContext(), new TaskCanceledException("timed out"));

        var response = await notifier.SendAsync(Valid);

        Assert.That(response.Errors[0].Code, Is.EqualTo("network.timeout"));
        Assert.That(response.Errors[0].Kind, Is.EqualTo(NotifierErrorKind.Unknown));
    }

    [Test]
    public void Caller_cancellation_during_the_provider_call_propagates()
    {
        using var cts = new CancellationTokenSource();
        var notifier = new ThrowingNotifier(new NotifierContext(), new OperationCanceledException(cts.Token));
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await notifier.SendAsync(Valid, cts.Token));
    }

    [Test]
    public async Task There_are_no_retries_by_default()
    {
        var notifier = new ScriptedNotifier(new NotifierContext(), () => Fail(NotifierErrorKind.Transient));

        var response = await notifier.SendAsync(Valid);

        Assert.That(response.Success, Is.False);
        Assert.That(notifier.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Transient_failures_are_retried_until_success()
    {
        var time = new FakeTimeProvider();
        var notifier = new ScriptedNotifier(
            Retrying(time),
            () => Fail(NotifierErrorKind.Transient),
            () => Fail(NotifierErrorKind.Transient),
            () => NotifierResponse.Accepted("ok"));

        var response = await SendAsync(notifier, time);

        Assert.That(response.Success, Is.True);
        Assert.That(notifier.Calls, Is.EqualTo(3));
    }

    [Test]
    public async Task Retries_stop_at_the_maximum_attempts()
    {
        var time = new FakeTimeProvider();
        var notifier = new ScriptedNotifier(Retrying(time, attempts: 3), () => Fail(NotifierErrorKind.Transient));

        var response = await SendAsync(notifier, time);

        Assert.That(response.Success, Is.False);
        Assert.That(notifier.Calls, Is.EqualTo(3));
    }

    [TestCase(NotifierErrorKind.Permanent)]
    [TestCase(NotifierErrorKind.Validation)]
    [TestCase(NotifierErrorKind.Configuration)]
    public async Task Permanent_failures_are_never_retried(NotifierErrorKind kind)
    {
        var time = new FakeTimeProvider();
        var notifier = new ScriptedNotifier(Retrying(time), () => Fail(kind));

        await SendAsync(notifier, time);

        Assert.That(notifier.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Unknown_failures_are_retried_only_when_explicitly_enabled()
    {
        var time = new FakeTimeProvider();
        var off = new ScriptedNotifier(Retrying(time), () => Fail(NotifierErrorKind.Unknown));
        var on = new ScriptedNotifier(Retrying(time, retryUnknown: true), () => Fail(NotifierErrorKind.Unknown));

        await SendAsync(off, time);
        await SendAsync(on, time);

        Assert.That(off.Calls, Is.EqualTo(1));
        Assert.That(on.Calls, Is.EqualTo(3));
    }

    [Test]
    public async Task A_partial_success_is_never_retried_because_it_would_message_accepted_recipients_twice()
    {
        var time = new FakeTimeProvider();
        var transient = new RecipientResult("b", false, Error: new NotifierError(NotifierErrorKind.Transient, "t", "later"));
        var notifier = new ScriptedNotifier(
            Retrying(time),
            () => NotifierResponse.FromRecipients([new RecipientResult("a", true), transient]));

        var response = await SendAsync(notifier, time);

        Assert.That(response.Status, Is.EqualTo(DeliveryStatus.PartiallyAccepted));
        Assert.That(notifier.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task The_providers_retry_after_hint_is_honored()
    {
        var time = new FakeTimeProvider();
        var notifier = new ScriptedNotifier(
            Retrying(time),
            () => Fail(NotifierErrorKind.Transient, retryAfter: TimeSpan.FromSeconds(10)),
            () => NotifierResponse.Accepted());

        var send = notifier.SendAsync(Valid);
        await Task.Yield();
        time.Advance(TimeSpan.FromSeconds(9));
        await Task.Yield();
        Assert.That(send.IsCompleted, Is.False, "must still be waiting for the 10 s hint");

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.That((await send).Success, Is.True);
    }

    [Test]
    public void Cancelling_during_a_retry_delay_stops_retrying()
    {
        var time = new FakeTimeProvider();
        var notifier = new ScriptedNotifier(Retrying(time), () => Fail(NotifierErrorKind.Transient));
        using var cts = new CancellationTokenSource();

        var send = notifier.SendAsync(Valid, cts.Token);
        cts.Cancel();

        Assert.ThrowsAsync<TaskCanceledException>(async () => await send);
    }
}
