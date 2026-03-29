# Contributing

Thanks for your interest in contributing to VATSCA Launchpad!

## Reporting bugs

Open an issue using the **Bug report** template. Include your Windows version, .NET version, and steps to reproduce. Attach the log file from `%APPDATA%\VatscaUpdateChecker\launchpad.log` if relevant.

## Suggesting features

Open an issue using the **Feature request** template before writing any code, so we can discuss whether it fits the scope of the project.

## Submitting a pull request

1. Fork the repo and create a branch from `main`.
2. Make your changes — see [CLAUDE.md](CLAUDE.md) for architecture notes and things to avoid.
3. Build and test locally:
   ```bash
   dotnet build vatsca-update-checker.sln
   ```
4. Test in both light and dark mode, and with and without tool paths configured.
5. Open a PR against `main` using the pull request template.

## Code style

- Follow the conventions already in the codebase (PascalCase methods, camelCase locals, no DI container, no MVVM framework).
- Do not add NuGet dependencies without discussing it first — keeping the dependency footprint at zero is intentional.
- Do not store secrets or credentials in `settings.json` — use Windows Credential Manager via `CredentialManagerService`.
- See [CLAUDE.md](CLAUDE.md) for a full list of non-obvious decisions and things to avoid.
