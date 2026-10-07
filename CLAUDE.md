# CLAUDE.md — VATSCA Launchpad

This file describes the current implementation, its design constraints and known limits. Keep it focused on architecture rather than test-session results.

Update the relevant architectural section when behavior changes. Keep execution plans in ignored `.codex/plans/`, research in `.codex/research/`, and logs or session handoffs in local notes; use Git history for the change chronology.

---

## Repository hygiene and approvals

Keep shared `AGENTS.md`/`CLAUDE.md` instructions, architectural decisions, user/developer documentation and synthetic test source in Git. Store planning/checklist documents, session transcripts, screenshots, generated test evidence and candidate inventories under ignored `.codex/` or `artifacts/` directories. Never commit credentials, runtime settings/profiles, browser data, recovery exports or signing keys.

Ask before making a Git commit and before starting a desktop or native installer/client test session. Prepare a reviewable diff or test plan first, and keep any approved session within its agreed scope. Implementation or a passing build does not authorize publication or desktop control.

## Project summary

A WPF desktop application for VATSIM Scandinavia controllers. It:

- Checks EuroScope against the pinned supported build and checks GNG, TrackAudio, VACS, vATIS and Launchpad releases
- Manages VATSIM profile credentials across EuroScope `.prf` files
- Launches and kills those applications with optional EuroScope profile selection
- Offers optional guided setup, reviewed adoption of existing paths, and explicit client installation/update
- Reviews application removal, optional data reset and recovery exports before destructive actions

Target: `net9.0-windows`, WPF. Velopack is an approved dependency for installation and self-updates; keep its SDK and the local `vpk` tool pinned to the same version.

---

## Build

```powershell
dotnet build vatsca-update-checker.sln -c Release
```

The root contains both a solution and a project file. Specify one explicitly when building; use the solution command above for the normal build.

**Publish (self-contained, single file, compressed):**

```powershell
dotnet publish VatscaUpdateChecker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish-check
```
The project enables `IncludeNativeLibrariesForSelfExtract` and `EnableCompressionInSingleFile` when `PublishSingleFile=true`. Retain both for the distributed WPF executable. Use a fresh output directory for each package candidate.

---

## Architecture

```
Models/       Pure data classes. CheckResult implements INotifyPropertyChanged.
Services/     Static helpers and stateful operation services with injectable test seams; no DI container.
Converters/   WPF IValueConverter implementations.
Themes/       Light.xaml and Dark.xaml resource dictionaries (hot-swapped at runtime).
```

Windows use code-behind intentionally; do not introduce a DI container or MVVM framework. MainWindow's eight-row initialization, launch-path assignment and check results share positional indices. Keep them aligned when changing rows. Partial MainWindow files isolate the larger operation workflows.

### Explicit third-party updates

`SoftwareReleaseSource` reads official VACS/TrackAudio GitHub metadata and vATIS's Windows feed. `SoftwareVersion` retains full prerelease ordering. `SoftwareUpdateService` coordinates explicit checks versus updates; neither construction nor checking installs anything. `SoftwareInstaller` recognizes existing installations and invokes each vendor's silent/no-restart route. The vATIS adapter must never use Setup over an existing root: that can delete profiles. Its installed helper requires a recoverable backup outside the root.

Registered and executable versions must agree before a VACS/TrackAudio update. A trailing zero fourth version component is normalized (`1.4.0.0` matches `1.4.0`); genuinely different versions retain the manual fallback and its message names both values. A product-identity mismatch has a separate explanation. Checking never rewrites Windows registration to make a mismatch disappear.

`MainWindow.SoftwareUpdates.cs` binds per-row progress and serializes software updates against settings/profile dialogs and Launchpad self-updates. Download/preparation cancellation is allowed; applying is not interrupted and closing is blocked until completion. No application auto-launch is requested. Unknown layouts use a vendor-download fallback. Vendor elevation/prerequisite prompts remain possible. The synthetic `Tests/SoftwareUpdate.Tests` harness must not launch processes or read real client data; run it in CI alongside the existing self-updater harness.

