# CLAUDE.md — VATSCA Launchpad

This file captures non-obvious architectural decisions, gotchas, and conventions for this project. Read it before making changes.

> **For Claude:** Keep this file up to date. After completing any non-trivial change, add a brief entry to the [Changelog](#changelog) at the bottom — one line per logical change, dated, describing *what* changed and *why*. If a change invalidates something already documented above, update that section too. The goal is that a future Claude session can read this file and immediately understand the current state of the codebase without needing to re-derive it from scratch.

---

## Project summary

A WPF desktop application for VATSIM Scandinavia controllers. It:
- Checks for updates to EuroScope, GNG Pack, TrackAudio, VACS, and vATIS
- Manages VATSIM profile credentials across EuroScope `.prf` files
- Launches and kills those applications with optional EuroScope profile selection

Target: `.NET 8.0-windows`, WPF, no NuGet dependencies.

---

## Build

```bash
dotnet build vatsca-update-checker.sln
```

There are two project files in the root (`.sln` and `.csproj`), so always specify the `.sln` to avoid MSBuild's "more than one project file" error.

**Publish (self-contained, single file, compressed):**
```bash
dotnet publish VatscaUpdateChecker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/
```
`EnableCompressionInSingleFile` is set in the csproj and activates automatically whenever `PublishSingleFile=true` is passed. Roughly halves the output size.

---

## Architecture

```
Models/       Pure data classes. CheckResult implements INotifyPropertyChanged.
Services/     Static service classes — no DI container.
Converters/   WPF IValueConverter implementations.
Themes/       Light.xaml and Dark.xaml resource dictionaries (hot-swapped at runtime).
```

All windows are code-behind heavy (not full MVVM). That is intentional for this app size — don't add a MVVM framework.

---

## Non-obvious decisions

### Windows-1252 encoding for EuroScope .prf files
EuroScope writes `.prf` files in Windows-1252, not UTF-8. Swedish names (ö, å, ä) corrupt if read/written as UTF-8.

In self-contained .NET builds, legacy encodings are not loaded automatically. **You must call `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` before `Encoding.GetEncoding(1252)`.** This is done in `ProfileService.GetPrfEncoding()`. Never call `Encoding.GetEncoding(1252)` in a static field initialiser — the provider won't be registered yet.

### DynamicResource cannot be used inside Style Trigger Setters
WPF limitation: `{DynamicResource X}` in a `<Setter>` inside a `<DataTrigger>` throws a runtime error. The alternating row background works around this via `RowBackgroundConverter`, which reads `Application.Current.Resources["RowBg"]` / `["RowAltBg"]` at conversion time.

After a theme switch, `AppList.ItemsSource` must be set to `null` then back to `_results` to force the converter to re-evaluate for all rows. See `ThemeToggle_Click`.

### ContextMenu theming
`ContextMenu` opens in its own `Popup` window and doesn't inherit the parent's implicit styles automatically. Both `Light.xaml` and `Dark.xaml` must define implicit styles for `ContextMenu` and `MenuItem`, otherwise they render in the OS default (white) regardless of theme.

### Launching Electron apps (TrackAudio)
TrackAudio is built on Electron. Launching it directly via `Process.Start(exe)` breaks Chromium's renderer and GPU child processes because they inherit our job object restrictions. Always launch it (and everything without a specific profile) via:
```csharp
Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = false });
```

EuroScope with a profile argument is the exception — it uses `UseShellExecute = false` directly so the `.prf` argument is passed correctly.

### TrackAudio pre-release filtering
The GitHub `prerelease` flag on TrackAudio releases is not reliably set. Filter betas by tag name regex (`\d+\.\d+\.\d+-`) in `UpdateChecker` rather than trusting the API field.

### Theme toggle button content
The button uses Unicode symbols: `☽` (dark mode active) and `☀` (light mode active). The button label shows the *current* mode, not what clicking will switch to — keep it that way.

---

## Credentials

Sensitive data is stored in Windows Credential Manager, not in `settings.json`.

| Credential target | Content |
|---|---|
| `VatscaLaunchpad/VATSIM` | VATSIM password |
| `VatscaLaunchpad/Hoppie` | Hoppie ACARS code |

Service: `CredentialManagerService` (P/Invoke on `advapi32.dll`).

---

## Settings file

Stored at `%APPDATA%\VatscaUpdateChecker\settings.json`. All paths and non-secret preferences live here. Do not store passwords here.

---

## Theme system

- `App.xaml` merges `Themes/Light.xaml` as the first `MergedDictionary` at startup.
- `App.SetTheme(isDark)` replaces `MergedDictionaries[0]` at runtime.
- All colour resources are `DynamicResource` in XAML so they update automatically.
- `IsDarkMode` is persisted in `AppSettings` and restored on next launch.

When adding a new themed colour, add it to **both** `Light.xaml` and `Dark.xaml`.

---

## EuroScope profile picker

- `CheckResult.Profiles` is an `ObservableCollection<ProfileOption>` populated only for the EuroScope row.
- `RefreshEuroscopeProfiles()` scans `_settings.EuroscopeDataPath` for `ES*.prf` files.
- The last selected profile path is persisted in `AppSettings.LastEuroscopeProfile`.
- When EuroScope is launched with a profile, it skips its built-in profile selector dialog.
- When launched without a profile (`SelectedProfile.FilePath == null`), it opens normally.

---

## External dependencies (runtime)

| Source | What it's used for |
|---|---|
| `api.github.com` | Latest releases for TrackAudio, VACS, vATIS |
| `files.aero-nav.com/ESAA` | GNG Pack latest version (HTML scrape) |
| Windows Credential Manager | Encrypted credential storage |

The GitHub API is called without authentication. If rate-limiting becomes an issue, add a `User-Agent` header (already present) but consider a token for heavy use.

---

## Things to avoid

- **Don't add a DI container or MVVM framework** — overkill for this app size.
- **Don't use `Encoding.GetEncoding(1252)` without `RegisterProvider` first.**
- **Don't use `DynamicResource` in `<DataTrigger>` / `<Trigger>` setter values** — use a converter instead.
- **Don't store secrets in `settings.json`** — use Credential Manager.
- **Don't `Process.Start` Electron apps directly** — use `explorer.exe` as the launcher.

---

## Changelog

### 2026-03-29
- **Logging** — added `Services/Logger.cs`; appends timestamped `[CHECK]`, `[LAUNCH]`, `[KILL]`, `[APPLY]` entries to `%APPDATA%\VatscaUpdateChecker\launchpad.log`; auto-trims to ~500 lines at 200 KB; never throws. Wired into `UpdateChecker` (per-app version check results + errors), `ProfileService` (per-file patch, Hoppie write, LoginProfiles update), and `MainWindow` (launch, kill). Passwords are never logged — field names only.
- **Silent catch fixed** — `FetchLatestTag` bare `catch {}` now logs the error instead of swallowing it silently.

### 2026-03-24
- **EuroScope profile picker** — replaced profile bar (below row) with a `▾` dropdown button next to Launch; selected profile name shown on button face; selection persisted to `AppSettings.LastEuroscopeProfile`; no-profile option labelled `— No profile —`
- **Window widened** — 740 → 800px (and MinWidth) to give the profile picker button room
- **`EnableCompressionInSingleFile`** — added to csproj, conditional on `PublishSingleFile=true`; roughly halves published `.exe` size
- **`IncludeNativeLibrariesForSelfExtract`** — required for single-file WPF publish; without it, native WPF DLLs (`wpfgfx_cor3.dll` etc.) are placed alongside the exe instead of bundled into it, causing `DllNotFoundException` at runtime when only the exe is distributed
- **`logo-square.png`** — removed unused `<Resource>` entry from csproj (file kept on disk)
- **Orphan `<StackPanel>`** — removed leftover wrapper around row `<Grid>` in DataTemplate (profile bar remnant)
- **Added** `.gitignore`, `CLAUDE.md`, `README.md`, `.github/workflows/build.yml`, issue templates, PR template
- **`*.zip` gitignored** — ESAA update zip and logo zip excluded from version control
