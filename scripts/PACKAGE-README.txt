Sweden FIR Launchpad - Windows package

Run SwedenFirLaunchpad-win-x64-Setup.exe to install for your Windows user.
The portable ZIP can be unpacked and run without a managed installation.
Both include .NET. Setup can download WebView2 if it is missing and creates
a Start Menu shortcut.

This package is signed with a self-signed development certificate. Windows
does not trust it by default, and SmartScreen warnings may still appear.
Do not import the certificate into Trusted Root Certification Authorities or
Trusted Publishers to suppress warnings. The .cer contains only the public
certificate; no private key is included.

The managed app is installed in:
  %LOCALAPPDATA%\SwedenFirLaunchpad
Preferences, logs and dedicated VatEFS/VATIRIS browser profiles are kept in:
  %APPDATA%\VatscaUpdateChecker
Downloads and recovery backups are kept separately in:
  %LOCALAPPDATA%\VatscaUpdateChecker

Installed copies offer explicit Download update and Restart to update actions.
Portable copies use the release-page download instead. Checks do not install
updates, and staged updates are not applied automatically at startup.

Interactive uninstall opens Launchpad's removal review. Removing Launchpad
alone keeps ATC applications, separate data folders and saved credentials.
Additional removal/reset choices must be selected explicitly. Native quiet
uninstall also preserves separate data. A temporary maintenance helper may
remain until Windows or user temporary-file cleanup.

Recovery exports can contain plain-text profile credentials. Keep them private
and follow their RESTORE.txt when restoring data. Some application removals
require manual restoration to keep settings. Shared runtimes are not removed.

User guide and releases:
  https://github.com/Sjolus/vatsca-sweden-fir-launchpad
License: GNU GPL version 3; see LICENSE.txt in the application folder.
ATC clients and GNG packages are installed separately.