This work shipped in 2.0.0, which also includes reviewed removal/reset and discovery of existing installations. The additional font check (#3) and old/new-package cleanup (#4) are separate follow-ups ahead of authenticated GNG installation. Full GNG package removal in the 2.0 maintenance dialog is separate from issue #4's selective obsolete-file cleanup.

### Existing installations and maintenance

Product direction: make Launchpad the common entry point for Swedish controllers without forcing existing users to migrate. Path adoption, configuration import/reuse, software replacement and data reset are distinct decisions. A saved path enables checking/launching; it does not authorize installation or deletion. Keep the legacy/manual path useful for copies that cannot be managed safely. Full GNG onboarding remains a separate feature.

`FreshSoftwareInstallService` and `FreshSoftwareInstallWindow` handle explicitly confirmed fresh VACS/vATIS/TrackAudio installation separately from upgrade-only adapters. Preview selects official release metadata and validates absence, installation scope, destination and retained data; it does not download the installer. Existing copies/residue direct the user to adoption or reviewed removal. Fresh vATIS beta selection is an explicit flag, never a fake installed version; the verified Setup adapter refuses its existing root. `SoftwarePrerequisiteService` exposes separately confirmed Microsoft runtime preparation. Hold `MaintenanceLock` and block owner/dialog close until native installation completes. Reuse settings only where the vendor route permits, with verified external exports required by the plan. See [onboarding decisions](docs/product-onboarding.md).

Fresh VACS uses machine scope and requires machine WebView2; TrackAudio uses current-user scope and machine VC++ x64. Existing VACS/TrackAudio settings require export before installation. vATIS fresh setup is pinned to the reviewed beta.19 Setup and its embedded package; future releases require adapter review. Its existing-install update route instead requires the recognized 0.0.1251 installed helper and an external backup.

### Pinned EuroScope lifecycle

EuroScope has a separate lifecycle from the upgrade-only VACS/vATIS/TrackAudio coordinator. `EuroScopePolicy` is the single pinned 3.2.3.2 binary/package identity, URL and digest source shared by checks, MSI removal and `EuroScopeInstallService`. `EuroScopeInstallWindow` reviews install, repair or replacement explicitly; replacing a newer build is a downgrade and never an ordinary automatic update. Download verification and complete recovery export precede changes. Preserve installation scope/path, verify the resulting PE version as well as the three-part MSI registration, and stop for reboot/manual recovery rather than guessing after partial replacement. Unknown copies must first be resolved/adopted. See [the policy and verification notes](docs/euroscope-management.md).

The pinned MSI is unsigned and has ProductVersion `3.2.3`, while the executable is `3.2.3.2`; exact official size/SHA-256 are the package check, not Authenticode. `EuroScopeMsiFootprint` proves cached MSI identity, scope and actual component paths before maintenance. It must cover the current user's reviewed Roaming\EuroScope as well as program files and vendor-owned fonts. Repair/replacement can overwrite bundled sample/configuration files, so export the complete reviewed install/data folders first; restoring personal files remains manual and must not overwrite the new binaries. Unknown or another user's component layout is unsupported.

`EuroScopePrerequisiteService` reads the Microsoft x86 v14 runtime registration. Its separately confirmed setup route downloads only through allowed Microsoft HTTPS hosts, verifies x86 product identity and Microsoft's Authenticode publisher, and invokes `/install /quiet /norestart`. The minimum is a reviewed Launchpad baseline, not a claim about the minimum runtime required by every GNG plugin. Runtime setup is explicit and shared runtimes remain installed during cleanup.

### Discovery and setup choices

`ConfiguredPathValidationService` checks selected programs and data folders without loading binaries, launching processes, reading credentials or opening profile contents. It distinguishes optional blanks, recognised identity/layout, unrecognised custom locations and invalid selections. Bounded PE/header/version metadata inspection distinguishes client files from common installers; GNG needs matching sector files, Swedish profiles and plugin evidence rather than an `ESAA` directory alone. This is selection guidance, not publisher trust, installation-management eligibility or readiness to control.

Settings debounces field checks off the UI thread, discards stale/closed-window results, and rechecks before saving. Changed invalid values block Save; changed warning values need an explicit checkbox. `NeedsAttention` preserves unchanged legacy choices so missing/custom paths cannot prevent unrelated preference changes. Discovery rechecks candidates before selection and again before returning them, disabling invalid results independently of their management support. The wizard displays the same program checks. Keep these checks separate from installer/removal verification, which still runs before each operation.

`InstallationDiscoveryService` reads recognized registrations and standard locations on request. Settings opens a review of candidates; selected paths are only persisted when Settings is saved. Existing values are kept unless replacement is selected. Setup and Settings expose this reviewed adoption flow. Discovery never moves files, changes ownership/ACLs, requests elevation, launches clients or reads credentials. It cannot convert unknown/portable copies into managed vendor installations. Keep Launchpad itself unelevated; only a recognized vendor operation may request its required scope.

`SetupWizardWindow` offers existing-app, fresh-setup and manual routes. MainWindow opens it automatically only when no settings file existed at startup and setup has not been completed/dismissed; the Setup button also opens it on demand. Opening or advancing the wizard does not discover, download, install or launch anything. Explicit actions reuse Settings/discovery, the reviewed EuroScope/fresh-client dialogs and Controller profile. GNG/plugin installation remains manual in 2.0, and choosing an existing path does not import configuration or credentials.

`ApplicationDescriptions` shares application descriptions between wizard cards, GNG/VatEFS configuration help and main-name tooltips/accessibility help. It does not add height to the compact rows. The final wizard page offers relevant EuroScope/GNG, TrackAudio and vATIS wiki links for nonblank chosen paths; they are optional guidance, not readiness checks. `SetupWizardState.GuideUrl` maps enum values to fixed HTTPS wiki destinations. Only the explicit guide button opens the default browser. No completion flags or automatic imports are added.

`SetupWizardState` separates tentative preferences from completed actions. Finish returns the draft; Skip, Escape and closing return the last explicit child-dialog saves and verified installation paths. Each such action is immediately checkpointed through `SettingsService.Save`; failure remains visible and the session retains the choices for another save attempt. Controller profile may already have written credentials/external files, and completed installations are never described as undone by leaving setup. The parent owns the maintenance gate and stops its process timer across the wizard. A runtime/installer restart requirement blocks further install/profile actions in that session, whether setup was opened from the wizard or directly. This flag does not prove a Windows restart across Launchpad process restarts; native restart/recovery behavior still requires VM validation.

Native exit `3010` must be latched as soon as the process returns, independently of later identity/registration verification or a successful result. Instance install/update/removal services retain that state; prerequisite operations notify the calling dialog synchronously before post-install verification. Dialogs forward it even on failure. Maintenance stops before later vendors, direct data/credential deletion or Launchpad uninstall handoff when a vendor requests a restart. The main footer has a separate wrapped, read-only restart notice that follows this latch; ordinary checks and preference-save messages cannot replace it. Its row collapses when no restart is required. Retaining this state does not change an adapter's accepted success codes or promise that an incomplete installation is usable.

`AppSettings.Copy()` preserves every current value/string setting across editors. `CompactLayout` defaults to true; the main-window Compact/Expanded switch retains text size and actions. The internal `Comfortable` tag remains the expanded-mode trigger. The wizard previews theme changes but saves its theme/density/startup choices only on Finish, unless the startup preference is explicitly saved in Settings. Dismissal restores the saved theme. Update checks use the authoritative in-memory settings, so a failed disk save cannot silently discard setup choices; an explicit maintenance reset still reloads settings after removing them. `Tests/SetupWizard.Tests` exercises copying, checkpoints, dismissal, verified paths, restart state and summaries entirely in memory.

### Reviewed removal and recovery

`MaintenanceWindow` separates application removal, settings/data deletion and Launchpad's own data categories. All destructive options start unchecked (Windows uninstall preselects Launchpad only). Review takes a cross-process `MaintenanceLock` until apply/cancel. Recovery exports default on and must go outside selected roots; vATIS and GNG removal require export if the user chooses to keep their settings, because those files live in the removed root. Keeping these settings means manual restoration from the export. The UI blocks closing once apply starts and reports the completed export path even after a later vendor failure.

`AtcRemovalCatalog` recognizes exact vendor layouts and scopes; `AtcRemovalVendor` revalidates identity before invoking known NSIS, MSI or vATIS uninstallers. It never executes arbitrary registry command strings. EuroScope recognition uses the official MSI identity/component path. VACS per-user operations needing elevation use a manual fallback; do not elevate them under another user's identity. VatEFS removal covers its exact configured DLL and dedicated browser profile only; other backend files and EuroScope references remain manual. Browser resets require Edge to be closed. Shared runtimes/fonts are not directly removed, but vendor-owned components (including EuroScope's font) may be removed by the vendor.

