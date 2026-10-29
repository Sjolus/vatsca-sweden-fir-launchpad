# Setting up your ATC applications

You can use Launchpad with an existing setup or install supported applications from scratch, including [Swedish GNG](gng-updates.md). Choose only the tools you need.

## Keep your existing setup

Open **Setup → Applications**. First launch includes a short guide through Applications, Swedish GNG and Controller profile. The **Apps** and **Setup** tabs stay visible; switching tabs does not finish the guide. It resumes after a restart. Choose **Finish setup** when done or **Later** to dismiss it, and use **Setup → Preferences → Open setup guide** to return. Optional tools never block completion.

1. Choose **Find installed apps…** to check installed-app records and common folders. Your current locations stay selected.
2. Choose any replacements, then **Done**. Unchanged locations are kept.
3. For a custom location, choose **Use existing…** beside an application, or **Location…** to change a saved one. GNG has its own folder choice under **Swedish GNG**.

Linking an application saves its location. It does not move files, change permissions or import its settings. You can continue using your current configuration without using Launchpad's controller-profile features.

Under **Setup → Preferences → Applications I don't use**, select applications to hide from Apps and update reminders. Their locations, data and EuroScope plugin choices stay unchanged; clear a checkbox to show an application again. Hidden applications remain available in Setup. Choosing or installing a standalone client includes it again; enabling a plugin also includes it. Installing VatEFS alone does not enable its plugin.

When choosing locations:

- For an application, select its installed `.exe`, rather than a shortcut or installer.
- For Swedish GNG, select the EuroScope data folder containing `ESAA`, Swedish profiles and sector files.
- For VatEFS, select the folder containing `VatEFS.dll`.

Launchpad checks the selected files and folders. Correct or clear invalid new selections; unfamiliar custom locations need confirmation. A valid location does not necessarily mean Launchpad supports updating or removing that installation.

## Install missing applications

Open **Setup → Applications**, then choose **Install…** beside the missing application. Check the destination and installation options before confirming.

| Application | Installation | Existing settings |
| --- | --- | --- |
| EuroScope | Sweden's supported **3.2.3.2** build. Existing installations also have repair and version-switch options. | Existing program/data folders are backed up before changes. See [EuroScope management](euroscope-management.md). |
| VACS | An all-users installation under Program Files. Windows asks for administrator permission. | Known settings for the current user can be reused; Launchpad exports them first. |
| TrackAudio | An installation for the current Windows user. | Existing TrackAudio settings can be reused; Launchpad exports them first. |
| vATIS | The supported beta.19 package, with an explicit choice to use the beta. | An existing vATIS folder must be handled through update or removal first. Setup cannot overwrite it. |
| VatEFS | The official MSI, including prereleases, under `%ProgramFiles%\VatEFS` for all users. Close EuroScope and the VatEFS backend first. | Fresh installation refuses an existing registration or folder. Supported installed copies use the update action with a recovery backup. External profiles and browser settings stay in place. |
| SAAMAN | Switch on **Use in APP / ACC** under **Setup → Applications**. A missing DLL is installed from the latest stable release. **Use existing…** selects an existing copy. | Enabling puts SAAMAN first in APP/ACC. Switching it off restores MAESTRO. Personal settings are kept. |
| Swedish GNG | Choose Install or Update under Setup → Swedish GNG and sign in if asked. Launchpad selects the appropriate package and shows an installation summary. Original ZIP import is also available. | Supported personal files and profile fields are preserved. Shared defaults are replaced with backups; follow any warnings in the summary. |

If Launchpad finds an existing copy or leftover installation files, use the existing-applications route or **Remove / reset…** before trying a fresh install. Unsupported layouts can be installed manually through the vendor.

You can cancel a download or preparation. Once installation starts, let it finish. Windows may request administrator permission, but Launchpad does not ask the client to open afterward.

## Install a missing runtime

Setup shows a separate action when a Microsoft runtime is needed:

- **VACS:** machine-wide WebView2.
- **TrackAudio:** Visual C++ x64.
- **EuroScope:** Visual C++ x86.
- **VatEFS:** the same Visual C++ x86 runtime used by its EuroScope plugin.
- **GNG browser:** WebView2. ZIP import remains available without it.
- **SAAMAN:** WebView2.

Confirm the runtime installation when prompted. If Windows requires a restart, restart before continuing. Shared runtimes stay installed when you remove Launchpad or an ATC application.

## Finish configuration

Setup keeps Applications, Swedish GNG, Controller profile, Preferences and Activity & recovery in one sidebar. **Preferences** controls compact layout, text size, staying on top and which applications you use. Opening a section does not change any files.

Under **Swedish GNG**, use **Maintenance → Use existing folder…** for a package you already have, or **Install Swedish GNG** for a new setup. New installations use `%APPDATA%\EuroScope`; Launchpad saves the location after installation succeeds or the package already matches. Existing setups offer **Update**. The update stays inside Launchpad and shows a short summary before installation. Optional **View details** provides individual file comparisons. **Check fonts…** opens missing or older fonts in Windows font preview; choose Install there if needed.

Edit your name, CID, rating and password under **Controller profile**. Switching sections keeps unfinished edits. **Save & apply** uses the details in configured EuroScope profiles, vATIS and TrackAudio; **Preview** is optional. Close the clients first. Unsupported copies are left unchanged, and the result explains what needs attention.

Fresh vATIS and TrackAudio installations also use saved controller details. vATIS receives the latest official Swedish airport profile if it has no automatic Swedish profile already. vATIS handles later airport-profile updates itself. Custom profiles, audio preferences and PTT are preserved. TrackAudio still needs its audio devices and PTT set up on a new installation.

VatEFS and SAAMAN have on/off switches under **Applications**. They change the EuroScope plugin entries immediately, with recovery copies, and leave controller details alone. Changing an enabled plugin's location also updates its profile references. Close EuroScope first. See [SAAMAN setup and recovery](third-party-updates.md#saaman).

Select the **Apps** tab to return to the application list. Completed actions are kept. Setup also contains Preferences and Activity & recovery. If saving fails, Launchpad warns that its choices are retained only for the current session.

A pending Windows restart blocks further installation and controller-profile changes. Restart Windows before continuing; reopening Launchpad alone does not fulfil that requirement.

## Start again with a clean setup

Use **Setup → Activity & recovery → Remove / reset…** to choose applications and settings separately. Keep the recovery export unless you intend permanent deletion. Then install the missing applications again. See [removal and recovery](uninstallation.md) for what each option removes.

GNG and vATIS store personal settings alongside their program/package files. Keeping those settings during removal means exporting them for manual restoration. Launchpad does not find every custom ATC file on your computer.

Use [GNG setup](gng-updates.md) to install the Swedish package, then follow the [Swedish installation guide](https://wiki.vatsim-scandinavia.org/books/general/page/euroscope-and-gng-package-installation) for EuroScope configuration. Enable VatEFS under **Setup → Applications → Use in EuroScope**. Fonts and custom EuroScope plugin references may still need manual configuration. VATIRIS opens as a web application.
