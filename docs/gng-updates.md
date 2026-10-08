# Installing and updating Swedish GNG

Available in Launchpad 2.1.0 and later.

Open **Swedish GNG package → Details… → Set up / update…**. The setup guide also offers an unchecked **Open GNG setup after I choose Finish** option. Version checks never download or install a package automatically.

## Get the package

Check the destination at the top of the window. This is your EuroScope **data folder**, usually `%APPDATA%\EuroScope`, containing the `ESAA` folder and Swedish profiles. It is separate from the folder containing `EuroScope.exe`. If no data folder is configured, choose one; Launchpad saves that choice only after a successful installation.

Choose **Download Swedish GNG**. Launchpad first checks AeroNav's public AIRAC, package number and revision, then looks for matching saved ZIPs. If the required packages are cached and valid, it goes straight to the installation review without opening the browser or asking you to sign in. If only one package is saved, only the missing one is downloaded. **Download fresh copies** bypasses the cache.

Complete the website's sign-in if a download is needed. AeroNav controls access through VATSIM and Navigraph accounts. Launchpad uses a separate browser session and does not read your normal browser's credentials or copy them into its settings.

Launchpad checks the destination and shows which packages it needs. For a recognized existing Swedish GNG installation, it uses **Update Only** to install and a matching **Full Package** to keep as a cleanup reference. For a new or incomplete installation, it uses **Full Package** to install. The extra complete reference is never installed over the update.