`AtcMaintenanceService` validates target overlaps, protects unselected configured paths even for missing/unsupported copies, exports before deletion, verifies each vendor's original snapshot immediately before its turn, and rechecks completed export bytes before subsequent destructive steps. It shares `PreservationPath` with Launchpad's uninstall guard to resolve existing ancestors and folder aliases during preview and apply. Launchpad removal also refuses configured ATC paths or recovery destinations within its managed installation. `RemovalFileService` uses bounded exact inventories, hashes and Windows file identity/handles; it rejects reparse paths, unsafe roots, network paths and named alternate streams (including Zone.Identifier). Direct deletion touches only reviewed unchanged entries. Exports are private file copies, not a full system/registry backup; see `RESTORE.txt`. Exact Launchpad Credential Manager targets may be deleted only by explicit selection and are never exported. Profile files and exports may already contain credentials by design.

The dependency-free `Tests/AtcRemoval.Tests` harness injects process, registry and identity probes; `Tests/UninstallIntegration.Tests` checks startup routing/registration and handoff with synthetic delegates. Neither may use the real settings, credential store or installed applications as fixtures.

---

## Non-obvious decisions

### Windows-1252 encoding for EuroScope .prf files
ProfileService reads/writes EuroScope `.prf` files and `LoginProfiles.txt` in Windows-1252, not UTF-8. Swedish names (ö, å, ä) corrupt if read/written as UTF-8.

