# Changelog

## [2.1.0] — 2026-10-08

- Install and update VatEFS from its official GitHub releases, including prereleases. Add it to the setup guide, back up managed installations before updating, and require EuroScope and the VatEFS backend to be closed. Compare the registered package version while keeping the separate browser launch.
- Request a GNG download and sign in on AeroNav within Launchpad; it selects the newest Swedish Update Only ZIP for an existing installation, or Full Package for setup or repair. ZIP import remains available. Review and explicitly confirm installation, with browser sessions and downloaded packages kept in AppData.
- Reuse validated saved GNG packages when AeroNav's public AIRAC and revision still match, skipping login when both required ZIPs are available. Download only missing packages, with a separate option to request fresh copies.
- Back up replaced GNG files, preserve supported personal settings and controller details, refresh package profiles and promote staged plugin DLLs while EuroScope is closed. Provide recovery for failed or unwanted installations.
- Disable GNG installation and restoration while any EuroScope instance is running, with a visible explanation and a fresh confirmation required after it closes.
- Keep EuroScope and TopSky list positions and whole-list visibility during GNG updates, with an option to use package defaults instead. Refresh list columns, items and sorting from the package, and show the expected result in each file's comparison.
- Review per-file comparisons before installing GNG, with unchanged files hidden by default. Make download progress more prominent and disable browser interaction while a package downloads.
- Offer GNG setup from the application row and as an optional next step after the setup guide. Missing WebView2 has a separate runtime installation action; browser downloads and ZIP import remain available as a fallback.
- Compare old and new complete GNG packages after an update, then choose eligible obsolete sector, profile and plugin files to move into a dated backup. Keep modified, referenced and protected files in place; nothing is selected automatically.
- Reuse the original complete ZIPs retained by managed GNG installations for cleanup. An existing installation without package history still needs its old complete ZIP.
- Keep a matching complete ZIP alongside managed Update Only installations for cleanup reference, without applying its extra files. Verify that both packages describe the same AIRAC and revision.
- Restore a cleanup backup to its original configured EuroScope data folder without overwriting existing files.
- Fix Windows Installer property quoting for paths containing spaces in VatEFS and EuroScope operations. Keep unexpected installer dialogs visible, report elapsed installation time for VatEFS, and save a private diagnostic log if troubleshooting is needed.

## [2.0.2] — 2026-10-07

- Replace Setup's plain progress dialog with a VATSIM Scandinavia splash and live progress bar, removing its unnecessary OK button during installation.

## [2.0.1] — 2026-10-07

- Check `TopSky.ttf` alongside the EuroScope and SMR ESGG fonts, and offer the existing font installation action when it is missing or outdated.
- Keep release downloads focused on the installer, portable ZIP and required update files; retain packaging reports in CI artifacts.

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
