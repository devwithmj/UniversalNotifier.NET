using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace UniversalNotifier.NET;

/// <summary>
/// The names under which the library publishes traces and metrics. Nothing is recorded unless something listens, so the
/// cost is close to zero by default. With OpenTelemetry:
/// <code>
/// .WithTracing(t =&gt; t.AddSource(NotifierTelemetry.SourceName))
/// .WithMetrics(m =&gt; m.AddMeter(NotifierTelemetry.MeterName, NotifierTelemetry.BackgroundMeterName))
/// </code>
/// Telemetry carries the provider, message type, status and error kind. It never contains recipients, message content or credentials.
/// </summary>
public static class NotifierTelemetry
{
    /// <summary>The <see cref="ActivitySource"/> name for traces.</summary>
    public const string SourceName = "UniversalNotifier.NET";

    /// <summary>The <see cref="Meter"/> name for the core metrics (sends, retries, durations, dispatches).</summary>
    public const string MeterName = "UniversalNotifier.NET";

    /// <summary>The <see cref="Meter"/> name for the background delivery metrics (queue depth, rejected, deduplicated).</summary>
    public const string BackgroundMeterName = "UniversalNotifier.NET.Background";

    internal static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter CoreMeter = new(MeterName);

    internal static readonly Counter<long> Messages =
        CoreMeter.CreateCounter<long>("notifier.messages", "{message}", "Messages sent through a provider, by outcome.");

    internal static readonly Counter<long> Retries =
        CoreMeter.CreateCounter<long>("notifier.retries", "{retry}", "Automatic retries after a failed attempt.");

    internal static readonly Histogram<double> Duration =
        CoreMeter.CreateHistogram<double>("notifier.duration", "ms", "Time from the start of a send to its result, including retries.");

    internal static readonly Counter<long> Notifications =
        CoreMeter.CreateCounter<long>("notifier.notifications", "{notification}", "Notifications dispatched to one or more channels, by overall status.");

    internal static double ElapsedMilliseconds(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