In self-contained .NET builds, legacy encodings are not loaded automatically. **You must call `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` before `Encoding.GetEncoding(1252)`.** This is done in `ProfileService.GetPrfEncoding()`. Never call `Encoding.GetEncoding(1252)` in a static field initialiser — the provider won't be registered yet.

### Alternating rows and theme switching

Alternating rows use `RowBackgroundConverter`, which reads `Application.Current.Resources["RowBg"]` / `["RowAltBg"]` at conversion time. `ThemeToggle_Click` resets `AppList.ItemsSource` to force reevaluation. Preserve that behavior when changing row styling.

Use `DynamicResource` for theme-dependent brushes. Keep matching resource keys and explicit menu/input styles in both dictionaries, and check live switching as well as initial rendering. The theme button shows the destination mode: `☽` in light mode and `☀` in dark mode.

### Desktop launch routes

Ordinary desktop clients, including TrackAudio, are launched through `explorer.exe` with `UseShellExecute=false`. Changes to this route need installed-client testing.

EuroScope is the exception with or without a profile. `EuroScopeLaunchService.CreateStartInfo` supplies the configured data folder as `WorkingDirectory` and uses `ArgumentList` for an optional absolute `.prf` path with `UseShellExecute=false`. With no configured data folder, it prefers the selected profile's parent, then `%APPDATA%\EuroScope`. A missing/relative configured folder or missing selected profile stops the launch; never silently substitute another folder for an explicit setting or change the process-wide working directory.

