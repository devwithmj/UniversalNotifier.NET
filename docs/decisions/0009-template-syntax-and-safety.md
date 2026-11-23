# ADR 0009: Small, safe template syntax

Status: accepted

## Context
1.x accepted `%key%`, `{key}` and `{{key}}`, replaced values inside the loop that matched them (so a value containing a token was substituted again), and never encoded HTML.

## Problem
What should the built-in renderer do, and what is left to optional engines?

## Alternatives
1. **Embed a full template language.** Powerful, large dependency and a code-execution surface.
2. **Keep all three token styles.** Ambiguous with CSS and JavaScript braces.
3. **One simple token syntax, single pass, safe by default; engines are pluggable.**

## Decision
Option 3. Only `{{Token}}` (nested `{{User.Name}}`, raw `{{{Token}}}`). Values are written in a single pass and never re-scanned. HTML parts encode token values. A missing token is an error by default. Text that looks like CSS (`{{ color: red }}`) stays literal. Templates are parsed once and cached. `ITemplateRenderer` and `ITemplateStore` are replaceable (Scriban, Fluid, a database).

## Consequences
- No expressions, loops or code execution in the built-in renderer, so no injection through templates.
- Queued notifications render identically after a JSON round trip (JSON elements and booleans are handled).

## Trade-offs
- No conditionals or loops in the built-in engine; users who need them plug in a richer engine.
- Removing `%key%` and `{key}` is a breaking change, documented in the migration guide.
