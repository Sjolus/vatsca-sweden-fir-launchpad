# EuroScope installation and repair

Launchpad uses **EuroScope 3.2.3.2**, the token-authentication build recommended by the [Swedish GNG installation guide](https://wiki.vatsim-scandinavia.org/books/general/page/euroscope-and-gng-package-installation). A newer public EuroScope release is not automatically recommended for Sweden.

## Install, repair or change versions

Open EuroScope's **Details…**, then **Set up…** for a missing copy or **Manage…** for an existing one. You can install the supported build, repair it, or replace another version. Switching from a newer version is shown as a downgrade.

For an existing copy, first select its location in Settings. Launchpad needs a matching Windows Installer registration; portable or unfamiliar installations require manual handling.

Choose a private recovery folder, select **Review change**, then confirm **Apply reviewed change**. Windows may ask for administrator permission. Downloads and backup preparation can be cancelled; let an installer finish once it has started. EuroScope does not open automatically afterward.

If removing the old version requires a restart, replacement stops before installing the supported build. Restart Windows, then return to review the remaining installation. You may need to update or clear the old executable path in Settings.

## Settings and recovery

Before changing an existing installation, Launchpad exports the program folder and existing data locations: the configured EuroScope folder, `%APPDATA%\EuroScope` and the older Documents\EuroScope folder. Keep the export private; it may contain login details.

The EuroScope installer owns some sample profiles, settings and a font as well as the program files. Repair or removal can change those files. Follow the export's `RESTORE.txt` to restore personal settings afterward; avoid copying old executables or DLLs over the new installation.

Profiles can refer to files elsewhere. Check any custom locations separately—Launchpad cannot discover every reference. See [EuroScope's settings documentation](https://www.euroscope.hu/wp/where-are-my-settings-saved/). GNG installation and removal are separate actions.

## Launch folder

Launchpad uses the configured EuroScope data folder as the shortcut's **Start in** directory. With no configured data folder, it uses the selected profile's folder, or `%APPDATA%\EuroScope` when no profile is selected. A missing configured folder or profile stops the launch and shows an error.

## Visual C++ runtime

Use **Check Visual C++ requirement** to check the x86 runtime. If needed, confirm the separate Microsoft runtime installation. Launchpad uses v14 **14.44.35211.0 or newer** as its supported baseline. Restart Windows if requested.

## Installer details

The [official 3.2.3.2 MSI](https://euroscope.hu/install/EuroScopeSetup.3.2.3.2.msi) is unsigned. Launchpad checks its exact expected size and SHA-256 before running it. Windows Installer records the package as **3.2.3**; Launchpad also checks the executable's full **3.2.3.2** version. See the [vendor's token-update notes](https://www.euroscope.hu/wp/2024/06/09/v3-2-2-3-and-v3-2-3-2-with-token-authentication-update/) for this release.
