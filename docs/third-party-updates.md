# Updating ATC applications

Close the application you want to update. In Launchpad, choose **Check for Updates**, then use the update action beside that application's status. Hover over the status for the full message, or focus it with the keyboard and press **Ctrl+C** to copy it.

Launchpad downloads and verifies the official package, applies the reviewed change and checks the installed version. Most applications use their vendor installer; SAAMAN is a plugin DLL. Progress appears in the app. Checking alone does not download or install anything.

## Supported installations

| Application | Support and release source |
| --- | --- |
| VACS | Recognized 2.x and 3.x installations registered for the current user or all users. Updates come from the official [client releases](https://github.com/vacs-project/vacs/releases). A per-user installation that requires elevation must be updated manually. |
| TrackAudio | Registered current-user installations. Updates come from official [stable releases](https://github.com/pierr3/TrackAudio/releases). Machine-wide copies are not supported. |
| vATIS | Its standard per-user installation with the supported updater. Updates use the official Windows feed. Stable installations stay on stable releases; existing beta installations may update to beta or stable. |

For VACS and TrackAudio, the executable and Windows registration must agree on the version and location. If they differ, Launchpad explains the mismatch and asks you to repair or reinstall through the vendor. vATIS is checked against its package metadata and installed updater. If the vATIS application can be verified but its updater is unsupported, Launchpad still shows the installed and available versions and offers the official download page. Portable, custom or unsupported installations use the download-page fallback.

VACS 3 renamed its executable from `vacs-client.exe` to `vacs.exe`. Launchpad saves the new launch path after a supported upgrade. VACS 3.0.1 also restored the Windows installation-location key used before 3.0.0; Launchpad recognizes both formats. If 3.0.1 is already installed but an older Launchpad shows **Needs review**, update Launchpad and check again.

For updates to 3.0.1 or later, Launchpad checks both the older and 3.0.0 installation locations. Conflicting locations stay on the manual route because the vendor installer can move or remove another copy. Updates specifically to 3.0.0 retain the standard all-users Program Files requirement for 2.x migrations.

For a missing application, see [setup](product-onboarding.md). EuroScope has separate [supported-version management](euroscope-management.md). Swedish GNG has a separate [download and installation workflow](gng-updates.md).

## VatEFS

Launchpad installs and updates VatEFS from its official [GitHub releases](https://github.com/minsulander/vatefs/releases), including published prereleases. For a missing copy, use **Setup → Applications → Install…** beside VatEFS. Existing supported installations offer an update action in the Apps row. Checking versions alone never downloads or installs anything. The browser launch remains separate and is available while the local flight-strip service is running.

Managed installation uses the standard all-users `%ProgramFiles%\VatEFS` folder. Windows asks for administrator permission; installation runs in the background with status in Launchpad. Fresh setup also checks the Visual C++ x86 runtime needed by the EuroScope plugin and offers its separate installation action. Enable the plugin with **Use in EuroScope** beside VatEFS under **Setup → Applications**.

The VatEFS package and EuroScope plugin have different version numbers. Launchpad compares the package version registered by Windows Installer only when that installation matches the configured plugin folder. Copied plugins and unregistered builds show **Not reported** for the installed version, with the latest release and its link still available. A GitHub check failure does not disable browser launch.

Close every EuroScope instance and the VatEFS backend before installing or updating: the package replaces the plugin as well as the flight-strip application. Launchpad blocks its managed operations while either is running and never closes them for you. Custom or unrecognized installations keep the manual release link.

Before an update, Launchpad exports the complete installed VatEFS folder to `%LOCALAPPDATA%\VatscaUpdateChecker\SoftwareUpdates\VatEfsRecovery`. This includes configuration stored beside its program files. Keep that recovery copy if you have local changes; restoration is manual. External EuroScope profiles, browser settings and `VatEFSsettings.json` outside the installation folder are not changed by this installation route.

While Windows Installer runs, Launchpad shows elapsed time and the diagnostic log location. Failed installations retain that log under `%LOCALAPPDATA%\VatscaUpdateChecker\SoftwareUpdates\VatEfsLogs`. An unexpectedly long wait does not cause Launchpad to stop or restart the installer.

## SAAMAN

Open **Setup → Applications**. SAAMAN has one **Use in APP / ACC** switch. Turning it on uses the selected DLL or installs the latest stable [SAAMAN release](https://github.com/Kaskobiii/SAAMAN/releases) if missing, then enables it in the profile. The default destination is `ESAA\Plugins\SAAMAN\SAAMAN.dll` inside your EuroScope data folder. SAAMAN runs inside EuroScope and has no separate Launch button.

Choose **Update** to install the latest stable release. Updating from Apps keeps progress, cancellation and results in the SAAMAN row without switching to Setup. An identical installed copy is kept without downloading it again. Launchpad verifies file sizes, SHA-256 hashes, the published checksum list, and the DLL's plugin identity and version. It retains the release's licence notices. WebView2 is required; a separate runtime installation button appears if needed.

The switch applies immediately, with a recovery copy. SAAMAN becomes the first plugin, MAESTRO's entry is removed, and remaining plugins retain their relative order and display settings. Controller credentials and the MAESTRO DLL are unchanged. The option starts off and targets `ESAA APP ACC.prf`, retaining any previously selected custom profile.

If an enabled DLL has moved, use **Location…** to select its new location. Launchpad repairs the profile reference while keeping SAAMAN enabled and its MAESTRO restoration record. This local change needs no GitHub connection. **Repair** replaces a missing enabled DLL at its saved location.

GNG update previews include the saved profile choice. Disabling a previously managed integration removes SAAMAN and restores the original MAESTRO entry, order and display permissions when Launchpad replaced it. Other profile edits remain in place: disabling does not restore a whole-profile backup or create a MAESTRO entry if none existed. These SAAMAN changes apply only to the selected profile; GNG still updates the other package profiles normally. Leaving the option untouched preserves an independently configured SAAMAN profile.

Restoring MAESTRO requires its original DLL and Launchpad's saved restoration record. If either is missing, Launchpad explains what needs to be recovered and keeps the enabled choice.

Close every EuroScope instance before installation, profile changes or recovery. Replaced DLLs and original profiles are backed up under `%LOCALAPPDATA%\VatscaUpdateChecker\SAAMAN`. Select the update in **Setup → Activity & recovery** to review and confirm a previous DLL restore. A fresh installation has no previous DLL to restore. SAAMAN's preferences and browser state in `%LOCALAPPDATA%\SAAMAN` are retained.

## During an update

You can cancel download and preparation. Once the installer starts, wait for it to finish. Launchpad blocks conflicting setup/profile changes and its own Close action during installation.

Keep the client closed until the update finishes. Launchpad does not request a normal client launch afterward. Windows or the vendor may still show prerequisite or administrator prompts.

If a restart is required, restart Windows before further changes. Reopening Launchpad alone does not complete that restart. After a failed update, read the error and check the installation again before retrying.

**Setup → Activity & recovery** retains recent operation outcomes and recovery locations. It records what happened at the time, rather than checking whether that application still has the same version. Open a listed recovery folder for manual instructions; choosing a history entry never rolls back an application automatically.

## vATIS backups

**Do not use vATIS Setup to update an existing installation.** Setup can replace the folder containing your profiles. Launchpad uses the installed updater instead and first exports the complete installation to:

`%LOCALAPPDATA%\VatscaUpdateChecker\SoftwareUpdates\Backups`

Keep the full backup, including its manifest and `RESTORE.txt`. Restoration is manual, and the backup may contain sign-in details. Launchpad retains these backups until you choose to remove them.

VACS and TrackAudio updates do not create this full-installation backup.

## Download checks

VACS and TrackAudio packages must match the official release asset's SHA-256, size and product/version metadata. vATIS packages also require matching manifests and trusted publisher signatures. VatEFS uses the official asset's hash and size plus MSI identity, version and installation-layout checks; this does not establish a trusted publisher signature. These checks run before installation; Launchpad checks the installed version again afterward.

Downloads are kept under `%LOCALAPPDATA%\VatscaUpdateChecker\SoftwareUpdates`. Use [Remove / reset](uninstallation.md) to review cached downloads and backup cleanup.

## Maintainer notes

The current vendor commands are VACS Setup with `/S /UPDATE /CurrentUser` or `/AllUsers`, TrackAudio Setup with `/S`, and vATIS's installed helper with `--silent apply --norestart --package`. VatEFS uses system `msiexec` with `/qn /norestart` and automatic application shutdown/restart disabled. The supported vATIS helpers are **0.0.1251** and **1.2.161**; other helper versions need review. This updater check is separate from the 4.1.x controller-settings compatibility rule.

Command behavior is documented in the [VACS/Tauri installer](https://github.com/tauri-apps/tauri/blob/8909f221d1515955fc843808032bdc5d62209c96/crates/tauri-bundler/src/bundle/windows/nsis/installer.nsi), [TrackAudio/NSIS installer](https://github.com/electron-userland/electron-builder/blob/electron-builder%4026.15.3/packages/app-builder-lib/templates/nsis/installSection.nsh) and [vATIS/Velopack apply command](https://github.com/velopack/velopack/blob/0.0.1251/src/bins/src/commands/apply.rs). These commands depend on Launchpad's installation checks; they are not a general repair recipe.
