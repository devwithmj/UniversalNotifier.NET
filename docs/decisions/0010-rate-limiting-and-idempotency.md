# ADR 0010: Per-notifier rate limiting and idempotent background submission

Status: accepted

## Context
Providers answer bursts with 429 responses, and clients (browsers, job runners, webhooks) retry submissions. Both lead to wasted calls or duplicate messages.

## Problem
How should the library keep within provider limits and make repeated submissions harmless, without new infrastructure?

## Alternatives
1. **`System.Threading.RateLimiting` token bucket.** Timers (auto-replenish), one more disposable to own, hard to test with a fake clock.
2. **Leave both to the caller.** Every caller reimplements them.
3. **A tiny generic cell rate algorithm gate on the injectable `TimeProvider`**, and a deduplication window in the in-memory queue.
4. **Rely on provider idempotency keys.** Not portable; several providers have none.

## Decision
Option 3. `WithRateLimit(perSecond, burst)` creates a gate per notifier inside `NotifierBase`; calls wait their turn and the wait honors the caller's token. `Notification.IdempotencyKey` plus `DeduplicationWindow` make `INotificationQueue.EnqueueAsync` accept a repeated key without queueing it again; a notification that was refused or cancelled releases its key.

## Consequences
- No timers or background threads; both behaviors are tested with a fake clock.
- Rate limiting is per notifier instance and per process.
- Deduplication memory is per process and bounded by purging expired keys.

## Trade-offs
- Multiple application instances do not share a limit or a deduplication window. A shared store belongs in a durable queue implementation, behind the same `INotificationQueue` interface.
