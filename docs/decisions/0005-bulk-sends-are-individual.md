# ADR 0005: Bulk sends are individual messages with per-recipient results

Status: accepted

## Context
1.x added every recipient to a single message's `to`, so each recipient saw everyone else's address. It also reported one result for the whole send.

## Problem
How should a message with several recipients be delivered and reported?

## Alternatives
1. **One message, many `to` addresses.** Leaks addresses, one failure hides the rest.
2. **Library chooses per provider silently.** Surprising.
3. **Several `To` recipients always mean individual messages; report each recipient.**

## Decision
Option 3. Mailgun uses batch sending with `recipient-variables`, SMTP sends one message per recipient over one connection, Twilio, Telegram and Web Push send one request per recipient with bounded parallelism, FCM sends one message per token in batches of 500. Every provider returns a `RecipientResult` per recipient.
Cc and Bcc are only allowed with a single `To` recipient, because they have no meaning for individually delivered bulk messages.

## Consequences
- No recipient ever sees another's address.
- One bad address or expired token does not block the others, and callers learn exactly which tokens or subscriptions to remove.
- Overall status is `Accepted`, `PartiallyAccepted` or `Failed`.

## Trade-offs
- A few more allocations and requests than a single multi-recipient message. A user who really wants a visible group email sends one message with a single `To` and Cc recipients.
