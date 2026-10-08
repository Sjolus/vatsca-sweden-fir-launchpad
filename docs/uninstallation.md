# Removing applications and resetting settings

Open **Remove / reset…** to remove applications, delete their settings, or uninstall Launchpad. Removing an application and deleting its data are separate choices.

## Review the selection

1. Select **Remove application / package** for each application you want to uninstall.
2. Separately select **Delete settings and local data** where you want a reset.
3. Keep **Export a recovery copy before removal** enabled and choose a private folder outside the affected locations.
4. Review the paths, then confirm **Remove selected items**.

When opened from Windows' Launchpad uninstall command, only Launchpad removal is selected initially. Selecting all applications does not select their settings.

You can disable the recovery export for permanent deletion. Keeping GNG or vATIS data while removing the package/application always needs an export because the personal files are inside the folder being removed.

Close affected applications first. Browser-session reset also requires Microsoft Edge to be closed. When an AeroNav session exists, close applications using WebView2 before resetting it. If a vendor uninstaller requests a restart, Launchpad stops before the remaining removals. Restart Windows, then review what remains. Launchpad itself is removed last.

## What gets removed

| Application | Program/package removal | Settings and other limits |
| --- | --- | --- |
| EuroScope | A supported Windows Installer installation. Portable or unfamiliar copies need manual removal. | The installer can remove its sample files in `%APPDATA%\EuroScope`, so Launchpad requires a recovery export. Custom external profiles remain. Remove Swedish GNG separately. |
| Swedish GNG | The configured folder's `ESAA` directory, root files beginning `ESAA `, and recognized generated `ESAA-Sweden_…` SCT/ESE/RWY files. | Package defaults and personal files are mixed together. There is no settings-only reset; keeping them means exporting for manual restoration. Unrelated files and the parent folder remain. |
| VACS | Supported VACS 2.x installations. | Optional deletion of the current user's Roaming/Local `app.vacs.vacs-client` folders. Custom locations remain. |
| TrackAudio | Supported current-user installations. | `%APPDATA%\trackaudio` stays unless settings deletion is selected. |
| vATIS | The supported standard installation. Its uninstaller removes the whole folder, including data. | Keeping settings means export and manual restoration. A settings-only reset removes known profiles, logs and configuration files while retaining binaries and unknown files. |
| VatEFS | The configured `VatEFS.dll` only. | Optional deletion of Launchpad's VatEFS browser profile. Disable or repair the EuroScope plugin references yourself; other VatEFS components remain. |
| VATIRIS | No program files. | Optional deletion of Launchpad's VATIRIS browser profile and local sign-in sessions. Online presets remain. Remove separately installed browser apps through the browser. |

Windows may ask for administrator permission. A per-user VACS uninstaller that requires elevation falls back to manual removal. Settings cleanup applies to the current Windows account.

Shared runtimes and Edge remain installed; online accounts are unchanged. Vendor uninstallers may remove their own components, such as EuroScope's font. Unknown copies, other FIR packages and custom files outside the listed locations need manual handling.

## Launchpad's data

Removing Launchpad deletes its managed installation, bundled .NET, self-update packages, shortcut and installed-app registration. Other cleanup options are separate:

| Option | Location or contents |
| --- | --- |
| Preferences and logs | Settings, log and browser tracking files in `%APPDATA%\VatscaUpdateChecker`, plus VatEFS installer diagnostics in `%LOCALAPPDATA%\VatscaUpdateChecker\SoftwareUpdates\VatEfsLogs`. |
| Browser sessions | Its `VATIRISProfile` and `VatEFSProfile` folders, plus `%LOCALAPPDATA%\VatscaUpdateChecker\Gng\Browser`. |
| Saved credentials | Launchpad's VATSIM password and Hoppie code in Windows Credential Manager. Credentials are not exported. |
| Cached downloads | Known software installer, update and prerequisite downloads in `%LOCALAPPDATA%\VatscaUpdateChecker`, including `Gng\Downloads`. GNG installation history and its retained ZIPs stay. The review lists the exact folders. |
| Existing recovery backups | `SoftwareUpdates\Backups`, `SoftwareUpdates\VatEfsRecovery`, `Gng\Installations` (history, original ZIPs and installation backups) and `CleanupBackups` under the same Local AppData folder. |

Unselected data remains available for a reinstall. Downloads saved through your normal browser, standalone copies, signing certificates and external recovery exports remain. Deleting GNG recovery history removes Launchpad's ability to restore those installations or automatically supply their original ZIPs for cleanup. Velopack logs and temporary maintenance helpers may also remain for normal temporary-file cleanup.

Deleting saved credentials does not remove copies already written to ATC profiles or recovery backups.

## If removal fails

Keep the recovery copy and read the reported result before trying again. Files changed since review, linked folders and conflicting paths can stop removal. Launchpad also protects unselected configured paths and refuses to uninstall itself when ATC data or recovery exports are inside its installation folder.

An export contains file copies, a manifest and `RESTORE.txt`. It is not an automatic reinstall or registry backup. Restore personal settings according to those instructions; check GNG customisations before copying them into a fresh package. Exports may contain credentials, so keep them private.

Windows' normal uninstall command opens this review. A native quiet uninstall removes Launchpad while retaining its separate data. Finish ongoing updates before using Windows uninstall: the native fallback can close Launchpad while another installer is still running.
