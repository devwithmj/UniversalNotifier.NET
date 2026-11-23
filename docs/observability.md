# Observability

The library publishes traces and metrics with `System.Diagnostics` only, so there is no extra dependency. Nothing is recorded unless something listens, and with no listener the cost is a few dozen bytes per send.

Telemetry carries the **provider, message type, status and error kind**. It never contains recipients, message content, credentials or URLs.

## With OpenTelemetry

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(NotifierTelemetry.SourceName))
    .WithMetrics(m => m.AddMeter(NotifierTelemetry.MeterName, NotifierTelemetry.BackgroundMeterName));
```

Any other listener works the same way: `ActivityListener` for traces, `MeterListener` for metrics, or tools such as `dotnet-counters`:

```bash
dotnet-counters monitor --counters UniversalNotifier.NET,UniversalNotifier.NET.Background -p <pid>
```

## Traces

Source: `UniversalNotifier.NET`

| Span | Kind | Tags |
|---|---|---|
| `<provider> send` (for example `mailgun send`) | client | `notifier.provider`, `notifier.message_type`, `notifier.status`, `notifier.attempts`; on failure also `notifier.error_code`, and the span status is Error |
| `notify` (one per `INotificationService.SendAsync`) | internal | `notifier.channels`, `notifier.status` |

A `notify` span is the parent of the `send` spans of its channels, so one notification shows up as one trace with a child per channel. Retries happen inside a single `send` span (see `notifier.attempts`).

## Metrics

Meter `UniversalNotifier.NET`:

| Instrument | Type | Tags | Meaning |
|---|---|---|---|
| `notifier.messages` | counter | `provider`, `message_type`, `status`, `error_kind` | messages sent through a provider, by outcome (`error_kind` is `none` on success) |
| `notifier.duration` | histogram (ms) | same | time from the start of a send to its result, including retries and rate-limit waits |
| `notifier.retries` | counter | `provider`, `message_type` | automatic retries |
| `notifier.notifications` | counter | `status` | notifications dispatched, by overall status |

Meter `UniversalNotifier.NET.Background`:

| Instrument | Type | Meaning |
|---|---|---|
| `notifier.queue.depth` | observable gauge | notifications waiting for delivery |
| `notifier.queue.rejected` | counter | notifications refused because the queue was full or closed |
| `notifier.queue.deduplicated` | counter | notifications dropped as repeats of an idempotency key |

## Useful alerts and dashboards

- Failure rate: `notifier.messages` with `status != Accepted`, split by `provider` and `error_kind`. A rise in `Configuration` means credentials or setup broke; `Transient` means a provider is struggling.
- Latency: p95 of `notifier.duration` per provider.
- Backlog: `notifier.queue.depth` trending up means workers cannot keep up (raise `MaxConcurrency` or check provider latency); `notifier.queue.rejected` above zero means producers are being turned away.
- Retry pressure: `notifier.retries` per provider.

Logging is separate and uses source-generated `ILogger` messages (never recipients or content); see the event ids in `NotifierLog`.
