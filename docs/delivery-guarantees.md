# Delivery guarantees

This library tells you what actually happened. It does not claim more.

## What "accepted" means

`NotifierResponse.Success` is true when the **provider accepted the message**: Mailgun returned 200, Twilio returned 201, FCM returned a message id, the SMTP server answered `250` to the message data, the push service answered `201`. That is the strongest statement a synchronous API call can make.

It does **not** mean:

- the email reached an inbox (it can still bounce or be filtered),
- the SMS reached the handset (carriers report that later),
- the device displayed the push notification.

Delivery receipts, bounces and read events arrive through provider webhooks. They are not part of this library yet; use your provider's webhooks and match on `ProviderMessageId`.

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

There is no idempotency-key support yet. If duplicates matter, keep `retryUnknownFailures` off and deduplicate by your own notification id.

## Background delivery

`EnqueueAsync` returning `true` means **queued**, not delivered. With the built-in in-memory queue:

- Queued notifications are lost if the process crashes or the shutdown timeout expires before they are delivered. Graceful shutdown drains the queue first.
- A full queue makes producers wait (default) or return `false` (`QueueFullMode.Reject`). It never grows without bound.
- Each notification is attempted once by the worker (plus any configured retries). The result, including failures, goes to every registered `INotificationResultHandler`: store it, alert, or dead-letter it there.

For delivery that survives restarts, implement `INotificationQueue` over a database or message broker. `NotificationJson.Serialize` / `Deserialize` store a notification; the JSON contains recipient addresses and message text, so protect it as personal data.

## Per-provider notes

| Provider | "Accepted" is | Bulk behavior |
|---|---|---|
| SMTP | server accepted the DATA | one message per recipient over one connection, per-recipient result |
| Mailgun | API returned 200 (queued) | batch sending, up to 1000 recipients per request; a failed request fails its whole chunk |
| Twilio | API returned 201 (queued) | one request per recipient, a few in parallel |
| FCM | FCM returned a message id | one message per token, batches of 500, per-token result |
| Telegram | Bot API `ok: true` | one request per chat, a few in parallel |
| Web Push | push service returned 201 | one push per subscription, per-subscription result (`webpush.gone` = remove it) |

Cc and Bcc are only allowed with a single `To` recipient, because bulk email is delivered as individual messages.
