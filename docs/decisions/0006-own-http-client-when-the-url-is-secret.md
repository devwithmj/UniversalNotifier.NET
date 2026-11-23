# ADR 0006: Providers own their HTTP client when the URL is a secret

Status: accepted

## Context
`IHttpClientFactory` logs each outgoing request URI by default. Telegram puts the bot token in the URL path (`/bot<token>/sendMessage`); a Web Push endpoint URL identifies a user's browser.

## Problem
How do we use HTTP idiomatically without writing secrets to logs?

## Alternatives
1. **Use `IHttpClientFactory` everywhere and tell users to configure redaction.** Unsafe by default.
2. **Remove the factory's logging handlers globally.** Changes behavior of the whole application.
3. **Mailgun and Twilio use the factory (their URLs hold no secrets); Telegram and Web Push create their own `HttpClient`** on a `SocketsHttpHandler` with a 5-minute `PooledConnectionLifetime`.

## Decision
Option 3. Tests assert the token and recipient data never appear in captured logs.

## Consequences
- Safe by default; DNS changes are still picked up.
- Mailgun and Twilio stay configurable (primary handler, Polly, proxies) through the factory.

## Trade-offs
- Telegram and Web Push clients are not shaped by the application's `IHttpClientFactory` configuration. An internal constructor accepting a handler keeps them testable.
