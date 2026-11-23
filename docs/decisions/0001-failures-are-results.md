# ADR 0001: Delivery failures are results, not exceptions

Status: accepted

## Context
Notifications are sent from request handlers and background workers. Providers fail in many ordinary ways: a mistyped phone number, an expired device token, a rate limit, a timeout.

## Problem
How should a send report that it did not succeed?

## Alternatives
1. **Throw on any failure.** Familiar, but every caller needs try/catch, provider exceptions leak into application code, and a bad recipient in a bulk send hides the other recipients.
2. **Return `bool`.** Loses the reason and whether repeating is safe.
3. **Return a result object** with a classified error list and per-recipient outcomes.

## Decision
`SendAsync` returns an immutable `NotifierResponse`. Only programmer errors (a `null` message) and cancellation throw. Errors carry a `NotifierErrorKind` (Validation, Configuration, Permanent, Transient, Unknown).

## Consequences
- Callers check `Success` and can branch on the kind (fix, stop, retry, investigate).
- One error model across every provider, enforced by `NotifierBase` and the shared contract tests.
- Bulk sends report each recipient, so expired tokens and subscriptions can be removed.

## Trade-offs
- A caller who ignores the result ignores the failure. The result type is explicit and `Success` is the first property, which makes this hard to do by accident.
- Provider SDK exceptions are flattened into messages; unexpected ones keep their type name and message in an `Unknown` error.
