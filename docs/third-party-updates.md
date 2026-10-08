# Updating ATC applications

Close the application you want to update. In Launchpad, choose **Check for Updates**, open that application's **Details…**, then choose the update action.

Launchpad downloads the official package, checks it, runs the vendor installer and verifies the installed version. Progress appears in the app. Checking alone does not download or install anything.

## Supported installations

| Application | Support and release source |
| --- | --- |
| VACS | VACS 2.x installations registered for the current user or all users. Updates come from the official [client releases](https://github.com/vacs-project/vacs/releases). A per-user installation that requires elevation must be updated manually. |
| TrackAudio | Registered current-user installations. Updates come from official [stable releases](https://github.com/pierr3/TrackAudio/releases). Machine-wide copies are not supported. |
| vATIS | Its standard per-user installation with the supported updater. Updates use the official Windows feed. Stable installations stay on stable releases; existing beta installations may update to beta or stable. |

For VACS and TrackAudio, the executable and Windows registration must agree on the version and location. If they differ, Launchpad explains the mismatch and asks you to repair or reinstall through the vendor. vATIS is checked against its package metadata and installed updater. Portable, custom or unsupported installations use the download-page fallback.

For a missing application, see [setup](product-onboarding.md). EuroScope has separate [supported-version management](euroscope-management.md). Swedish GNG has a separate [download and installation workflow](gng-updates.md).

## VatEFS

Launchpad installs and updates VatEFS from its official [GitHub releases](https://github.com/minsulander/vatefs/releases), including published prereleases. For a missing copy, choose **Details… → Install…** or use the setup guide. Existing supported installations offer an update action. Checking versions alone never downloads or installs anything. The browser launch remains separate and is available while the local flight-strip service is running.

Managed installation uses the standard all-users `%ProgramFiles%\VatEFS` folder. Windows asks for administrator permission; installation runs in the background with status in Launchpad. Fresh setup also checks the Visual C++ x86 runtime needed by the EuroScope plugin and offers its separate installation action. After setup, use **Controller profile** to review enabling VatEFS in your EuroScope profiles.

The VatEFS package and EuroScope plugin have different version numbers. Launchpad compares the package version registered by Windows Installer only when that installation matches the configured plugin folder. Copied plugins and unregistered builds show **Not reported** for the installed version, with the latest release and its link still available. A GitHub check failure does not disable browser launch.

Close every EuroScope instance and the VatEFS backend before installing or updating: the package replaces the plugin as well as the flight-strip application. Launchpad blocks its managed operations while either is running and never closes them for you. Custom or unrecognized installations keep the manual release link.

Before an update, Launchpad exports the complete installed VatEFS folder to `%LOCALAPPDATA%\VatscaUpdateChecker\SoftwareUpdates\VatEfsRecovery`. This includes configuration stored beside its program files. Keep that recovery copy if you have local changes; restoration is manual. External EuroScope profiles, browser settings and `VatEFSsettings.json` outside the installation folder are not changed by this installation route.

While Windows Installer runs, Launchpad shows elapsed time and the diagnostic log location. Failed installations retain that log under `%LOCALAPPDATA%\VatscaUpdateChecker\SoftwareUpdates\VatEfsLogs`. An unexpectedly long wait does not cause Launchpad to stop or restart the installer.

## During an update

You can cancel download and preparation. Once the installer starts, wait for it to finish. Launchpad blocks conflicting setup/profile changes and its own Close action during installation.

Keep the client closed until the update finishes. Launchpad does not request a normal client launch afterward. Windows or the vendor may still show prerequisite or administrator prompts.

If a restart is required, restart Windows before further changes. Reopening Launchpad alone does not complete that restart. After a failed update, read the error and check the installation again before retrying.

## vATIS backups

**Do not use vATIS Setup to update an existing installation.** Setup can replace the folder containing your profiles. Launchpad uses the installed updater instead and first exports the complete installation to:

`%LOCALAPPDATA%\VatscaUpdateChecker\SoftwareUpdates\Backups`

Keep the full backup, including its manifest and `RESTORE.txt`. Restoration is manual, and the backup may contain sign-in details. Launchpad retains these backups until you choose to remove them.

VACS and TrackAudio updates do not create this full-installation backup.

## Download checks

VACS and TrackAudio packages must match the official release asset's SHA-256, size and product/version metadata. vATIS packages also require matching manifests and trusted publisher signatures. VatEFS uses the official asset's hash and size plus MSI identity, version and installation-layout checks; this does not establish a trusted publisher signature. These checks run before installation; Launchpad checks the installed version again afterward.

Downloads are kept under `%LOCALAPPDATA%\VatscaUpdateChecker\SoftwareUpdates`. Use [Remove / reset](uninstallation.md) to review cached downloads and backup cleanup.

## Maintainer notes

The current vendor commands are VACS Setup with `/S /UPDATE /CurrentUser` or `/AllUsers`, TrackAudio Setup with `/S`, and vATIS's installed helper with `--silent apply --norestart --package`. VatEFS uses system `msiexec` with `/qn /norestart` and automatic application shutdown/restart disabled. The vATIS helper is pinned to **0.0.1251**; future helper versions need review.

Command behavior is documented in the [VACS/Tauri installer](https://github.com/tauri-apps/tauri/blob/8909f221d1515955fc843808032bdc5d62209c96/crates/tauri-bundler/src/bundle/windows/nsis/installer.nsi), [TrackAudio/NSIS installer](https://github.com/electron-userland/electron-builder/blob/electron-builder%4026.15.3/packages/app-builder-lib/templates/nsis/installSection.nsh) and [vATIS/Velopack apply command](https://github.com/velopack/velopack/blob/0.0.1251/src/bins/src/commands/apply.rs). These commands depend on Launchpad's installation checks; they are not a general repair recipe.
