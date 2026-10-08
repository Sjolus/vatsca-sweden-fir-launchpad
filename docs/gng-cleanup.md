# Clean up old GNG files

Available in Launchpad 2.1.0 and later.

Installing a new GNG package can leave older sector files, profiles and plugin DLLs behind. The installation result's **Review old files for cleanup…** action compares two complete packages and helps you move eligible old files into a backup. You can also open **Swedish GNG package → Details… → Clean up…** after a manual update. Cleanup does not install an update or remove the whole GNG package.

## Compare the packages

Install the newer GNG package first. Cleanup needs the original **complete** ZIP downloads for the version you replaced and the version you installed. After a managed update, Launchpad supplies its verified cached ZIPs when available, including complete references retained alongside Update Only installations. On your first managed installation, you may need to select the old ZIP yourself. The files still start unchecked.

For manually selected ZIPs, keep AeroNav's original `ESAA-Full-Package_…zip` filenames; browser-added duplicate numbers are accepted. **Update Only** ZIPs, renamed downloads and ZIPs made from your own installation cannot establish which files the package retired. A standalone Update Only import without a matching complete reference cannot supply that comparison. Cleanup must also verify the installed replacement sectors against the complete reference; it refuses comparison when package differences prevent that proof.

Use **Download packages on AeroNav ↗** to open the [Swedish package page](https://files.aero-nav.com/ESAA) in your normal browser and sign in there. Check your Downloads folder or saved package ZIPs for the old version; AeroNav may no longer offer it. If you cannot obtain the matching old complete ZIP, leave those files in place. Launchpad will not guess which files belonged to that package.

The cleanup window shows the reviewed EuroScope data folder. From the main window, this is the folder saved in **App settings**; from an installation result, it is the folder just updated. Check this is the copy you use. To change it, close cleanup and choose the correct folder first.

Choose the old and new complete ZIPs, then **Compare packages**. Launchpad checks that the newer sector files are installed and compares the package contents with files in the data folder. Comparing changes nothing. Every file starts unchecked; the reason beside each file explains whether it can move or must stay.

The preview can include:

- Old Swedish sector and companion files that are absent from the newer package.
- Root-level profiles and plugin DLLs supplied by the old package and removed from the newer one.
- Local runway settings for a retired sector generation, when its matching sector files still match the old package.

Files with local changes, files referenced by retained profiles or radar screens, the selected profile and other configured applications are kept. Staged **Updated Plugin** folders and unrelated or unknown files are kept too. A file can be old without being safe to remove.

An old profile can keep its sector or plugin files in use. If you archive that unused profile, **compare the packages again** to review files it no longer references. Selecting the profile does not also select or remove its dependencies in the same operation. For several older package versions, repeat the comparison with each old complete ZIP against the package currently installed.

Cleanup stops if a profile points to a missing profile or radar-screen file, or one outside the data folder. It cannot establish which files those references still need. Leave the files in place and review that configuration before trying again.

An unfinished managed GNG update also blocks comparison and archiving. Resolve installation recovery first. Cleanup's restore action remains available if you need to return previously archived files.

## Move selected files into a backup

Review the reasons, select the files you want to move and choose **Move selected to backup…**. The confirmation shows the data folder and backup location. Close EuroScope before continuing.

Launchpad checks the files and references again before moving them. The backup folder is shown before you compare; dated backups go under `%LOCALAPPDATA%\VatscaUpdateChecker\CleanupBackups`. The data folder and backup must be on the same local volume; network locations and folder links are not supported. Launchpad shows an early notice if the paths appear to be on different drives and verifies the paths again before moving files.

Moving files to a backup clears them from the active GNG folder **without freeing disk space**. Keep the backup until you are confident the remaining configuration works.

Wait for the operation to finish. The result shows how many files moved, any files skipped and the backup path. Skipped files remain in place. If only part of the selection moved, keep the backup and read the reasons before comparing again. Launchpad does not automatically delete cleanup backups.

## Restore a backup

Open cleanup and choose **Restore a backup…**; the package ZIPs are not needed. Select the dated folder inside `CleanupBackups`, keeping its `cleanup-manifest.json` and file structure intact.

The confirmation shows the destination. It must be the same configured EuroScope data folder from which the files were archived. Close EuroScope first. Restore verifies each file and moves it out of the backup into its original relative path. Existing destination files are never overwritten; any skipped files stay in the backup for manual review. Running restore again will skip files already restored.

Each cleanup pass creates a separate dated backup; to undo several passes, restore sector and plugin files first, then the old profiles that reference them.

Cleanup backups can include profiles containing controller details or passwords. Keep them private and do not attach them to public issues. Full GNG removal and clean reinstalls use the separate [Remove / reset workflow](uninstallation.md).
