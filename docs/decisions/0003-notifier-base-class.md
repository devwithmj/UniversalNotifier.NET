# ADR 0003: A sealed template method in `NotifierBase`

Status: accepted

## Context
Every provider needs the same behavior around its API call: validate the message, honor cancellation, log without leaking, map exceptions to errors, retry when asked.

## Problem
How do we make that behavior identical across providers, including ones written by other people?

## Alternatives
1. **Interface only.** Each provider re-implements the rules; they drift and some forget.
2. **Decorators** (a retry notifier wrapping a notifier). Needs one decorator per channel interface and awkward DI decoration.
3. **Base class with a sealed `SendAsync`** that calls an abstract `SendCoreAsync`.

## Decision
Option 3. `NotifierBase<TMessage>.SendAsync` validates, logs, calls `SendCoreAsync`, converts unexpected exceptions with an overridable `Classify`, rethrows only caller cancellation, and applies the retry policy.

## Consequences
- A provider is the API call and its error mapping, nothing else.
- Behavior is verified once by `ProviderContractTests`, which every provider fixture runs.

## Trade-offs
- Providers inherit a class. The public contract is still the interface, so consumers and tests are unaffected.
