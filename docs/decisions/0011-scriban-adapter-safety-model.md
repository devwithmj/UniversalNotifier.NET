# ADR 0011: Scriban as an optional renderer with an explicit data model

Status: accepted

## Context
The built-in renderer is intentionally small. Some teams need conditionals, loops and filters. Scriban is a capable, maintained engine, but its documentation is clear that it is not a security boundary around arbitrary .NET objects.

## Problem
How do we offer a richer engine without giving templates a way to run arbitrary code or read sensitive members?

## Alternatives
1. **Bundle Scriban in the core.** Adds a dependency for everyone and a larger attack surface.
2. **Pass user objects straight to Scriban.** Templates can reach every public member.
3. **Optional package; build the model from plain values; enforce execution limits.**

## Decision
Option 3. `UniversalNotifier.NET.Templates.Scriban` converts template data into plain script objects, arrays and primitives (a snapshot of public properties, dictionaries, enumerables and JSON elements, at most 8 levels deep), so templates read data and never call .NET members. It sets loop, recursion, string and output limits, disables `include` (no template loader), treats an undefined variable as an error by default, and HTML-encodes every string value in HTML parts before the template sees it.

## Consequences
- Existing `{{Name}}` templates keep working; conditionals, loops and filters become available.
- Queued notifications render the same after a JSON round trip.

## Trade-offs
- Names are case-sensitive (the built-in renderer is not).
- There is no raw (unencoded) output in HTML parts; trusted HTML must be assembled before it is passed as data, or a part should use the built-in renderer's `{{{Raw}}}`.
- Scriban's own `html.escape` on already-encoded values would double-encode, so it should not be used in HTML parts.
