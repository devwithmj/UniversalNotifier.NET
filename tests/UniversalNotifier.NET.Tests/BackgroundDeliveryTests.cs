using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET.Background;
using UniversalNotifier.NET.Testing;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
public class BackgroundDeliveryTests
{
    private sealed class RecordingHandler : INotificationResultHandler
    {
        private readonly List<(Notification Notification, NotificationResult Result)> _handled = [];

        public Func<Notification, Exception?>? Throw { get; set; }

        public IReadOnlyList<(Notification Notification, NotificationResult Result)> Handled
        {
            get
            {
                lock (_handled)
                {
                    return [.. _handled];
                }
            }
        }

        public ValueTask HandleAsync(Notification notification, NotificationResult result, CancellationToken cancellationToken)
        {
            lock (_handled)
            {
                _handled.Add((notification, result));
            }

            return Throw?.Invoke(notification) is { } exception ? throw exception : ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedService(Func<Notification, CancellationToken, Task<NotificationResult>> send) : INotificationService
    {
        public Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken = default) =>
            send(notification, cancellationToken);
    }

    private static Notification Mail(string id) => Notification.To("ada@example.com").Email("Hi", text: "x").WithId(id);

    private static NotificationResult Accepted(Notification n) =>
        new(n.Id, [new ChannelResult(NotificationChannel.Email, NotifierResponse.Accepted())]);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for the condition.");
            }

