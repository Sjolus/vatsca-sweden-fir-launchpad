# VATSCA Launchpad

A desktop launchpad for [VATSIM Scandinavia](https://vatsca.org) controllers. It keeps your ATC tools up to date, manages your VATSIM profile across all EuroScope configurations, and lets you launch everything from one place.

---

## Features

- **Update checker** — checks EuroScope, GNG Pack, TrackAudio, VACS, and vATIS against their latest releases
- **One-click launch / kill** — start or stop any tool directly from the app
- **EuroScope profile picker** — preselect a `.prf` file so EuroScope opens straight into your sector without the profile dialog
- **Profile sync** — fill in your VATSIM name, CID, password, rating, and Hoppie code once; the app writes them to every EuroScope profile file
- **Dry-run preview** — see exactly which files and lines will change before applying
- **Light / dark theme** — toggle with the ☽ / ☀ button; preference is remembered

---

## Requirements

- Windows 10 / 11
- [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (or use the self-contained build)

---

## Installation

Download the latest release from the [Releases](../../releases) page and run `VatscaUpdateChecker.exe`. No installer needed.

---

## First-time setup

1. Open **Settings** (top-right) and set the paths to your installed tools.
2. Open **App Config** and enter your VATSIM details. Click **Preview** to verify, then **Save & Apply**.
3. Click **Check for Updates** (or enable "Check on startup" in Settings).

---

## Building from source

```bash
git clone https://github.com/Sjolus/vatsca-sweden-fir-launchpad.git
cd vatsca-update-checker
dotnet build vatsca-update-checker.sln
```

**Self-contained single-file publish:**
```bash
dotnet publish VatscaUpdateChecker.csproj -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o publish/
```

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

---

## Project structure

```
Assets/           App icon and logo images
Converters/       WPF value converters
Models/           Data classes (AppSettings, CheckResult, ProfileOption)
Services/
  SettingsService.cs         JSON settings persistence (%APPDATA%)
  UpdateChecker.cs           Version checks (EuroScope, GitHub API, HTML scrape)
  ProfileService.cs          EuroScope .prf file patching (Windows-1252 encoding)
  CredentialManagerService.cs  Encrypted credential storage via Windows Credential Manager
Themes/           Light.xaml and Dark.xaml resource dictionaries
```

---

## Credentials & privacy

Passwords and the Hoppie ACARS code are stored in the **Windows Credential Manager** (the same encrypted store used by browsers and Windows itself). They are never written to disk in plain text.

All other settings live in `%APPDATA%\VatscaUpdateChecker\settings.json`.

The app makes outbound HTTPS requests only to:
- `api.github.com` — latest release info for TrackAudio, VACS, vATIS
- `files.aero-nav.com` — GNG Pack version check

---

## Contributing

Pull requests are welcome. Please open an issue first for anything beyond a small bug fix so we can discuss the approach.

See [CLAUDE.md](CLAUDE.md) for architecture notes and non-obvious implementation details.

---

## License

GPL v3 — see [LICENSE](LICENSE).
