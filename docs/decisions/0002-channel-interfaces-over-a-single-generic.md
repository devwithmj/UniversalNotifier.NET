# ADR 0002: Channel interfaces over a single-parameter generic

Status: accepted

## Context
1.x had `INotifier<TReceiver, TContent>`: closed per channel, with no common type. Application code could not hold "a notifier", and a multi-channel dispatcher had nothing to route to. The goal is `ISmsNotifier : INotifier` implemented by Twilio and others.

## Problem
What shape lets application code depend on a channel, lets providers be swapped, and still allows one dispatcher over all channels?

## Alternatives
1. **Non-generic `INotifier` taking a base message.** Each channel interface would have to accept only its own message type, forcing runtime type checks and breaking substitutability.
2. **Two type parameters (receiver, content).** The pairing has to be kept in sync by hand; DI registrations are awkward.
3. **`INotifier<in TMessage>` plus empty-bodied channel interfaces** (`IEmailNotifier : INotifier<EmailMessage>`).

## Decision
Option 3. A message carries its own recipients and content. A channel interface exists only where the message shape differs between channels.

## Consequences
- Application code depends on `IEmailNotifier`, `ISmsNotifier`, ... and never on a vendor.
- Swapping a provider is a DI registration change.
- `INotificationService` can route one `Notification` to each channel interface.

## Trade-offs
- One small interface per channel. They are justified by differing message types (subject and HTML, short text, title and data, chat ids, subscriptions); do not add one that duplicates another.
