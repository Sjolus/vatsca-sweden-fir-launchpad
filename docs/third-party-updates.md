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

For a missing application, see [setup](product-onboarding.md). EuroScope has separate [supported-version management](euroscope-management.md). GNG installation remains manual in 2.0.

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

VACS and TrackAudio packages must match the official release asset's SHA-256, size and product/version metadata. vATIS packages also require matching manifests and trusted publisher signatures. These checks run before installation; Launchpad checks the installed version again afterward.

Downloads are kept under `%LOCALAPPDATA%\VatscaUpdateChecker\SoftwareUpdates`. Use [Remove / reset](uninstallation.md) to review cached downloads and backup cleanup.

## Maintainer notes

The current vendor commands are VACS Setup with `/S /UPDATE /CurrentUser` or `/AllUsers`, TrackAudio Setup with `/S`, and vATIS's installed helper with `--silent apply --norestart --package`. The vATIS helper is pinned to **0.0.1251**; future helper versions need review.

Command behavior is documented in the [VACS/Tauri installer](https://github.com/tauri-apps/tauri/blob/8909f221d1515955fc843808032bdc5d62209c96/crates/tauri-bundler/src/bundle/windows/nsis/installer.nsi), [TrackAudio/NSIS installer](https://github.com/electron-userland/electron-builder/blob/electron-builder%4026.15.3/packages/app-builder-lib/templates/nsis/installSection.nsh) and [vATIS/Velopack apply command](https://github.com/velopack/velopack/blob/0.0.1251/src/bins/src/commands/apply.rs). These commands depend on Launchpad's installation checks; they are not a general repair recipe.