            await Task.Delay(5);
        }
    }

    private static NotificationDeliveryService Worker(
        INotificationQueue queue,
        INotificationService service,
        int concurrency = 2,
        params INotificationResultHandler[] handlers) =>
        new(queue, service, handlers, Microsoft.Extensions.Options.Options.Create(new BackgroundDeliveryOptions { MaxConcurrency = concurrency }), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

    private static ChannelNotificationQueue Queue(int capacity = 100, QueueFullMode mode = QueueFullMode.Wait) =>
        new(new BackgroundDeliveryOptions { Capacity = capacity, FullMode = mode });

    [Test]
    public async Task Enqueued_notifications_are_delivered_and_the_handler_sees_the_result()
    {
        var handler = new RecordingHandler();
        var queue = Queue();
        var worker = Worker(queue, new ScriptedService((n, _) => Task.FromResult(Accepted(n))), handlers: handler);
        await worker.StartAsync(default);

        Assert.That(await queue.EnqueueAsync(Mail("a")), Is.True);
        Assert.That(await queue.EnqueueAsync(Mail("b")), Is.True);

        await WaitUntilAsync(() => handler.Handled.Count == 2);
        Assert.That(handler.Handled.Select(h => h.Notification.Id), Is.EquivalentTo(new[] { "a", "b" }));
        Assert.That(handler.Handled.All(h => h.Result.Success));
        await worker.StopAsync(default);
    }

    [Test]
    public async Task A_full_queue_makes_producers_wait_instead_of_growing_without_limit()
    {
        var queue = Queue(capacity: 1);
        await queue.EnqueueAsync(Mail("1"));

        var second = queue.EnqueueAsync(Mail("2")).AsTask();
        await Task.Delay(50);
        Assert.That(second.IsCompleted, Is.False, "the second enqueue must wait for space");

        var read = await queue.DequeueAllAsync().FirstAsync();
        Assert.That(read.Id, Is.EqualTo("1"));
        Assert.That(await second, Is.True);
    }

    [Test]
    public async Task In_reject_mode_a_full_queue_returns_false_immediately()
    {
        var queue = Queue(capacity: 1, QueueFullMode.Reject);

        Assert.That(await queue.EnqueueAsync(Mail("1")), Is.True);
        Assert.That(await queue.EnqueueAsync(Mail("2")), Is.False);
    }

    [Test]
    public async Task A_waiting_producer_can_be_cancelled()
    {
        var queue = Queue(capacity: 1);
        await queue.EnqueueAsync(Mail("1"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.CatchAsync<OperationCanceledException>(async () => await queue.EnqueueAsync(Mail("2"), cts.Token));
    }

    [Test]
    public async Task Concurrency_never_exceeds_the_configured_maximum_and_uses_all_of_it()
    {
        var inFlight = 0;
        var peak = 0;
        var done = 0;
        var service = new ScriptedService(async (n, ct) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
                // Retry until the peak is recorded.
            }

            await Task.Delay(20, ct);
            Interlocked.Decrement(ref inFlight);
            Interlocked.Increment(ref done);
            return Accepted(n);
        });
        var queue = Queue();
        var worker = Worker(queue, service, concurrency: 3);
        await worker.StartAsync(default);

        for (var i = 0; i < 30; i++)
        {
            await queue.EnqueueAsync(Mail("n" + i));
        }

        await WaitUntilAsync(() => Volatile.Read(ref done) == 30);
        Assert.That(peak, Is.EqualTo(3));
        await worker.StopAsync(default);
    }

    [Test]
    public async Task Stopping_delivers_everything_already_queued_and_then_refuses_new_notifications()
    {
        var delivered = 0;
        var queue = Queue();
        var worker = Worker(queue, new ScriptedService(async (n, ct) =>
        {
            await Task.Delay(2, ct);
            Interlocked.Increment(ref delivered);
            return Accepted(n);
        }));
        await worker.StartAsync(default);
        for (var i = 0; i < 100; i++)
        {
            await queue.EnqueueAsync(Mail("n" + i));
        }

        await worker.StopAsync(default);

        Assert.That(delivered, Is.EqualTo(100));
        Assert.That(await queue.EnqueueAsync(Mail("late")), Is.False);
    }

    [Test]
    public async Task When_the_shutdown_timeout_runs_out_in_flight_sends_are_cancelled_so_the_host_can_stop()
    {
        var started = new TaskCompletionSource();
        var cancelled = 0;
        var queue = Queue();
        var worker = Worker(queue, new ScriptedService(async (n, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }

            return Accepted(n);
        }), concurrency: 1);
        await worker.StartAsync(default);
        await queue.EnqueueAsync(Mail("slow"));
        await started.Task;

        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await worker.StopAsync(shutdown.Token);

        await WaitUntilAsync(() => Volatile.Read(ref cancelled) == 1);
    }

    [Test]
    public async Task A_notification_that_cannot_be_processed_does_not_stop_the_worker()
    {
        var handler = new RecordingHandler();
        var service = new ScriptedService((n, _) => n.Id == "bad"
            ? throw new ArgumentException("no channels")
            : Task.FromResult(Accepted(n)));
        var queue = Queue();
        var worker = Worker(queue, service, concurrency: 1, handler);
        await worker.StartAsync(default);

        await queue.EnqueueAsync(Mail("bad"));
        await queue.EnqueueAsync(Mail("good"));

        await WaitUntilAsync(() => handler.Handled.Count == 1);
        Assert.That(handler.Handled[0].Notification.Id, Is.EqualTo("good"));
        await worker.StopAsync(default);
    }

    [Test]
    public async Task A_failing_handler_does_not_stop_the_worker_or_the_other_handlers()
    {
        var failing = new RecordingHandler { Throw = _ => new InvalidOperationException("handler bug") };
        var healthy = new RecordingHandler();
        var queue = Queue();
        var worker = Worker(queue, new ScriptedService((n, _) => Task.FromResult(Accepted(n))), 1, failing, healthy);
        await worker.StartAsync(default);

        await queue.EnqueueAsync(Mail("a"));
        await queue.EnqueueAsync(Mail("b"));

        await WaitUntilAsync(() => healthy.Handled.Count == 2);
        await worker.StopAsync(default);
    }

    [Test]
    public async Task Failed_deliveries_are_reported_to_the_handler_with_the_failed_channels()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<RecordingHandler>()
            .AddSingleton<INotificationResultHandler>(sp => sp.GetRequiredService<RecordingHandler>())
            .AddUniversalNotifier(n => n.AddFakeNotifiers().AddBackgroundDelivery())
            .BuildServiceProvider();
        provider.GetRequiredService<FakeEmailNotifier>().Responder = _ =>
            NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Permanent, "x", "no"));
        var hosted = provider.GetServices<IHostedService>().Single();
        await hosted.StartAsync(default);

        await provider.GetRequiredService<INotificationQueue>().EnqueueAsync(Mail("a"));

        var handler = provider.GetRequiredService<RecordingHandler>();
        await WaitUntilAsync(() => handler.Handled.Count == 1);
        Assert.That(handler.Handled[0].Result.Success, Is.False);
        Assert.That(handler.Handled[0].Result[NotificationChannel.Email]!.Errors[0].Code, Is.EqualTo("x"));
        await hosted.StopAsync(default);
    }

    [Test]
    public async Task It_works_end_to_end_through_dependency_injection()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddFakeNotifiers().AddBackgroundDelivery(o => { o.Capacity = 10; o.MaxConcurrency = 2; }))
            .BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().Single();
        await hosted.StartAsync(default);

        await provider.GetRequiredService<INotificationQueue>().EnqueueAsync(Mail("a"));

        var email = provider.GetRequiredService<FakeEmailNotifier>();
        await WaitUntilAsync(() => email.Sent.Count == 1);
        await hosted.StopAsync(default);
    }

    [Test]
    public void A_queue_registered_first_replaces_the_in_memory_one()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<INotificationQueue>(new ChannelNotificationQueue(new BackgroundDeliveryOptions { Capacity = 7 }))
            .AddUniversalNotifier(n => n.AddBackgroundDelivery())
            .BuildServiceProvider();

        Assert.That(provider.GetServices<INotificationQueue>().Count(), Is.EqualTo(1));
    }

    [Test]
    public void Background_delivery_needs_a_host_so_the_standalone_builder_refuses_it() =>
        Assert.Throws<NotSupportedException>(() => new NotifierBuilder().AddBackgroundDelivery());

    [Test]
    public void Invalid_options_are_reported()
    {
        using var provider = new ServiceCollection()
            .AddUniversalNotifier(n => n.AddBackgroundDelivery(o => o.Capacity = 0))
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<INotificationQueue>());
        Assert.Throws<ArgumentException>(() => new ChannelNotificationQueue(new BackgroundDeliveryOptions { MaxConcurrency = 0 }));
    }
}

internal static class AsyncEnumerableExtensions
{
    public static async Task<T> FirstAsync<T>(this IAsyncEnumerable<T> source)
    {
        await foreach (var item in source)
        {
            return item;
        }

        throw new InvalidOperationException("The sequence is empty.");
    }
}
