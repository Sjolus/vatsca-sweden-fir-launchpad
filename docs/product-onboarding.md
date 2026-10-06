# Setting up your ATC applications

You can use Launchpad with an existing setup or install supported applications from scratch. Choose only the tools you need. GNG installation remains manual in 2.0.

## Keep your existing setup

Open **Setup guide…** and choose the existing-applications route. The guide opens automatically for new users; you can skip it and return later.

1. Choose **Find installed programs…**. Launchpad checks installed-app records and common folders for EuroScope, TrackAudio, VACS, vATIS, Swedish GNG and VatEFS.
2. Select the copies you use. Existing saved locations are kept unless you choose to replace them.
3. Review the locations in Settings and save. For a custom location, use **Choose files and folders…**.

Linking an application saves its location. It does not move files, change permissions or import its settings. You can continue using your current configuration without using Launchpad's controller-profile features.

When choosing locations:

- For an application, select its installed `.exe`, rather than a shortcut or installer.
- For Swedish GNG, select the EuroScope data folder containing `ESAA`, Swedish profiles and sector files.
- For VatEFS, select the folder containing `VatEFS.dll`.

Launchpad checks the selected files and folders. Correct or clear invalid new selections; unfamiliar custom locations need confirmation. A valid location does not necessarily mean Launchpad supports updating or removing that installation.

## Install missing applications

Open an application's **Details…**, then **Set up…**. Check the destination and installation options before confirming.

| Application | Installation | Existing settings |
| --- | --- | --- |
| EuroScope | Sweden's supported **3.2.3.2** build. Existing installations also have repair and version-switch options. | Existing program/data folders are backed up before changes. See [EuroScope management](euroscope-management.md). |
| VACS | An all-users installation under Program Files. Windows asks for administrator permission. | Known settings for the current user can be reused; Launchpad exports them first. |
| TrackAudio | An installation for the current Windows user. | Existing TrackAudio settings can be reused; Launchpad exports them first. |
| vATIS | The supported beta.19 package, with an explicit choice to use the beta. | An existing vATIS folder must be handled through update or removal first. Setup cannot overwrite it. |

If Launchpad finds an existing copy or leftover installation files, use the existing-applications route or **Remove / reset…** before trying a fresh install. Unsupported layouts can be installed manually through the vendor.

You can cancel a download or preparation. Once installation starts, let it finish. Windows may request administrator permission, but Launchpad does not ask the client to open afterward.

## Install a missing runtime

Setup shows a separate action when a Microsoft runtime is needed:

- **VACS:** machine-wide WebView2.
- **TrackAudio:** Visual C++ x64.
- **EuroScope:** Visual C++ x86.

Confirm the runtime installation when prompted. If Windows requires a restart, restart before continuing. Shared runtimes stay installed when you remove Launchpad or an ATC application.

## Finish configuration

**Controller profile** lets you enter controller details and preview changes to supported EuroScope files. It is optional. The last setup page also links to the relevant VATSCA guides for EuroScope/GNG, TrackAudio audio/PTT and Sweden's vATIS profile.

**Finish** saves the guide's preferences. **Skip for now** or closing it discards unfinished preferences but keeps installations and choices you already saved in another dialog. If saving fails, Launchpad warns that those choices are retained only until the app closes.

## Start again with a clean setup

Use **Remove / reset…** to choose applications and settings separately. Keep the recovery export unless you intend permanent deletion. Then install the missing applications again. See [removal and recovery](uninstallation.md) for what each option removes.

GNG and vATIS store personal settings alongside their program/package files. Keeping those settings during removal means exporting them for manual restoration. Launchpad does not find every custom ATC file on your computer.

Install GNG using the [Swedish installation guide](https://wiki.vatsim-scandinavia.org/books/general/page/euroscope-and-gng-package-installation), then select its folder in Launchpad. VatEFS setup and EuroScope plugin references may still need manual configuration. VATIRIS opens as a web application.
