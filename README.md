# VATSCA Launchpad

A Windows desktop app for [VATSIM Scandinavia](https://vatsca.org) controllers. Check versions, launch configured ATC tools, manage supported installations and apply your controller details to EuroScope profiles.

This README describes **Launchpad 2.0.1**. GNG package installation and old-package cleanup belong to later releases.

![Launchpad's compact application list with TrackAudio selected and its update status shown below](Assets/screenshot.png)

*Compact view with application details, shown in a development build.*

## Install Launchpad

Use Windows 10 or 11 on an x64 PC. For installer [releases](https://github.com/Sjolus/vatsca-sweden-fir-launchpad/releases), download `SwedenFirLaunchpad-win-x64-Setup.exe`. Setup installs for your Windows user and creates a Start Menu shortcut. The installer and portable ZIP include .NET; framework-dependent builds require the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0). Setup can install WebView2 if it is missing. Microsoft Edge is used for the VatEFS and VATIRIS web-app windows.

The release packages use a self-signed development certificate. Windows may show an unknown-publisher or SmartScreen warning; this signature does not establish a publicly trusted publisher. Do not add the certificate to a trusted certificate store to suppress those warnings.

## Set up your tools

The optional setup guide opens when no settings have been saved. Existing users can open **Setup guide…** from the main window. Every application is optional.

1. **Use existing applications:** choose **Find installed programs…** to look for EuroScope, TrackAudio, VACS and vATIS, plus GNG and VatEFS folders. Select the copies you use, then review and save their locations. Nothing is selected by default. Use **Choose files and folders…** for custom locations.
2. **Install missing applications:** use the individual setup buttons. Each installation has its own prerequisite checks, review and confirmation. GNG installation remains manual in 2.0.
3. **Configure your profile, if wanted:** open **Controller profile**, check the EuroScope target folder and choose **Preview** before **Save & apply**. Without an available target folder, **Save profile** saves the details in Launchpad only.
4. Choose your theme, compact or expanded rows, and startup version checks. Choose **Finish** to save these preferences.

**Skip for now** discards unfinished preferences but keeps paths and profiles you explicitly saved, and installations already completed. Discovery only links paths; it does not import another application's configuration. Existing configurations can be kept without using profile sync.

Settings checks program identity and expected folder contents without starting anything. Wrong or missing new selections must be corrected or cleared; unrecognised custom locations need your confirmation. Existing saved choices can stay while you change other preferences. These checks do not confirm client configuration or support for automatic updates/removal.

The final page links to relevant VATSCA guides for EuroScope/GNG, TrackAudio and Sweden's vATIS profile. See [setup choices](docs/product-onboarding.md) for supported layouts and limitations.

## Everyday use

- **Check for Updates** checks versions. Downloads, installations and application launches require a separate action.
- Use a row's **Launch** control to start a configured tool. The EuroScope profile picker selects which `.prf` to open.
- Choose **Details…** beside an application for its explanation, update, setup and configuration actions. The panel names the selected application; long messages can be scrolled and copied.
- **Compact** keeps equal-height rows with details below. **Expanded** adds a short explanation to every row; its details panel opens on demand. Both support light and dark themes.
- Use **Controller profile → Preview** to inspect proposed profile changes before applying them.

Profile sync has two known limitations: the sync indicator checks only the first matching `ES*.prf`, and a blank Hoppie code can preview clearing a file even though Apply leaves it unchanged.

## Install or update ATC software

| Application | Supported route |
| --- | --- |
| EuroScope | Open **Details… → Set up…** for a missing copy or **Manage…** for an existing one. Install, repair or switch to the pinned **3.2.3.2** token-authentication build. Existing installations need a recovery folder; switching from a higher version is a downgrade. |
| VACS | Update recognized 2.x NSIS installations; fresh setup uses an all-users installation. A per-user installer that requires elevation must be handled manually. |
| TrackAudio | Install or update a recognized current-user installation. |
| vATIS | Update the recognized standard per-user layout, with a complete recovery backup. Fresh setup supports the reviewed beta.19 package only after explicit beta opt-in, and refuses an existing installation/data folder. |
| GNG and other custom tools | Configure their paths; installation remains manual in 2.0. |

Close the target application first. Launchpad downloads and verifies the official package, then shows installation status. Its supported installer routes do not request normal client launch. Windows elevation and prerequisite prompts may still appear.

Downloads and preparation can be cancelled. Once installation starts, wait for it to finish; other setup/profile changes and Launchpad's Close action are blocked. Finish the operation before uninstalling through Windows. If an installer requests a Windows restart, restart before further maintenance; the notice and guard last for the current Launchpad session.

Unknown layouts, unsupported helper versions or missing prerequisites may require manual installation. After a failed update, check again before retrying. Do not run vATIS Setup over an existing data folder.

Read [client update support](docs/third-party-updates.md) and [EuroScope management](docs/euroscope-management.md) for the detailed restrictions and recovery steps.

## Remove applications or reset data

Open **Remove / reset…**. Select application removal and settings/data deletion separately, review the paths, then confirm. A private recovery export is enabled by default and is required for some preservation choices. Windows uninstall preselects Launchpad itself; other removal choices start unchecked.

Keeping vATIS or GNG settings when removing the application/package means saving an external export and restoring it manually. GNG reset removes the reviewed package and its personal files. VatEFS removal covers the configured DLL; its profile references need manual repair. Follow the export's `RESTORE.txt` if recovery is needed.

After a removal failure, selections are cleared and discovery refreshes before another review, unless Windows requires a restart. A completed export is retained. Removing Launchpad alone keeps ATC clients, shared runtimes and Launchpad's separate data folders. Its interactive Windows uninstaller offers the same review; native quiet uninstall keeps the separate data.

See [removal and recovery](docs/uninstallation.md) before a clean reinstall.

## Update Launchpad

Installed copies use the **Sweden FIR Launchpad** row: open **Details…**, choose **Download update**, then **Restart to update**. The app follows this repository's stable Windows feed. A staged update is not applied automatically at startup; reopen Launchpad and check again to recover its ready state.

Portable and standalone copies use the [release-page download](https://github.com/Sjolus/vatsca-sweden-fir-launchpad/releases) instead. Use that page if no compatible feed is published or an in-app update cannot proceed. Close other dialogs and finish maintenance before restarting to update.

## Data and privacy

| Location | Contents |
| --- | --- |
| `%LOCALAPPDATA%\SwedenFirLaunchpad` | Managed Launchpad installation |
| `%APPDATA%\VatscaUpdateChecker` | Preferences, controller identity fields, logs and dedicated VatEFS/VATIRIS browser profiles |
| `%LOCALAPPDATA%\VatscaUpdateChecker` | Downloaded packages and recovery backups |
| Windows Credential Manager | Launchpad's saved VATSIM password and Hoppie code |

Profile sync writes the password and Hoppie code into the external tools' text files. Those files and recovery exports can contain plain-text credentials; keep them private. **Delete saved credentials** removes only Launchpad's two Credential Manager entries, not copies in profiles or backups. Credential Manager secrets are not exported.

vATIS update backups are under `%LOCALAPPDATA%\VatscaUpdateChecker\SoftwareUpdates\Backups`. Keep the complete folder, including its manifest and `RESTORE.txt`; Launchpad does not automatically delete these backups.

Version checks use GitHub, `vatis.app` and `files.aero-nav.com`. Explicit setup/download actions also contact vendor and Microsoft download hosts. VatEFS opens `http://localhost:17770`; VATIRIS opens `vatiris.se`. See [SECURITY.md](SECURITY.md) for security reporting.

## Building from source

Build on Windows with the .NET 9 SDK:

```powershell
dotnet build vatsca-update-checker.sln -c Release
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for tests, packaging and pull requests.

Licensed under [GPL v3](LICENSE).
