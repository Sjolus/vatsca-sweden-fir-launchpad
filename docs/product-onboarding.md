# Setting up your ATC applications

You can use Launchpad with an existing setup or install supported applications from scratch, including [Swedish GNG](gng-updates.md). Choose only the tools you need.

This guide describes Launchpad 2.5.0 and later.

## Keep your existing setup

Open **Setup guide…** and choose the existing-applications route. The guide opens automatically for new users; you can skip it and return later.

1. Choose **Find installed applications**. Launchpad checks installed-app records and common folders, showing the copies it finds beside each application.
2. Select a copy, then choose **Use this copy**. Its location is checked and saved; existing saved paths change only when you choose a replacement.
3. For a custom location, use **Choose copy…** (or **Choose folder…** for VatEFS). GNG has a separate data-folder choice on the next step.

Linking an application saves its location. It does not move files, change permissions or import its settings. You can continue using your current configuration without using Launchpad's controller-profile features.

Mark an optional application **Not used** to remove its warnings from the attention count. Its location and data remain available; clear that choice to include it again. Choosing or installing a copy through the guide includes it again automatically.

When choosing locations:

- For an application, select its installed `.exe`, rather than a shortcut or installer.
- For Swedish GNG, select the EuroScope data folder containing `ESAA`, Swedish profiles and sector files.
- For VatEFS, select the folder containing `VatEFS.dll`.

Launchpad checks the selected files and folders. Correct or clear invalid new selections; unfamiliar custom locations need confirmation. A valid location does not necessarily mean Launchpad supports updating or removing that installation.

## Install missing applications

Open an application's **Details…**, then **Install…** (**Set up…** for EuroScope). Check the destination and installation options before confirming.

| Application | Installation | Existing settings |
| --- | --- | --- |
| EuroScope | Sweden's supported **3.2.3.2** build. Existing installations also have repair and version-switch options. | Existing program/data folders are backed up before changes. See [EuroScope management](euroscope-management.md). |
| VACS | An all-users installation under Program Files. Windows asks for administrator permission. | Known settings for the current user can be reused; Launchpad exports them first. |
| TrackAudio | An installation for the current Windows user. | Existing TrackAudio settings can be reused; Launchpad exports them first. |
| vATIS | The supported beta.19 package, with an explicit choice to use the beta. | An existing vATIS folder must be handled through update or removal first. Setup cannot overwrite it. |
| VatEFS | The official MSI, including prereleases, under `%ProgramFiles%\VatEFS` for all users. Close EuroScope and the VatEFS backend first. | Fresh installation refuses an existing registration or folder. Supported installed copies use the update action with a recovery backup. External profiles and browser settings stay in place. |
| SAAMAN | The latest stable plugin release, available from the guide's Controller profile step. An existing DLL can also be selected. | Installing the DLL leaves profiles unchanged. Enable the optional APP/ACC integration separately after reviewing it. |
| Swedish GNG | Choose Download Swedish GNG and sign in if asked. Launchpad chooses Update Only for a recognized existing setup, or Full Package for setup/repair; review installation afterward. Original ZIP import is also available. | Supported personal files and profile fields are preserved. Shared defaults are replaced with backups; read the preview before confirming. |

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

The normal route has six steps: Welcome, Applications, Swedish GNG, Controller profile, Fonts and Finish. Every step can be skipped. The preferences-only route skips application, GNG and font setup.

At the GNG step, keep your existing data folder or choose **Install or update GNG…**. The package window has its own review and confirmation. After a completed installation, the guide resumes at Controller profile; cancelling returns to the GNG step.

**Controller profile** lets you enter details and preview changes to supported EuroScope files. Choose **Preview** before **Save & apply**; with a configured target, this updates its Swedish profiles. Without an available target folder, **Save profile** stores the details in Launchpad only. Keeping your existing configuration is also fine.

This step also offers **Set up / update SAAMAN…**. After choosing its DLL, review the optional APP/ACC profile change with **Preview SAAMAN** and **Apply SAAMAN only**. This separate action does not save controller credentials. See [SAAMAN setup and recovery](third-party-updates.md#saaman).

At Fonts, choose **Check fonts**. Missing or older fonts can be opened in Windows font preview; choose Install there, then check again. The guide does not install fonts automatically or start EuroScope. The last page also links to the relevant VATSCA guides for EuroScope/GNG, TrackAudio audio/PTT and Sweden's vATIS profile.

**Finish** saves appearance and startup preferences. **Continue later** or closing the guide keeps your place, selected paths and completed installations/profile actions. Unfinished appearance preferences are discarded. Reopen **Setup guide…** to continue. If saving fails, Launchpad warns that the choices are retained only for the current session.

A pending Windows restart blocks further installation and controller-profile changes. Restart Windows before continuing; reopening Launchpad alone does not fulfil that requirement.

## Start again with a clean setup

Use **Remove / reset…** to choose applications and settings separately. Keep the recovery export unless you intend permanent deletion. Then install the missing applications again. See [removal and recovery](uninstallation.md) for what each option removes.

GNG and vATIS store personal settings alongside their program/package files. Keeping those settings during removal means exporting them for manual restoration. Launchpad does not find every custom ATC file on your computer.

Use [GNG setup](gng-updates.md) to install the Swedish package, then follow the [Swedish installation guide](https://wiki.vatsim-scandinavia.org/books/general/page/euroscope-and-gng-package-installation) for EuroScope configuration. After installing VatEFS, use **Controller profile → Preview** to review enabling its plugin. Fonts and custom EuroScope plugin references may still need manual configuration. VATIRIS opens as a web application.
