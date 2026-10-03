# CLAUDE.md — VATSCA Launchpad

This file captures non-obvious architectural decisions, gotchas, and conventions for this project. Read it before making changes.

> **For Claude:** Keep this file up to date. After completing any non-trivial change, add a brief entry to the [Changelog](#changelog) at the bottom — one line per logical change, dated, describing *what* changed and *why*. If a change invalidates something already documented above, update that section too. The goal is that a future Claude session can read this file and immediately understand the current state of the codebase without needing to re-derive it from scratch.

---

## Project summary

A WPF desktop application for VATSIM Scandinavia controllers. It:
- Checks for updates to EuroScope, GNG Pack, TrackAudio, VACS, and vATIS
- Manages VATSIM profile credentials across EuroScope `.prf` files
- Launches and kills those applications with optional EuroScope profile selection

Target: `net9.0-windows`, WPF. Velopack is an approved dependency for installation and self-updates; keep its SDK and the local `vpk` tool pinned to the same version.

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
Services/     Mostly static services; LaunchpadUpdateService owns update state. No DI container.
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

## Launchpad installation and updates

`Program.Main` is the explicit startup object. `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` must be its first operation, before constructing WPF or reading settings. Installer/packaging hooks exit here without opening windows or starting checks. Staged updates are never automatically applied at startup.

`LaunchpadUpdateService` uses this repository's stable `win-x64` GitHub release feed. It manages installed copies with package ID `SwedenFirLaunchpad`; standalone/development and portable copies use the release-page fallback. Check, download and restart are separate user actions. Only `RestartToApply` invokes the updater, and MainWindow blocks that action during checks, downloads or open/modal dialogs.

Downloads use full packages, checking the expected package ID, version, size and SHA-256. A receipt in Velopack's package cache lets later checks reverify a staged download while offline. The service never infers readiness from a cached filename alone. SDK checks have no cancellation parameter, so the operation gate stays held until a check completes and then honors cancellation. Downloads support cancellation directly. The synthetic regression harness exercises the real SDK with temporary feeds and a locator that forbids process starts/exits; it never applies updates.

The installation root is `%LOCALAPPDATA%\SwedenFirLaunchpad`, separate from the existing `VatscaUpdateChecker` AppData folders. Never reuse the data-folder name as the package ID: Velopack uninstall removes its installation root. Existing settings and Credential Manager targets remain unchanged.

`scripts/Build-Installer.ps1` restores the pinned local `vpk`, publishes self-contained compressed win-x64 output, signs/packages it, and verifies signatures, tamper rejection, metadata and feed hashes. `--framework webview2` prepares the runtime prerequisite for the separately developed GNG browser. The build version is passed to both .NET and Velopack; output must be a fresh directory below `artifacts`.

Development keys are non-exportable and stored in `CurrentUser\My`; only the public certificate and thumbprint go under `%LOCALAPPDATA%\VatscaUpdateChecker\Signing`. Do not import the certificate into Root/TrustedPublisher to suppress warnings. Development signatures have no timestamp or public publisher trust. CI creates an ephemeral certificate to build and verify packages on PRs/main builds; version tags create draft releases for review. Production signing identity/provider remains undecided.

Automated checks do not establish installed upgrade/restart or uninstall behavior. Those smoke tests and both-theme UI checks remain required before the first public installer release.

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

### 2026-10-03
- **Installer and self-updates** — added Velopack Setup/portable/update packages, development self-signing, a hook before WPF startup, explicit download/restart controls, synthetic updater checks and CI package verification. Installation is separate from AppData so upgrades/uninstall preserve user data. Tagged releases are drafts pending review and installer smoke testing.

### 2026-05-10
- **VatEFS plugin support** — added VatEFS as an "Installed"/"Not installed" row in the main list. Path setting in `SettingsWindow` (folder picker, defaults to `C:\Program Files\VatEFS`). "Enable VatEFS plugin" toggle in `AppConfigWindow` with full reconciliation: ON adds `Plugins\tPluginN\t<absolute path>` plus `PluginNDisplayK` rows for each radar screen surface — without the Display rows EuroScope loads the plugin but won't let it draw. Screen names are discovered from existing plugin display rows in the same `.prf`, falling back to "Ground Radar display" + "Standard ES radar screen". OFF removes the VatEFS main + Display rows and renumbers any higher slot indices to keep `Plugin0..PluginM` dense (we don't know if EuroScope tolerates gaps). VatEFS is identified by filename basename (`VatEFS.dll`), so the toggle still works after the user moves the install folder. Untracked when `VatEfsPath` is empty — empty path means the launchpad doesn't touch VatEFS lines either way. New `CheckStatus.Installed` enum value (badge color #41826e green, same as `UpToDate`) for apps detected locally without an online version source.
- **Font verification on GNG Pack row** — small color-coded "Aa" sub-button on the EuroScope (GNG Pack) row indicates whether `EuroScope.ttf` and `SMR ESGG.ttf` are installed at the OS level and at least as new as the source `.ttf` bundled in `<EuroscopeDataPath>\ESAA\`. Green = installed & up-to-date; orange = missing or older than the GNG Pack version; grey = can't determine (data path unset or source `.ttf`s missing). Click → opens `fontview.exe` (Windows' built-in font preview, no admin needed for per-user install) for each font that needs action; user clicks Install / Reinstall in Windows' UI. Filename match is exact (ignores Windows-generated `FOO (1).TTF` duplicate artifacts). **Version comparison reads `head.fontRevision` via WPF's `System.Windows.Media.GlyphTypeface`** — same value Windows Explorer shows in the Properties dialog. Byte-equality was tried first and rejected: same logical font version can ship with different bytes (signing metadata, build timestamps), so byte-compare false-flags freshly-installed fonts. Tolerance of ±0.0001 covers 16.16 fixed-point quantisation noise. "Up to date" rule allows installed-newer-than-source so we don't ask users to downgrade. New `Services/FontService.cs` (record-based result type, sync probe). Wired in via post-step in `MainWindow.RunChecks` after the existing `Task.WhenAll`.
- **VatEFS Launch button via local-port probe** — VatEFS is an EuroScope plugin, so port 17770 is only open while EuroScope is running with the plugin loaded. The row is configured as a web app (`IsWebApp = true`, `LaunchPath = "http://localhost:17770"`) but with a new `IsLocalUrl` flag that gates `ShowLaunch` on `IsLocalUrlReachable`. `MainWindow` snapshots `IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()` once per 3s `_processTimer` tick and matches each `IsLocalUrl` row's URL port against the listener table — chosen over an HTTP/TCP-connect probe specifically because connect-refused on localhost throws first-chance `HttpRequestException` + `TaskCanceledException` on every tick when nothing is listening, which floods the debugger output. The listener-table approach is a single non-throwing kernel call. Match accepts any loopback or wildcard binding (127.0.0.1, ::1, 0.0.0.0, [::]). Clicking launches Edge `--app=http://localhost:17770` in a per-app user-data-dir (`AppDataDir\VatEFSProfile`) — the profile dir is now parameterised by `AppName` (was hardcoded to `VATIRISProfile`); name resolves to the same `VATIRISProfile` for the existing VATIRIS row, so no migration. Kill flow re-uses the existing pid-tracking logic (`{AppName}.pid`).

### 2026-03-30
- **VATIRIS web app row** — added VATIRIS as an `IsWebApp = true` row; launched via `msedge.exe --app=https://vatiris.se --user-data-dir=%APPDATA%\VatscaUpdateChecker\VATIRISProfile` (isolated Edge profile — logins persist but sandboxed from regular Edge). Added `CheckStatus.WebApp` enum value with "Web app" badge text. Web app rows are skipped in update checks and the Checking-status reset loop.
- **Web app PID tracking** — after launch, PID + launch timestamp are written to `%APPDATA%\VatscaUpdateChecker\VATIRIS.pid`. The running-state timer checks the tracked PID; if it has exited (Edge relaunches itself on first-run profile setup), `ProcessHelper.FindEdgeBrowserProcess` scans all `msedge.exe` processes using `CreateToolhelp32Snapshot` and finds the root browser process (the one whose parent is not itself msedge — renderers/GPU/network processes are children of the browser). Kill targets that specific PID with `entireProcessTree: true`, leaving unrelated Edge windows untouched.

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
