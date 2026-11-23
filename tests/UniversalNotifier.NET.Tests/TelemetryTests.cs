using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using UniversalNotifier.NET.Background;
using UniversalNotifier.NET.Testing;

namespace UniversalNotifier.NET.Tests;

[TestFixture]
[NonParallelizable] // listeners observe process-wide sources, so keep these tests apart from the others
public class TelemetryTests
{
    private sealed record Measurement(string Instrument, double Value, Dictionary<string, object?> Tags);

    private sealed class Probe : IDisposable
    {
        private readonly ActivityListener _activities;
        private readonly MeterListener _meters = new();
        private readonly List<Measurement> _measurements = [];

        public Probe()
        {
            Activities = [];
            _activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == NotifierTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    lock (Activities)
                    {
                        Activities.Add(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_activities);

            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name is NotifierTelemetry.MeterName or NotifierTelemetry.BackgroundMeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meters.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
            _meters.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
            _meters.SetMeasurementEventCallback<int>((i, v, t, _) => Record(i, v, t));
            _meters.Start();
        }

        public List<Activity> Activities { get; }

        public IReadOnlyList<Measurement> Measurements
        {
            get
            {
                _meters.RecordObservableInstruments();
                lock (_measurements)
                {
                    return [.. _measurements];
                }
            }
        }

        public void Dispose()
        {
            _activities.Dispose();
            _meters.Dispose();
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            lock (_measurements)
            {
                _measurements.Add(new Measurement(instrument.Name, value, copy));
            }
        }
    }

    private sealed class ScriptedSms(NotifierContext context, Func<NotifierResponse> respond) : NotifierBase<SmsMessage>(context)
    {
        protected override string ProviderName => "scripted";

        protected override Task<NotifierResponse> SendCoreAsync(SmsMessage message, CancellationToken cancellationToken) => Task.FromResult(respond());
    }

    private static readonly SmsMessage Message = SmsMessage.Create("+14165550177", "secret body text");

    [Test]
    public async Task A_successful_send_emits_a_span_and_metrics_tagged_by_provider_and_outcome()
    {
        using var probe = new Probe();
        var notifier = new ScriptedSms(new NotifierContext(), () => NotifierResponse.Accepted("id-1"));

        await notifier.SendAsync(Message);

        var span = probe.Activities.Single(a => a.OperationName == "scripted send");
        Assert.That(span.Kind, Is.EqualTo(ActivityKind.Client));
        Assert.That(span.GetTagItem("notifier.provider"), Is.EqualTo("scripted"));
        Assert.That(span.GetTagItem("notifier.message_type"), Is.EqualTo("SmsMessage"));
        Assert.That(span.GetTagItem("notifier.status"), Is.EqualTo("Accepted"));
        Assert.That(span.GetTagItem("notifier.attempts"), Is.EqualTo(1));
        Assert.That(span.Status, Is.Not.EqualTo(ActivityStatusCode.Error));

        var sent = probe.Measurements.Single(m => m.Instrument == "notifier.messages");
        Assert.That(sent.Value, Is.EqualTo(1));
        Assert.That(sent.Tags["provider"], Is.EqualTo("scripted"));
        Assert.That(sent.Tags["status"], Is.EqualTo("Accepted"));
        Assert.That(sent.Tags["error_kind"], Is.EqualTo("none"));
        Assert.That(probe.Measurements.Single(m => m.Instrument == "notifier.duration").Value, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public async Task A_failed_send_marks_the_span_as_an_error_and_counts_the_error_kind()
    {
        using var probe = new Probe();
        var notifier = new ScriptedSms(new NotifierContext(), () => NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Permanent, "scripted.rejected", "no")));

        await notifier.SendAsync(Message);

        var span = probe.Activities.Single();
        Assert.That(span.Status, Is.EqualTo(ActivityStatusCode.Error));
        Assert.That(span.GetTagItem("notifier.error_code"), Is.EqualTo("scripted.rejected"));
        var sent = probe.Measurements.Single(m => m.Instrument == "notifier.messages");
        Assert.That(sent.Tags["status"], Is.EqualTo("Failed"));
        Assert.That(sent.Tags["error_kind"], Is.EqualTo("Permanent"));
    }

    [Test]
    public async Task Validation_failures_are_counted_without_calling_the_provider()
    {
        using var probe = new Probe();
        var calls = 0;
        var notifier = new ScriptedSms(new NotifierContext(), () =>
        {
            calls++;
            return NotifierResponse.Accepted();
        });

        await notifier.SendAsync(new SmsMessage());

        Assert.That(calls, Is.Zero);
        var sent = probe.Measurements.Single(m => m.Instrument == "notifier.messages");
        Assert.That(sent.Tags["error_kind"], Is.EqualTo("Validation"));
        Assert.That(probe.Activities.Single().GetTagItem("notifier.attempts"), Is.EqualTo(0));
    }