### Release selection

`SoftwareReleaseSource` requires both published, non-prerelease GitHub metadata and a stable semantic tag for VACS/TrackAudio. It validates exact asset names, HTTPS origins, size and SHA-256. Do not replace this with the generic latest-release endpoint. `SoftwareVersion` compares full prerelease identifiers; an installed vATIS beta may receive beta/stable releases, while a stable copy stays on stable. Fresh beta setup requires its separate explicit opt-in.

### Read-only progress and responsive layouts

The `SoftwareProgressValue` and `SelfUpdateProgressValue` properties on `CheckResult` are read-only; their progress-bar bindings explicitly use `Mode=OneWay`. Retain this when editing templates.

The main table has equal-height rows: 38px in Compact, 56px in Expanded. Launch/profile selection, versions, status and Details occupy fixed columns. `CheckResult` derives presentation labels without changing operation eligibility; `Tests/CheckResult.Tests` exercises status and diagnostic handling with synthetic state. `MainWindow.ApplicationDetails.cs` selects a row and binds the shared panel to that exact model. Compact keeps the panel visible; Expanded adds row explanations and opens details on demand. Explicitly opened details survive density changes. The same existing handlers and eligibility bindings serve the panel actions, while per-row progress stays visible even when another application's details are selected. Full diagnostic/recovery text is read-only, scrollable and selectable; it never enlarges individual rows. The inert WPF fixture links the production selection partial as well as XAML and presentation models. Controller-profile fields scroll independently of their footer. `ReviewScrollHelper` positions generated reviews before confirmation; it does not confirm them. Check these behaviors in both themes and at minimum window sizes.

## Credentials

Launchpad's saved secrets are stored in Windows Credential Manager, not in `settings.json`. `ProfileService.Apply` also writes the password into external EuroScope `.prf` files and the Hoppie code into plugin text files. Do not claim secrets are never stored as plain text; those files and backups require the same privacy as credentials. Native quiet uninstall leaves those external copies and Credential Manager entries alone. Reviewed maintenance can delete explicitly selected data/credential targets; it cannot erase unknown copies elsewhere.

| Credential target | Content |
|---|---|
| `VatscaLaunchpad/VATSIM` | VATSIM password |
| `VatscaLaunchpad/Hoppie` | Hoppie ACARS code |

Service: `CredentialManagerService` (P/Invoke on `advapi32.dll`).

`Save` checks `CredWriteW` and throws a fixed-message `Win32Exception` on failure without including the target value or secret. Controller profile catches either credential-save failure before applying any EuroScope files or returning success, keeps the entries open for retry, and warns that the first of the two independent writes may already have succeeded. These writes are not transactional; do not claim rollback or log the submitted values.

Controller profile distinguishes storage from file application. When its target folder is absent at opening, it offers Save profile and keeps that scope even if the folder later appears. With an existing target, Save & apply updates all `ES*.prf` files in that folder, not only the selected launch profile. A disappearing target is rejected before credential access/file mutation by `ProfileService.Apply`.

---

## Settings file

Stored at `%APPDATA%\VatscaUpdateChecker\settings.json`. All paths and non-secret preferences live here. Do not store passwords here.

Ordinary main-window Settings, Controller profile, theme and selected-profile saves use `SettingsService.TrySave`. Filesystem/access/security failures retain the authoritative session settings, allow UI refresh and show a retry warning; they do not undo already completed credential/profile/application actions. The helper does not load fallback disk values or log settings. `Tests/Settings.Tests` injects an in-memory writer for success/failure/retry cases, without touching the user's settings. Writes themselves are not transactional.

---

## Theme system

