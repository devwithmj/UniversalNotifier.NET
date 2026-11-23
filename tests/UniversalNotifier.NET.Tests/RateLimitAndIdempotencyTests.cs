using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UniversalNotifier.NET.Background;
using UniversalNotifier.NET.Testing;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class RateLimitAndIdempotencyTests
{
    private static RateLimitOptions Limit(double perSecond, int burst = 1) => new() { PermitsPerSecond = perSecond, Burst = burst };

    [Test]
    public async Task The_gate_spaces_calls_evenly()
    {
        var time = new FakeTimeProvider();
        var gate = new RateGate(Limit(10), time); // one call every 100 ms

        await gate.WaitAsync(default);
        var second = gate.WaitAsync(default);
        var third = gate.WaitAsync(default);

        Assert.That(second.IsCompleted, Is.False);
        time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.That(second.IsCompleted, Is.False);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await second;
        Assert.That(third.IsCompleted, Is.False);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await third;
    }

    [Test]
    public async Task A_burst_goes_through_at_once_then_spacing_starts()
    {
        var time = new FakeTimeProvider();
        var gate = new RateGate(Limit(10, burst: 3), time);

        await gate.WaitAsync(default);
        await gate.WaitAsync(default);
        await gate.WaitAsync(default);
        var fourth = gate.WaitAsync(default);

        Assert.That(fourth.IsCompleted, Is.False);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await fourth;
    }

    [Test]
    public async Task An_idle_gate_forgets_the_past_and_allows_a_fresh_burst()
    {
        var time = new FakeTimeProvider();
        var gate = new RateGate(Limit(10, burst: 2), time);
        await gate.WaitAsync(default);
        await gate.WaitAsync(default);

        time.Advance(TimeSpan.FromSeconds(10));

        await gate.WaitAsync(default);
        await gate.WaitAsync(default);
    }

    [Test]
    public async Task Waiting_for_the_gate_can_be_cancelled()
    {
        var time = new FakeTimeProvider();
        var gate = new RateGate(Limit(1), time);
        await gate.WaitAsync(default);
        using var cts = new CancellationTokenSource();

        var waiting = gate.WaitAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.CatchAsync<OperationCanceledException>(async () => await waiting);
    }

    private sealed class CountingSms(NotifierContext context) : NotifierBase<SmsMessage>(context)
    {
        public int Sent;

        protected override Task<NotifierResponse> SendCoreAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Sent);
            return Task.FromResult(NotifierResponse.Accepted());
        }
    }

    [Test]
    public async Task A_notifier_waits_its_turn_instead_of_exceeding_the_configured_rate()
    {
        var time = new FakeTimeProvider();
        var options = new NotifierOptions();
        options.RateLimit.PermitsPerSecond = 5; // every 200 ms
        var notifier = new CountingSms(new NotifierContext(options, timeProvider: time));
        var message = SmsMessage.Create("+14165551234", "Hi");

        var sends = Enumerable.Range(0, 3).Select(_ => notifier.SendAsync(message)).ToArray();
        await Task.Delay(50);
        Assert.That(notifier.Sent, Is.EqualTo(1), "only the first call may go out immediately");

        time.Advance(TimeSpan.FromMilliseconds(200));
        await Task.Delay(50);
        Assert.That(notifier.Sent, Is.EqualTo(2));

        time.Advance(TimeSpan.FromMilliseconds(200));
        await Task.WhenAll(sends);
        Assert.That(notifier.Sent, Is.EqualTo(3));
    }

    [Test]
    public async Task Rate_limiting_is_off_by_default()
    {
        var notifier = new CountingSms(new NotifierContext());

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => notifier.SendAsync(SmsMessage.Create("+14165551234", "Hi"))));

        Assert.That(notifier.Sent, Is.EqualTo(50));
    }

    [Test]
    public void The_rate_limit_is_configured_and_validated_through_the_builder()
    {
        using var provider = new ServiceCollection().AddUniversalNotifier(n => n.WithRateLimit(20, burst: 5)).BuildServiceProvider();
        var limit = provider.GetRequiredService<NotifierContext>().RateLimit;

        Assert.That(limit.PermitsPerSecond, Is.EqualTo(20));
        Assert.That(limit.Burst, Is.EqualTo(5));

        Assert.Throws<OptionsValidationException>(() => new NotifierBuilder().WithRateLimit(-1).Build());
        Assert.Throws<OptionsValidationException>(() => new NotifierBuilder().WithRateLimit(5, burst: 0).Build());
    }

    private static Notification Keyed(string? key) =>
        key is null ? Notification.To("ada@example.com").Email("Hi", text: "x") : Notification.To("ada@example.com").Email("Hi", text: "x").WithIdempotencyKey(key);

    private static async Task<List<Notification>> DrainAsync(ChannelNotificationQueue queue)
    {
        queue.Complete();
        var items = new List<Notification>();
        await foreach (var item in queue.DequeueAllAsync())
        {
            items.Add(item);
        }

        return items;
    }

    [Test]
    public async Task A_repeated_idempotency_key_is_accepted_but_queued_only_once()
    {
        var queue = new ChannelNotificationQueue(new BackgroundDeliveryOptions { DeduplicationWindow = TimeSpan.FromMinutes(5) });

        Assert.That(await queue.EnqueueAsync(Keyed("order-42")), Is.True);
        Assert.That(await queue.EnqueueAsync(Keyed("order-42")), Is.True, "a duplicate is not an error");
        Assert.That(await queue.EnqueueAsync(Keyed("order-43")), Is.True);
        Assert.That(await queue.EnqueueAsync(Keyed(null)), Is.True);
        Assert.That(await queue.EnqueueAsync(Keyed(null)), Is.True);

        var queued = await DrainAsync(queue);
        Assert.That(queued.Select(n => n.IdempotencyKey), Is.EqualTo(new string?[] { "order-42", "order-43", null, null }));
    }

    [Test]
    public async Task The_key_is_forgotten_after_the_window()
    {
        var time = new FakeTimeProvider();
        var queue = new ChannelNotificationQueue(new BackgroundDeliveryOptions { DeduplicationWindow = TimeSpan.FromMinutes(5) }, time);

        await queue.EnqueueAsync(Keyed("k"));
        time.Advance(TimeSpan.FromMinutes(4));
        await queue.EnqueueAsync(Keyed("k"));
        time.Advance(TimeSpan.FromMinutes(2));
        await queue.EnqueueAsync(Keyed("k"));

        Assert.That(await DrainAsync(queue), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Without_a_window_keys_are_ignored()
    {
        var queue = new ChannelNotificationQueue(new BackgroundDeliveryOptions());

        await queue.EnqueueAsync(Keyed("k"));
        await queue.EnqueueAsync(Keyed("k"));

        Assert.That(await DrainAsync(queue), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task A_notification_that_was_not_queued_does_not_burn_its_key()
    {
        var queue = new ChannelNotificationQueue(new BackgroundDeliveryOptions
        {
            Capacity = 1,
            FullMode = QueueFullMode.Reject,
            DeduplicationWindow = TimeSpan.FromMinutes(5),
        });
        await queue.EnqueueAsync(Keyed("first"));

        Assert.That(await queue.EnqueueAsync(Keyed("second")), Is.False, "the queue is full");
        await foreach (var _ in queue.DequeueAllAsync().Take(1))
        {
        }

        Assert.That(await queue.EnqueueAsync(Keyed("second")), Is.True, "the retry must not be mistaken for a duplicate");
    }

    [Test]
    public async Task Concurrent_submissions_of_one_key_are_queued_exactly_once()
    {
        var queue = new ChannelNotificationQueue(new BackgroundDeliveryOptions { DeduplicationWindow = TimeSpan.FromMinutes(5), Capacity = 1000 });

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(async () => await queue.EnqueueAsync(Keyed("same")))));

        Assert.That(await DrainAsync(queue), Has.Count.EqualTo(1));
    }

    [Test]
    public void The_idempotency_key_survives_json_and_can_be_configured_through_the_builder()
    {
        var copy = NotificationJson.Deserialize(NotificationJson.Serialize(Keyed("order-9")));

        Assert.That(copy.IdempotencyKey, Is.EqualTo("order-9"));
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddFakeNotifiers().AddBackgroundDelivery(o => o.DeduplicationWindow = TimeSpan.FromMinutes(1)))
            .BuildServiceProvider();
        Assert.That(provider.GetRequiredService<INotificationQueue>(), Is.InstanceOf<ChannelNotificationQueue>());
        Assert.Throws<ArgumentException>(() => new ChannelNotificationQueue(new BackgroundDeliveryOptions { DeduplicationWindow = TimeSpan.FromSeconds(-1) }));
    }
}