    [Test]
    public async Task Retries_are_counted_and_the_span_records_the_attempts()
    {
        using var probe = new Probe();
        var options = new NotifierOptions();
        options.Retry.MaxAttempts = 3;
        options.Retry.BaseDelay = TimeSpan.FromMilliseconds(1);
        var calls = 0;
        var notifier = new ScriptedSms(new NotifierContext(options), () => ++calls < 3
            ? NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Transient, "t", "later"))
            : NotifierResponse.Accepted());

        await notifier.SendAsync(Message);

        Assert.That(probe.Measurements.Where(m => m.Instrument == "notifier.retries").Sum(m => m.Value), Is.EqualTo(2));
        Assert.That(probe.Activities.Single().GetTagItem("notifier.attempts"), Is.EqualTo(3));
    }

    [Test]
    public async Task The_dispatcher_emits_one_span_and_one_count_per_notification()
    {
        using var probe = new Probe();
        using var provider = new ServiceCollection().AddUniversalNotifier(n => n.AddFakeNotifiers()).BuildServiceProvider();

        await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To(new Recipient { Email = "ada@example.com", Phone = "+14165550177" }).Email("Hi", text: "x").Sms("Hi"));

        var notify = probe.Activities.Single(a => a.OperationName == "notify");
        Assert.That(notify.GetTagItem("notifier.channels"), Is.EqualTo(2));
        Assert.That(notify.GetTagItem("notifier.status"), Is.EqualTo("Accepted"));
        Assert.That(probe.Measurements.Single(m => m.Instrument == "notifier.notifications").Tags["status"], Is.EqualTo("Accepted"));
    }

    [Test]
    public async Task Telemetry_never_contains_recipients_message_content_or_credentials()
    {
        using var probe = new Probe();
        var notifier = new ScriptedSms(new NotifierContext(), () => NotifierResponse.Failed(new NotifierError(NotifierErrorKind.Permanent, "x", "no")));
        using var provider = new ServiceCollection().AddUniversalNotifier(n => n.AddFakeNotifiers()).BuildServiceProvider();

        await notifier.SendAsync(Message);
        await provider.GetRequiredService<INotificationService>().SendAsync(
            Notification.To(new Recipient { Email = "private-person@example.com" }).Email("secret subject", text: "secret body text"));

        var everything = string.Join(
            '\n',
            probe.Activities.SelectMany(a => a.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(a.DisplayName))
                .Concat(probe.Measurements.SelectMany(m => m.Tags.Select(t => $"{t.Key}={t.Value}"))));

        Assert.That(everything, Is.Not.Empty);
        foreach (var sensitive in new[] { "+14165550177", "private-person@example.com", "secret body text", "secret subject" })
        {
            Assert.That(everything, Does.Not.Contain(sensitive), sensitive);
        }
    }

    [Test]
    public async Task Without_listeners_nothing_is_recorded_and_sending_still_works()
    {
        var notifier = new ScriptedSms(new NotifierContext(), () => NotifierResponse.Accepted());

        var response = await notifier.SendAsync(Message);

        Assert.That(response.Success, Is.True);
        Assert.That(Activity.Current, Is.Null);
    }

    [Test]
    public async Task The_queue_publishes_its_depth_and_counts_rejected_and_deduplicated_notifications()
    {
        using var probe = new Probe();
        using var queue = new ChannelNotificationQueue(new BackgroundDeliveryOptions
        {
            Capacity = 2,
            FullMode = QueueFullMode.Reject,
            DeduplicationWindow = TimeSpan.FromMinutes(1),
        });
        Notification Mail(string key) => Notification.To("ada@example.com").Email("Hi", text: "x").WithIdempotencyKey(key);

        await queue.EnqueueAsync(Mail("a"));
        await queue.EnqueueAsync(Mail("a"));
        await queue.EnqueueAsync(Mail("b"));
        await queue.EnqueueAsync(Mail("c"));

        var measurements = probe.Measurements;
        Assert.That(measurements.Last(m => m.Instrument == "notifier.queue.depth").Value, Is.EqualTo(2));
        Assert.That(measurements.Where(m => m.Instrument == "notifier.queue.deduplicated").Sum(m => m.Value), Is.EqualTo(1));
        Assert.That(measurements.Where(m => m.Instrument == "notifier.queue.rejected").Sum(m => m.Value), Is.EqualTo(1));
    }
}