Once the [Swedish package page](https://files.aero-nav.com/ESAA) makes downloads available, Launchpad selects the current ZIPs, shows download progress and prepares a review. You do not need to choose a row or archive format on the website. Paired downloads must have the same AIRAC/package number and revision; their build timestamps may differ.

The panel above the browser shows which package is downloading, its percentage and size, and the Cancel action. The website is covered by a muted, locked panel during transfer and package checks, leaving progress and cancellation visible. Sign-in remains interactive whenever needed. After the first download, Launchpad checks the ZIP before starting the Full reference. Once both are downloaded, it checks your destination and prepares the installation review. Any error stays visible there with the next action.

Opening the window alone does not start a download. Cancelling stops that request; it does not restart on its own. If the website changes or Launchpad cannot identify the requested ZIP, it leaves your installation unchanged and offers a retry or normal-browser import. It does not substitute another country's package or a different package type. If only the cleanup-reference download fails, the downloaded Update Only ZIP is retained while you retry or import the missing reference.

If sign-in does not work inside Launchpad, choose **Open in browser ↗**, download there and use **Import ZIP…**. Keep the original `ESAA-Full-Package_…zip` or `ESAA-Update-Only_…zip` filename. Browser-added duplicate numbers are accepted; renamed or repacked archives are not.

For a manual download, choose **ESAA Update_Only → zip** for a routine update, or **ESAA Full_Package → zip** for installation or repair. Update Only needs an existing GNG installation with the unchanged plugins its ZIP omits. A standalone Update Only import can be installed without a cleanup reference; it cannot supply a complete-package cleanup comparison.

If Cloudflare verification keeps spinning during VATSIM sign-in, use that browser/import route rather than waiting indefinitely. Signing in through your normal browser does not sign the embedded browser in; download the ZIP there and bring the file back to Launchpad. Installation review, preservation, recovery and optional cleanup still happen in Launchpad.

The embedded browser needs Microsoft WebView2. If it is missing, the window offers a separately confirmed runtime installation, which may ask for administrator permission. Restart Windows if requested. ZIP import works without the embedded browser.

## Review and install

The preview shows additions, replacements and merges by default, with counts of files to change, files already matching and personal files kept. Select **Show unchanged files** to include the files that will not be modified. This only changes the list you see, not what will be installed. Read the warnings and check the destination before selecting the confirmation and choosing **Install reviewed package**.

Close every EuroScope instance before installing or restoring GNG. Launchpad disables those actions while it detects a EuroScope process, including one started outside Launchpad or with no visible window. Downloads and review remain available. Once EuroScope closes, confirm the review again. Launchpad rechecks before changing files; if EuroScope starts during an installation, further changes stop and recovery may need to be completed after closing it.

**Keep my list positions and visibility** is on by default. For matching EuroScope and TopSky lists, it keeps the X/Y position and whether the whole list is shown. Columns, individual list items, sorting and other settings follow the new package. New lists use package defaults; retired lists are not brought back. Clear the option to use the package's list layout instead. Changing it rebuilds the preview and clears your installation confirmation.

Launchpad follows the existing profiles' list settings paths within the reviewed data folder. If a custom path is missing, outside that folder, or profiles disagree about the same list's placement, preservation stops for review. The option remains available so you can explicitly choose the package defaults instead.

Choose **View…** beside a file to see its expected changes. Text files offer a compact diff and a **Before / After** view; profiles and list settings show the merged result that will be installed. Sign-in values and other recognized credentials are hidden. Personal files, binaries and text files exceeding 2 MiB or 20,000 lines show a summary with sizes and file hashes instead. Viewing a comparison does not change any files or approve installation. If a reviewed file, source setting or ZIP has changed, choose the package again to prepare a fresh review.

The installer treats files differently according to their purpose:

| Files | What happens |
| --- | --- |
| Sector files, radar screens and shared package defaults | The package refreshes them. Existing files that will change are backed up first; custom changes to shared defaults can be replaced. |
| Existing `LoginProfiles.txt`, and supported Local/Hoppie settings files | Kept byte for byte. Files not supplied by the ZIP are left in place. |
| Existing package profiles | Package references and plugin entries are refreshed while keeping LastSession fields, supported personal preferences and custom plugin/display entries. |
| EuroScope and TopSky list settings | With preservation on, retain matching lists' positions and whole-list visibility. Other values come from the package. With it off, use package defaults. |
| New package profiles | Added with package sign-in fields omitted. They do not inherit credentials or custom plugins from other profiles; review Controller profile before use. |
| DLLs in the package's Updated Plugin folder | Promoted to their active plugin location. Existing DLLs are backed up. |

When a package supplies RDF, updated profiles use that DLL and drop old AFV/RDF entries. External plugin files remain untouched. Local TopSky RDF preferences are preserved and flagged for review; Launchpad does not decide whether every custom plugin combination is compatible. Old profiles missing from the new package remain untouched and are identified in the warnings.

Launchpad rechecks the ZIP, previewed files and required plugins before applying changes. A changed file requires a fresh review. Older generations are refused. Full and Update Only ZIPs can have different build timestamps for the same AIRAC/revision; a package with an earlier timestamp is refused too. Use a suitable package or restore the last managed update first.

Installation shows progress and cannot be cancelled once it starts. It does not launch EuroScope, install Windows fonts or apply credentials. Afterward, use **Check fonts** and review **Controller profile**, then choose a profile supplied by the new package before controlling.

## Optional old-file cleanup

When a complete package reference is available, **Review old files for cleanup…** opens a separate preview. Launchpad fills in its verified old and new complete ZIPs when available, including references retained alongside Update Only installs. Your first managed installation may still need the original old ZIP. Without it, leave old files in place.

Matching AIRAC and revision alone do not prove that a Full Package reference describes every installed file. Cleanup still checks the actual installed replacement sectors and files. If the Full and Update Only packages differ in ways it cannot verify, it keeps the old files and explains why.

Nothing is selected automatically. Eligible selected files move into a dated backup; modified and referenced files stay. See [GNG cleanup](gng-cleanup.md) for the comparison rules and its separate restore action.

## Storage and recovery

Launchpad keeps GNG data under `%LOCALAPPDATA%\VatscaUpdateChecker\Gng`:

| Folder | Contents |
| --- | --- |
| `Browser` | Dedicated AeroNav sign-in session. |
| `Downloads` | ZIP downloads, including incomplete downloads. |
| `Installations` | Per-destination installation history, retained original ZIPs, recovery manifests and replaced files. |

Installation and cleanup backups must be on the same local volume as the EuroScope data folder. Network locations and linked folders are unsupported. There is no automatic backup expiry; keeping originals and backups uses additional disk space.

A failed installation attempts to restore the files it changed. Read the result before trying again. If recovery is incomplete, keep the backup and resolve the reported files before using EuroScope. Launchpad reports the pending recovery and blocks EuroScope launch and further GNG installation for that folder.

To undo a managed update, choose **Restore backup…** in GNG setup and select the dated installation folder shown in its result. Recovery must target the same data folder and starts with the most recent managed installation, or the pending failed operation. Restore can replace unchanged installed files with their originals and remove unchanged files added by the update. Files edited since installation are kept and reported for attention. Preserve those edits elsewhere, resolve the listed conflicts and retry recovery. Keep each backup's manifest, package and file structure together.

Cleanup has its own **Restore a backup…** action and separate `CleanupBackups` folder. If undoing both cleanup and an installation, restore archived dependencies before returning to profiles that need them.

In **Remove / reset…**, deleting **cached downloads** leaves installation history and its original ZIPs intact. Deleting **recovery backups** removes GNG installation history, retained ZIPs and cleanup backups as well as existing client recovery backups. Resetting **browser sessions** clears the dedicated sign-in session separately.

Profiles and their recovery copies can contain passwords and controller details. Keep backups private; they are not suitable for attaching to a public issue.