- `App.xaml` merges `Themes/Light.xaml` as the first `MergedDictionary` at startup.
- `App.SetTheme(isDark)` replaces `MergedDictionaries[0]` at runtime.
- Shared theme brushes use `DynamicResource` so they follow dictionary replacement. Some controls also use fixed accent/status colors; check their contrast in both themes.
- `IsDarkMode` is persisted in `AppSettings` and restored on next launch.

When adding a new themed colour, add it to **both** `Light.xaml` and `Dark.xaml`.

---

## Launchpad installation and updates

`Program.Main` is the explicit startup object. `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` must be its first operation, before constructing WPF or reading settings. Installer/packaging hooks exit here without opening windows or starting checks. Staged updates are never automatically applied at startup.

`LaunchpadUpdateService` uses this repository's stable `win-x64` GitHub release feed. It manages installed copies with package ID `SwedenFirLaunchpad`; standalone/development and portable copies use the release-page fallback. Check, download and restart are separate user actions. Only `RestartToApply` invokes the updater, and MainWindow blocks that action during checks, downloads or open/modal dialogs.

Downloads use full packages, checking the expected package ID, version, size and SHA-256. A receipt in Velopack's package cache lets later checks reverify a staged download while offline. The service never infers readiness from a cached filename alone. SDK checks have no cancellation parameter, so the operation gate stays held until a check completes and then honors cancellation. Downloads support cancellation directly. The synthetic regression harness exercises the real SDK with temporary feeds and a locator that forbids process starts/exits; it never applies updates.

The installation root is `%LOCALAPPDATA%\SwedenFirLaunchpad`, separate from the existing `VatscaUpdateChecker` AppData folders. Never reuse the data-folder name as the package ID: Velopack uninstall removes its installation root. Existing settings and Credential Manager targets remain unchanged.

The [uninstallation guide](docs/uninstallation.md) records the reviewed removal choices. `LaunchpadUninstallService` redirects only the interactive Windows uninstall command to the maintenance dialog; the native quiet command remains a preserve-data fallback. Velopack's uninstall hook cannot host a cancellable UI: native uninstall may terminate Launchpad first. A copied registration finalizer waits for Setup's process to finish because fresh Setup writes its registration after the install hook; update/startup also repair recognized registration. These paths validate package identity and the existing command rather than overwriting unrelated entries.

After reviewed cleanup, a verified copy outside the installation root holds the handoff gate, waits for Launchpad to exit and invokes native uninstall. Temporary helper copies under `%TEMP%\SwedenFirLaunchpad-Maintenance` are conservatively cleaned on later startup; one may remain after full uninstall for Windows/user temporary-file cleanup. Fresh/silent/repair/update registration and this actual process handoff require VM testing. Native quiet/direct uninstall bypasses the custom gate and can still interrupt another operation; do not assume hooks can prevent interruption or guarantee recovery.

`scripts/Build-Installer.ps1` restores the pinned local `vpk`, publishes self-contained compressed win-x64 output, signs/packages it, and verifies signatures, tamper rejection, metadata and feed hashes. `--framework webview2` prepares the runtime prerequisite for the separately developed GNG browser. The build version is passed to both .NET and Velopack; output must be a fresh directory below `artifacts`.

Setup uses `Assets/installer-splash.png` through Velopack's `--splashImage` option, with a cyan live progress bar. This selects its button-free splash window for installation/repair; prerequisite, error, update-apply and uninstall dialogs retain Velopack's native behavior. The static 600×300 image reserves its bottom 12 pixels for progress. Its raster text does not provide native screen-reader text or adapt to Windows high-contrast colors. `scripts/Test-InstallerSplash.ps1` checks image bytes, opacity, dimensions, progress metadata and the full package embedded in Setup without running it. `scripts/Render-InstallerSplash.ps1` regenerates the image from the existing logo.

