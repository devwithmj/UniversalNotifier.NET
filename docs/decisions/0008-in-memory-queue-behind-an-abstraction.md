# ADR 0008: A bounded in-memory queue behind `INotificationQueue`

Status: accepted

## Context
Background delivery is required, but no database or broker may be mandatory.

## Problem
What should ship in the box, and how does a durable queue plug in?

## Alternatives
1. **Unbounded in-memory queue.** Simple, grows without limit under load.
2. **Require a broker.** Violates "no mandatory infrastructure".
3. **Bounded `Channel<T>` queue behind an interface, with a result-handler hook.**

## Decision
Option 3. `INotificationQueue` has `EnqueueAsync`, `DequeueAllAsync` and `Complete`. The built-in queue is a bounded `Channel<T>`: a full queue makes producers wait (backpressure, default) or return `false` (`QueueFullMode.Reject`). A hosted worker delivers with `MaxConcurrency` workers, drains on shutdown until the host timeout, then cancels in-flight sends. `Notification` is immutable data and `NotificationJson` round-trips it, so durable adapters can store it.

## Consequences
- Memory is bounded and the behavior under load is explicit.
- Delivery guarantees are honest: "enqueued" is not "delivered", and the in-memory queue loses queued items if the process crashes (documented).

## Trade-offs
- Guaranteed delivery needs the user to implement `INotificationQueue` over their store. Official adapters can be added later as separate packages.
