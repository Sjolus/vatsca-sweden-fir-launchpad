# Contributing

Open an issue before a substantial change so its scope and approach can be discussed. Small bug fixes can go directly to a pull request against `main`.

## Report a problem

Use the **Bug report** template. Include the Launchpad version, Windows version, installer or portable build, and steps to reproduce. Include the .NET runtime version only for a framework-dependent build. Describe the expected and actual results.

If logs help, share only the relevant, redacted excerpts from `%APPDATA%\VatscaUpdateChecker\launchpad.log`. Do not attach real profiles, credentials, browser sessions or recovery backups. Follow [SECURITY.md](SECURITY.md) for vulnerabilities.

## Build and test

Use Windows and the [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).

```powershell
git clone https://github.com/Sjolus/vatsca-sweden-fir-launchpad.git
cd vatsca-sweden-fir-launchpad
dotnet build vatsca-update-checker.sln -c Release
```

Run the harnesses relevant to your change. They use synthetic data and injected installers; do not substitute a real ATC setup or saved credentials. The [CI workflow](.github/workflows/build.yml) lists all required checks. Useful starting points:

- [Launchpad updates](Tests/LaunchpadUpdate.Tests/README.md) and [client updates](Tests/SoftwareUpdate.Tests/README.md)
- [Fresh installs](Tests/FreshSoftwareInstall.Tests/README.md), [EuroScope management](Tests/EuroScopeInstall.Tests/README.md) and [removal/discovery](Tests/AtcRemoval.Tests/README.md)
- [Profile writes](Tests/Profile.Tests/README.md) and [settings persistence](Tests/Settings.Tests/README.md)
- [Isolated WPF layout review](Tests/UiReview/README.md)

For example:

```powershell
dotnet run --project Tests/SoftwareUpdate.Tests/SoftwareUpdate.Tests.csproj -c Release
dotnet build Tests/UiReview/UiReview.csproj -c Release
dotnet Tests/UiReview/bin/Release/net9.0-windows/Launchpad.UiReview.dll --validate-layouts
```

UI changes need both themes, minimum window size, keyboard navigation, and configured/unconfigured paths. Hidden layout checks do not replace desktop testing. Use a controlled test account or VM for native installer, UAC, restart and recovery tests.

## Keep changes focused

- Follow the existing WPF code-behind and service structure; there is no DI container or MVVM framework.
- Discuss new NuGet dependencies first. Velopack is the current packaging/updater dependency.
- Keep secrets out of settings JSON, logs, tests and screenshots. Use fake credentials in fixtures and `CredentialManagerService` for actual saved secrets.
- Keep build outputs, local tool state, test captures and signing keys out of Git.
- Update the relevant user guide when behavior changes. Use [CLAUDE.md](CLAUDE.md) for architecture and repository instructions.

In the pull request, describe the problem, behavior after the change, validation performed and remaining limitations. Use the PR template.

## Package a development build

A standalone, self-contained publish:

```powershell
dotnet publish VatscaUpdateChecker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish-check
```

The project enables native-library extraction and compression for single-file builds. Keep both settings when changing publishing.

For Setup, portable and update packages, use PowerShell 7 on Windows:

```powershell
./scripts/New-DevelopmentCertificate.ps1
./scripts/Build-Installer.ps1 -Version 2.0.0-dev.1
```

The certificate script creates or reuses a non-exportable key in the current user's Personal certificate store. Only its public certificate and thumbprint are saved under `%LOCALAPPDATA%\VatscaUpdateChecker\Signing`. It does not add trust. Never commit or distribute the private key.

The build script restores the pinned `vpk` tool, publishes, signs and verifies the packages. Output is `artifacts/installer/<version>/releases`; use a fresh `-OutputRoot artifacts/<name>` for another build. It does not run Setup or publish a release. Development signatures are not timestamped and do not establish a trusted publisher.

CI builds and verifies development packages on PRs and main builds. Version tags prepare a **draft** release with Setup, the portable ZIP, the full update package and `releases.win-x64.json`. The public certificate, verification reports and other packaging metadata remain in the CI artifact. Prerelease packages are not offered by the production updater.