`Assets/app.ico` is the shared Windows application icon. The project embeds it as both the executable's `ApplicationIcon` and a WPF resource; MainWindow sets `Window.Icon`, while other windows use WPF's assembly-icon fallback. These cover window/taskbar/Alt-Tab icons without adding a visible title bar. Velopack's `--icon` brands Setup, the updater and execution launcher. Its Start Menu shortcut and Windows uninstall entry use the main executable's icon; the package requests `StartMenuRoot` only, so no desktop shortcut or taskbar pin is created. There is no notification-area icon. The package verifier checks icon resources against the source ICO as well as signatures.

Development keys are non-exportable and stored in `CurrentUser\My`; only the public certificate and thumbprint go under `%LOCALAPPDATA%\VatscaUpdateChecker\Signing`. Do not import the certificate into Root/TrustedPublisher to suppress warnings. Development signatures have no timestamp or public publisher trust. CI creates an ephemeral certificate to build and verify packages on PRs/main builds; version tags create draft releases for review. Production signing identity/provider remains undecided.

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
| `api.github.com` | Official VACS/TrackAudio release catalogs and Launchpad stable releases |
| `files.aero-nav.com/ESAA` | GNG Pack latest version (HTML scrape) |
| `vatis.app/updates/windows/` | vATIS release feed, packages and reviewed fresh Setup |
| `euroscope.hu` | Exact pinned EuroScope MSI; no latest-version lookup |
| Microsoft download hosts | Explicit verified runtime preparation |
| Windows Credential Manager | Launchpad's saved passwords and Hoppie code |
| Microsoft Edge | Dedicated VatEFS/VATIRIS web-app windows |

GitHub requests are unauthenticated and already include a User-Agent. Handle rate-limit/network failures as unavailable checks; do not add credential storage or authentication as an incidental workaround.

---

## Fonts, plugins and web-app rows

`FontService` checks `EuroScope.ttf`, `SMR ESGG.ttf` and `TopSky.ttf` against the copies in the configured GNG `ESAA` folder. It matches installed fonts by exact filename, ignoring duplicate names such as `FOO (1).TTF`. It compares `GlyphTypeface.Version` with a tolerance of 0.0001 and accepts an installed version newer than the bundled source. Byte equality is not the version test. The font action opens the source through the Windows shell for the user to install; it does not silently install fonts.

`ProfileService` identifies VatEFS by DLL basename. Enabling it adds both the plugin slot and display rows; disabling it removes those rows and renumbers later slots. A blank `VatEfsPath` leaves VatEFS references untouched. Screens come from existing profile display rows, with Ground Radar/Standard ES fallbacks. Existing display reconciliation and multi-profile sync have limitations; test them with synthetic profiles before changing them.

VATIRIS and VatEFS use dedicated Edge profile folders under `%APPDATA%\VatscaUpdateChecker`, with a PID/timestamp file per app. VatEFS availability checks the local TCP listener table for port 17770 rather than making repeated HTTP connections. A listening port does not prove the responding application's identity.

The Edge fallback finds a root browser process newer than the saved launch time; it does not verify the app URL or profile directory. The Kill action targets the tracked process tree. Do not assume this heuristic isolates unrelated Edge windows or protects against PID reuse. Keep this limitation visible when changing launch/kill behavior.

## Logging

`Logger` appends timestamped entries to `%APPDATA%\VatscaUpdateChecker\launchpad.log` and suppresses logging failures. Above 200 KiB it retains the latter half of the current lines; it does not enforce a fixed 500-line limit. Callers must omit credentials. File paths and error messages can still contain private details, so keep raw logs out of shared documentation.

## Validation boundaries

The workflow lists the current console harnesses and hidden WPF layout check. Their defaults use synthetic files/state; inspect a harness's README before using optional native signature or package-inspection modes. `Tests/UiReview` links production XAML and presentation models to inert handlers. It checks layout/bindings, not the real dialogs' native operations.

Run the explicit Release build and relevant harnesses for a change. Synthetic checks do not establish installed update/restart/uninstall behavior, UAC/scope, recovery or desktop accessibility/DPI. Native testing needs separate authorization and an isolated environment; keep its plans and results local.
