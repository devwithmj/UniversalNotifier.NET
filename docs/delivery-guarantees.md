# Delivery guarantees

This library tells you what actually happened. It does not claim more.

## What "accepted" means

`NotifierResponse.Success` is true when the **provider accepted the message**: Mailgun returned 200, Twilio returned 201, FCM returned a message id, the SMTP server answered `250` to the message data, the push service answered `201`. That is the strongest statement a synchronous API call can make.

It does **not** mean:

- the email reached an inbox (it can still bounce or be filtered),
- the SMS reached the handset (carriers report that later),
- the device displayed the push notification.

Delivery receipts, bounces and read events arrive through provider callbacks and need an inbound HTTP endpoint, which a client library cannot be. They are intentionally not part of this library: use your provider's webhooks (Mailgun events, Twilio status callbacks, ...) in your own application and match on `ProviderMessageId`.

## The stages you can observe

| Stage | Where you see it |
|---|---|
| Notification invalid | failed result, `Kind = Validation`, nothing was sent |
| Queued for background delivery | `INotificationQueue.EnqueueAsync` returned `true` |
| Provider call made | (logged at debug level) |
| Provider accepted | `Success`, `ProviderMessageId`, per-recipient `Accepted` |
| Provider refused | failed result with a classified error |
| Outcome unknown | `Kind = Unknown` (timeout, dropped connection) |

## Errors decide what to do next

| Kind | Do this |
|---|---|
| `Validation` | Fix the message. Never retry. |
| `Configuration` | Fix credentials, region or sender setup. Never retry. |
| `Permanent` | Stop. The number, address or device is not reachable (remove expired tokens). |
| `Transient` | Safe to repeat: the provider did not accept the message (HTTP 429/503, SMTP 4xx, connection refused). |
| `Unknown` | Do not blindly repeat: the provider may have sent it. |

## Retries

Off by default. `WithRetries(maxAttempts: n)` repeats a send only when:

- the response is a total failure (never after a partial success, which would message accepted recipients twice), and
- every error is `Transient`, or `Unknown` with `retryUnknownFailures: true` (this can send duplicates; use it only for notifications where a duplicate is harmless).

Waits double with jitter up to `maxDelay`. A provider's `Retry-After` hint takes precedence.

Provider APIs do not share an idempotency mechanism, so retries never rely on one: they are limited to failures that are provably safe (above). To make *your own* repeated submissions harmless, give the notification an `IdempotencyKey` and enable `DeduplicationWindow` on background delivery.

## Rate limiting

`WithRateLimit(perSecond, burst)` spaces calls per notifier (a timer-free generic cell rate algorithm on the injectable clock). Calls wait for their turn rather than fail, and the wait honors the caller's cancellation token. It prevents provider 429 responses; it does not replace a provider's account-level limits.

## Background delivery

`EnqueueAsync` returning `true` means **queued**, not delivered. With the built-in in-memory queue:

- Queued notifications are lost if the process crashes or the shutdown timeout expires before they are delivered. Graceful shutdown drains the queue first.
- A full queue makes producers wait (default) or return `false` (`QueueFullMode.Reject`). It never grows without bound.
- With `DeduplicationWindow` set, enqueuing a notification whose `IdempotencyKey` was seen inside the window returns `true` but queues nothing (it is already accepted). A notification that was refused or cancelled does not consume its key. The memory is per process; a durable queue needs its own deduplication.
- Each notification is attempted once by the worker (plus any configured retries). The result, including failures, goes to every registered `INotificationResultHandler`: store it, alert, or dead-letter it there.

For delivery that survives restarts, implement `INotificationQueue` over a database or message broker. `NotificationJson.Serialize` / `Deserialize` store a notification; the JSON contains recipient addresses and message text, so protect it as personal data.

## Per-provider notes

| Provider | "Accepted" is | Bulk behavior |
|---|---|---|
| SMTP | server accepted the DATA | one message per recipient over one connection, per-recipient result |
| Mailgun | API returned 200 (queued) | batch sending, up to 1000 recipients per request; a failed request fails its whole chunk |
| Twilio | API returned 201 (queued) | one request per recipient, a few in parallel |
| Twilio WhatsApp | API returned 201 (queued) | one request per recipient; free-form text outside the 24-hour window is refused (Twilio error 63016), so use an approved template |
| FCM | FCM returned a message id | one message per token, batches of 500, per-token result |
| Telegram | Bot API `ok: true` | one request per chat, a few in parallel |
| Web Push | push service returned 201 | one push per subscription, per-subscription result (`webpush.gone` = remove it) |
| SendGrid | API returned 202 (queued) | one personalization per recipient, up to 1000 per request; a failed request fails its chunk |
| In-app | the store accepted the entry | one entry per user; "delivered" means stored, display is up to your UI |
| Webhook | endpoint answered 2xx | one request per message to a named endpoint; the response body is ignored |

Cc and Bcc are only allowed with a single `To` recipient, because bulk email is delivered as individual messages.
