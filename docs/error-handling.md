# Error handling

`SendAsync` does not throw for delivery problems. It returns a result, and every failure says whether repeating is safe.

## What throws

Only programmer errors and cancellation:

| Situation | What you get |
|---|---|
| A `null` message or notification | `ArgumentNullException` |
| A notification with no channels | `ArgumentException` |
| The caller's token is cancelled | `OperationCanceledException` |
| Invalid provider options | at startup: `OptionsValidationException` (host) or an exception from `Build()` |
| Invalid templates | `TemplateValidator.ThrowIfInvalid` at startup |

## Reading a result

```csharp
var response = await emailNotifier.SendAsync(message);   // NotifierResponse
if (!response.Success)
{
    foreach (var error in response.Errors)
        logger.LogWarning("Email failed: {Kind} {Code}", error.Kind, error.Code);
}

var result = await notifications.SendAsync(notification);   // NotificationResult
result.Status;                                  // Accepted, PartiallyAccepted or Failed
result[NotificationChannel.Sms]?.Errors;        // one channel's errors
```

`Success` means the provider **accepted** the message, not that it was delivered (see [delivery guarantees](delivery-guarantees.md)).

## Decide by kind

| `NotifierErrorKind` | Meaning | Do |
|---|---|---|
| `Validation` | the message is invalid (bad address, missing subject, a template token without a value) | fix the data; never retry |
| `Configuration` | credentials, region, sender or provider setup is wrong, or no provider is registered | fix the setup; alert |
| `Permanent` | the provider refused it for good (unknown number, unregistered device) | stop; remove dead tokens and subscriptions |
| `Transient` | not accepted, safe to repeat (rate limit, service unavailable, connection refused) | retry later (or enable `WithRetries`) |
| `Unknown` | timeout or dropped connection; the provider may have sent it | do not blindly repeat; a repeat can duplicate |

`Code` is stable and machine-readable (`email.recipients.required`, `sms.number.invalid`, `fcm.unregistered`, `webpush.gone`, `mailgun.rate_limited`, ...). `Message` explains it and never contains credentials, recipient addresses or message content.

## Bulk sends

Per-recipient outcomes are in `response.Recipients`. One bad recipient does not hide the rest:

```csharp
foreach (var r in response.Recipients.Where(r => !r.Accepted))
{
    if (r.Error is { Code: "fcm.unregistered" or "webpush.gone" })
        await tokens.RemoveAsync(r.Recipient);   // the device or browser is gone
}
```

Status is `Accepted` (all), `PartiallyAccepted` (some) or `Failed` (none). A partial success is never retried automatically, because that would message the accepted recipients twice.

## Retries

```csharp
n.WithRetries(maxAttempts: 3);                          // Transient only
n.WithRetries(3, retryUnknownFailures: true);           // also timeouts: may send duplicates
```

Waits double with jitter up to `maxDelay`, and a provider's `Retry-After` takes precedence. `response.RetryAfter` is also available if you schedule retries yourself.

## In the background

Failures from the background worker go to every registered `INotificationResultHandler`: store them, alert, or dead-letter the notification there. See [background delivery](delivery-guarantees.md#background-delivery).

## In your own provider

Return `NotifierResponse.Failed(new NotifierError(kind, "yourprovider.code", "what happened"))` and pick the kind honestly; throw only for unexpected failures (they are classified for you). See [custom providers](custom-provider.md).
