# Changelog

## [2.6.4] — 2026-10-29

- Recognize VACS 3.0.1 after its Windows registration change. Check both old installation locations before updating, keeping conflicting copies on the manual route.
- Simplify GNG updates with a short installation summary inside the main window. Highlight customised files and checks needing attention, with an information button explaining the optional advanced review.
- Give file comparisons more space. Add search, profile/plugin filters and Previous/Next file navigation; returning to the list keeps its search, sorting and scroll position.
- Keep GNG progress visible when switching Apps and Setup. Show outstanding font or controller-profile tasks after installation, with cleanup and recovery under advanced options.
- Accept original complete AeroNav Testing ZIPs through manual import and label verified testing installations. Normal downloads still use published packages and retain downgrade protection.
- Refresh installed GNG status after installation or restoration. Leave files and recovery history unchanged when the package already matches.
- Fix retrying list-position preservation after a cancelled comparison, and keep installation disabled when a file comparison becomes stale.

## [2.6.3] — 2026-10-29

- Fix controller Save & apply rejecting newer vATIS 4.1 releases. Accept numbered beta/RC builds and stable 4.1.x patches from beta.19 onward, with publisher and settings-format checks retained.
- Recognize renamed Swedish vATIS profiles and preserve additional fields in existing profiles. Keep validation strict for newly downloaded profiles.
- Explain when a vATIS version needs a Launchpad compatibility update instead of reporting a generic setup failure.

## [2.6.2] — 2026-10-29

- Support VACS 3 installations and its renamed `vacs.exe`. Update the saved launch path after upgrading from VACS 2, and check both process names before installation or removal.
- Keep custom VACS 2 installations on the manual upgrade route when the VACS 3 installer cannot preserve their location.
- Keep SAAMAN updates in the Apps view, with progress, cancellation and results in its row.
- Reopen the setup guide from Setup → Preferences. Remove its duplicate entry from the Help menu; first-run guidance still opens automatically.

## [2.6.1] — 2026-10-24

- Fix vATIS beta.20 updates being rejected because its package uses a newer updater. Keep signature checks and the complete installation backup.
- Explain when a future vATIS updater needs a Launchpad compatibility update instead of suggesting repeated retries.

## [2.6.0] — 2026-10-21

- Bring application locations, GNG, controller details and preferences together under Setup. Move Activity & recovery out of the main toolbar and place help beside the window controls.
- Guide first-time setup in that same view, with next steps and saved progress across restarts. Switching tabs no longer marks setup complete, and startup update checks wait until the guide is finished or dismissed.
- Enable or disable VatEFS and SAAMAN from Setup → Applications. SAAMAN installs its DLL when needed and restores MAESTRO when switched off, without a separate settings dialog. Controller details save independently of plugin choices.
- Hide unused applications from Apps. Group GNG, VatEFS and SAAMAN beneath EuroScope.
- Keep existing locations selected when finding installed applications. Review only the paths being changed.
- Change an enabled SAAMAN DLL's location and update its profile reference in one place, without an online release check. Keep its plugin order and MAESTRO restoration information.
- Use saved controller details for supported vATIS and TrackAudio installations. Add the Swedish vATIS profile when needed, preserving existing profiles and audio settings.
- Reuse recent release checks and respect service retry times. Explain rate limits separately from access failures and connection problems.
- Add setup and maintenance outcomes, screen visits and inventory freshness to the maintainer dashboard. Include the service and HTTP status in optional error reports and the local sent-report viewer.

## [2.5.2] — 2026-10-21

- Fix unreadable dark-mode text under **What is sent?** in the Privacy and support dialog. (#20)

## [2.5.1] — 2026-10-21

- Restore vATIS update checks and package downloads after its Windows feed moved to a new address.
- Keep showing verified vATIS versions when its updater is unsupported, with the manual-update option retained.

## [2.5.0] — 2026-10-20

- Install and update SAAMAN from its latest stable GitHub release, or adopt an existing plugin. Show its version alongside the other applications.
- Add an optional APP/ACC profile change that puts SAAMAN first and replaces MAESTRO's entry. Disabling it restores any MAESTRO entry Launchpad replaced, with its original order and display permissions, while keeping other profile changes. Preview and apply it separately from controller details, with a backup of the original profile.
- Preserve the SAAMAN profile choice during GNG updates. Apply its plugin changes only to the selected profile and keep other plugins' display settings.
- Verify downloaded SAAMAN files, keep recovery copies before replacing the DLL, and require EuroScope to be closed for installation, profile changes and recovery.
- Recognize SAAMAN in optional detailed DLL reporting and the maintainer dashboard.
- Move controller-profile explanations into information buttons available by mouse or keyboard, keeping the form easier to scan.

## [2.4.0] — 2026-10-14

- Restore visible Show and confirmed Close actions for running applications. Add reviewed Start all and Close all actions, with individual selection and normal application close prompts.
- Show the latest confirmed usage, software inventory, error and manually submitted report in Help, with a readable summary, confirmation time and exact JSON.
- Improve software and plugin version charts. Add an optional inventory of all DLLs in configured plugin folders, distinguish staged copies, and read supported plugins' EuroScope registration versions separately from their Windows file versions. Check the separately configured VatEFS plugin folder too.
- Include today's usage and software-version observations in the maintainer dashboard, with the current day marked as partial.
- Exempt signed-in dashboard requests from rate limits while retaining protection on public reporting and sign-in endpoints.
- Add named backoffice users with passkey invitations and separate read permissions for each page and API. Owners can disable access or reset a lost passkey.

## [2.3.1] — 2026-10-14

- Fix GNG update checks getting stuck on Checking after the latest version is found. Keep status notifications on the UI thread, including failures and portable Launchpad version checks.

## [2.3.0] — 2026-10-14

- Add independent choices for usage statistics, software/plugin versions and automatic error reports, with a first-use prompt and a support ID that survives updates. Change the ID or clear unsent reports from Help.
- Ask existing and new users to choose before sending anything. Saving all reporting choices off is remembered; Not now leaves the choice for a later launch.
- Preview and send structured problem reports from Help, application details or Activity, with an option to save locally and a receipt after submission.
- Add a private passkey-protected maintainer dashboard for usage trends, application launches, feature use and shared software/plugin versions. Reports, diagnostics and support lookup are separate, with bounded queues, request quotas, retention and storage limits.
- Keep reporting data separate from ordinary preferences reset, with an explicit support-ID and reporting reset option.

## [2.2.0] — 2026-10-12

- Connect application selection, GNG installation, controller-profile review and font checks in a resumable setup guide. Find and choose existing copies directly beside each application.
- Mark optional applications as Not used without clearing their paths or configuration, and show relevant update and attention counts.
- Bring running applications forward with Show. Move normal close and confirmed force-stop actions into Details, with executable, process ID and start-time checks.
- Add independent text sizes, an adjustable details panel, an always-on-top pin and remembered window size, position and monitor. Show the selected EuroScope profile before launch.
- Keep recent software outcomes and recovery locations in a local Activity view. Open GNG recovery with the chosen backup suggested, retaining its normal checks and confirmation.
- Lead installation and removal reviews with what changes, what stays and recovery guidance. Highlight GNG additions/removals and jump between change groups.
- Make a blank Hoppie code's preview match Apply: existing Hoppie files stay unchanged.

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
