# Security Policy

## Reporting a vulnerability

Do not post vulnerability details, exploit code or private data in a public issue or pull request.

Use **Report a vulnerability** on the repository's [Security page](https://github.com/Sjolus/vatsca-sweden-fir-launchpad/security). If that option is unavailable, open an issue asking for a private contact, without describing the vulnerability.

Once a private channel is agreed, include affected versions, reproduction steps and potential impact. Use synthetic data and omit real credentials, profiles and browser sessions.

## Scope

Launchpad is a local desktop application. Security-sensitive behavior includes:

- Saving passwords and Hoppie codes in Windows Credential Manager, and explicitly writing them into external profile/plugin text files.
- Reading and changing configured files, and creating recovery exports that may contain sensitive data.
- Parsing release metadata and archives, verifying downloads and invoking installers or uninstallers.
- Keeping dedicated browser profiles that can contain sign-in sessions.

External profiles and recovery copies can contain plain-text secrets even though Launchpad's saved secrets are kept outside `settings.json`. Do not include them in public bug reports. Removing Launchpad alone does not erase those copies.
