# ADR 0004: Retries are opt-in and conservative

Status: accepted

## Context
Retrying a notification can send it twice. A customer who receives the same SMS or email repeatedly loses trust faster than one who receives it late.

## Problem
When, if ever, should the library repeat a send by itself?

## Alternatives
1. **Retry everything by default.** Simple, produces duplicates after timeouts.
2. **Never retry; leave it to callers.** Safe, but every caller reimplements backoff and Retry-After.
3. **Opt-in retries limited to failures that are provably safe.**

## Decision
Option 3, applied in `NotifierBase`:
- Off unless `MaxAttempts > 1`.
- Only `Transient` errors (the provider did not accept the message): HTTP 429/503, SMTP 4xx, connection refused or unresolved.
- `Unknown` (timeouts, dropped connections, 5xx) only with `retryUnknownFailures: true`, documented as able to duplicate.
- Never after a partial success.
- Equal-jitter exponential backoff; the provider's `Retry-After` takes precedence.
- Third-party clients' own retry is disabled (Web Push) so exactly one policy exists.

## Consequences
- The default cannot duplicate a message through retries.
- Idempotency keys are not implemented yet; callers who need exactly-once should deduplicate by their own notification id.

## Trade-offs
- Some temporary failures that would have succeeded on retry surface to the caller unless retries are enabled.
