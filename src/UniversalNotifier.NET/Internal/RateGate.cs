namespace UniversalNotifier.NET;

/// <summary>
/// Spaces calls so a provider is never hit faster than its limit (generic cell rate algorithm). It needs no timers or
/// background threads, uses the injectable <see cref="TimeProvider"/>, and lets a burst through up to the configured size.
/// </summary>
internal sealed class RateGate
{
    private readonly long _intervalTicks;
    private readonly long _burstToleranceTicks;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private long _theoreticalArrivalTicks;

    public RateGate(RateLimitOptions options, TimeProvider time)
    {
        _intervalTicks = (long)(TimeSpan.TicksPerSecond / options.PermitsPerSecond);
        _burstToleranceTicks = _intervalTicks * (options.Burst - 1);
        _time = time;
    }

    /// <summary>Waits, if needed, until the caller may make a call.</summary>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (_lock)
        {
            var now = _time.GetUtcNow().UtcTicks;
            var next = Math.Max(_theoreticalArrivalTicks, now) + _intervalTicks;
            var allowedAt = next - _burstToleranceTicks - _intervalTicks;
            _theoreticalArrivalTicks = next;
            delay = TimeSpan.FromTicks(Math.Max(0, allowedAt - now));
        }

        return delay > TimeSpan.Zero ? Task.Delay(delay, _time, cancellationToken) : Task.CompletedTask;
    }
}
