# Changelog

## [2.0.1] — 2026-10-07

- Check `TopSky.ttf` alongside the EuroScope and SMR ESGG fonts, and offer the existing font installation action when it is missing or outdated.

## [2.0.0] — 2026-10-06

### Installation and updates

- Add a Windows installer, portable package and in-app Launchpad updates. Packages include .NET; Setup can install WebView2. Updates require an explicit download and restart.
- Install and update supported VACS, TrackAudio and vATIS copies, with progress and official-package checks. Fresh vATIS setup requires opting into the supported beta. Missing Microsoft runtimes have separate installation actions.
- Install, repair or switch EuroScope to Sweden's supported **3.2.3.2** token-authentication build.
- Build and verify self-signed packages in CI. Windows publisher warnings remain expected until trusted signing is available.

### Setup and everyday use

- Add an optional setup guide for existing applications, fresh installation and manual configuration. Explain each tool's purpose and link to the relevant VATSCA guides.
- Find installed programs and check chosen application files and data folders. Existing saved paths remain unless you choose to replace them.
- Keep explicitly saved choices and completed installations when leaving setup; save unfinished preferences only on Finish. GNG/plugin setup remains manual in 2.0.
- Retain the floating window design while adding resizing, Compact/Expanded views, equal-height application rows and a shared details panel. Keep bottom actions visible, let profile forms scroll and improve dark-theme contrast and keyboard focus.
- Use EuroScope's configured data folder as its working directory, with or without a selected profile. Report missing paths before starting it.

### Removal, preservation and error handling

- Choose application removal and settings reset separately, with a path review and recovery export. Windows uninstall opens the same screen with only Launchpad removal selected.
- Save controller details separately from applying them to EuroScope. Failed credential writes stop profile application and leave the form open for retry.
- Show installer restart requests and block further maintenance until the current session ends. Windows is never restarted automatically.
- Keep current choices after a settings-save failure and show a retry warning.
- Explain when an installed application's version differs from its Windows registration and require repair before managed updates.

## [1.4.0] — 2026-05-10

- Check installed EuroScope and SMR ESGG fonts against the copies in the GNG package, with a colour-coded status beside the GNG row.
- Open Windows font preview to install missing or outdated fonts.

## [1.3.0] — 2026-05-10

- Add VatEFS detection and a folder setting.
- Enable or disable the VatEFS plugin in EuroScope profiles, including screen permissions, with a preview before applying changes.
- Open the local VatEFS interface from Launchpad when its EuroScope plugin is running.
- Preserve controller details and the selected EuroScope profile when saving settings.

## [1.2.2] — 2026-04-03

Release of the LoginProfiles encoding fix from 1.2.1. Only the version number changed; there were no additional functional changes.

## [1.2.1] — 2026-04-03

Tagged version; included in the 1.2.2 release.

- Preserve Swedish characters when updating existing `LoginProfiles.txt` files by reading and writing Windows-1252.

## [1.2.0] — 2026-03-30

- Add a Launchpad version row with update checks and a link to download new releases.
- Ship VATIRIS support after fixing the build errors in 1.1.0.
- Increase the window height to accommodate the new application rows.

## [1.1.0] — 2026-03-30

Tagged version; VATIRIS support shipped in 1.2.0 after a build fix.

- Add a VATIRIS row with launch and stop controls for an Edge app window, using a separate browser profile and process tracking.
- Show N/A in the version columns for VATIRIS.

## [1.0.0] — 2026-03-29

Initial public release.

### Features
- Update checker for EuroScope, GNG Pack, TrackAudio, VACS, and vATIS
- One-click launch and kill for all supported tools
- EuroScope profile picker — preselect a `.prf` file to skip the profile dialog on launch
- Profile sync — write controller details to `ES*.prf` files in the configured folder and supported Hoppie/login-profile files
- Dry-run preview — review proposed profile-file changes before applying
- Credentials stored in Windows Credential Manager; profile sync writes them to external tools' text files as required by those tools.
- Light and dark theme, persisted across sessions
- Application log at `%APPDATA%\VatscaUpdateChecker\launchpad.log`
