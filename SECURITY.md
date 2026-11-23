# Security policy

## Supported versions

Security fixes go to the latest release. Preview versions are supported only until the next preview or stable release.

| Version | Supported |
|---|---|
| 2.x (latest) | yes |
| 1.x | no. These packages could not send messages and should not be used. |

## Reporting a vulnerability

Please do not open a public issue for a security problem.

Report it privately through GitHub: **Security tab, then "Report a vulnerability"** on this repository. Include what you found, how to reproduce it and the version. You can expect an acknowledgement within a few days and a fix or a clear plan after triage. Credit is given unless you ask otherwise.

## What counts

Things we treat as security issues include:

- credentials, tokens, webhook or subscription URLs, recipient addresses or message content appearing in logs, errors or `ToString()` output;
- header or recipient injection through message fields or display names;
- one recipient being able to see another in a bulk send;
- template injection or a template reaching .NET members or the file system (built-in, Scriban and Fluid renderers);
- path traversal in the directory template store;
- unsafe defaults, such as sending credentials over an unencrypted connection.

## For users

- Keep API keys, tokens, SMTP passwords, service account files and webhook URLs in a secret store, not in source control.
- Prefer scoped API keys (for example a Twilio API key rather than the account token).
- Notifications serialized with `NotificationJson` contain recipient addresses and message text; protect stored queue payloads as personal data.
- Keep dependencies current; this repository runs a dependency vulnerability scan in CI.
