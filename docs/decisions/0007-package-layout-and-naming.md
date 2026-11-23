# ADR 0007: Package layout and naming

Status: accepted

## Context
The goal is one familiar primary package, optional provider packages, automatic dependency resolution and no unrelated SDKs.

## Problem
How many packages, named how, and what goes into the core?

## Alternatives
1. **One package with every SDK.** Easy to install, drags MailKit, Firebase and others into every app.
2. **Core plus abstraction-only packages plus providers.** Forces users to install plumbing.
3. **Core with contracts, dispatcher, templates and DI; one package per provider; optional extension packages.**

## Decision
Option 3, named `UniversalNotifier.NET.{Channel}.{Provider}` (`Email.Smtp`, `Email.Mailgun`, `Push.Fcm`). When channel and provider are the same thing, or one provider serves several channels, the channel segment is dropped (`Telegram`, `WebPush`, `Twilio` for SMS and WhatsApp). Extensions: `Background`, `Testing`.
- The core depends only on `Microsoft.Extensions.DependencyInjection(.Abstractions)`, `Options` and `Logging.Abstractions`.
- SMTP is its own package because MailKit is a large dependency; REST providers use plain `HttpClient`.
- Provider `AddX` extension methods live in `Microsoft.Extensions.DependencyInjection` so they are found without extra `using` lines.
- The 1.x package `UniversalNotifier.NET.Mailgun` is superseded by `UniversalNotifier.NET.Email.Mailgun`; it never worked, so it is simply abandoned.
- No `UniversalNotifier.NET.All` meta package until there is demand.
- Libraries target `net10.0` (LTS) and `net11.0`; .NET 8 reached end of support on 10 November 2026.

## Consequences
- Installing a provider installs the core automatically; an app contains only the SDKs it chose. Verified by installing each package into a clean app.

## Trade-offs
- A typical app installs two packages (core plus provider) instead of one.
- Dropping net8.0 excludes applications that have not yet moved to a supported runtime.
