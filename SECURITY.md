# Security Policy

## Reporting a vulnerability

Please **do not** open a public GitHub issue for security vulnerabilities.

Instead, report them privately via [GitHub's private vulnerability reporting](../../security/advisories/new) or by emailing the maintainer directly.

Include as much detail as you can: steps to reproduce, potential impact, and any suggested fix if you have one. You'll receive a response within a few days.

## Scope

This is a local desktop application with no server component. The main security-sensitive areas are:

- **Credential handling** — VATSIM password and Hoppie code are stored in Windows Credential Manager and should never appear in log files, settings, or error messages.
- **File patching** — the app writes to EuroScope `.prf` files and `LoginProfiles.txt`; unexpected file paths or content could cause unintended writes.
- **External requests** — the app fetches data from `api.github.com` and `files.aero-nav.com`; any response parsing that could lead to code execution or path traversal is in scope.
