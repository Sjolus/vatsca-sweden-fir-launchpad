Sweden FIR Launchpad - development/test installer

This build is signed with a self-signed development certificate. Windows does
not trust that certificate by default; signing does not establish a verified
publisher or remove SmartScreen warnings. Do not import this certificate into
Trusted Root Certification Authorities or Trusted Publishers to suppress them.
The .cer file contains only the public certificate. No private key is included.

Setup installs for the current user under:
  %LOCALAPPDATA%\SwedenFirLaunchpad
The installer can download Microsoft's WebView2 Runtime if it is missing.
The application bundles its .NET runtime. Start Menu shortcuts are included.

Launchpad's existing settings remain in %APPDATA%\VatscaUpdateChecker.
Existing application data under %LOCALAPPDATA%\VatscaUpdateChecker also remains
separate. These data folders are not part of the Velopack installation and are
preserved when the application is removed.

Source: https://github.com/Sjolus/vatsca-sweden-fir-launchpad
License: GNU General Public License version 3; see LICENSE.txt in the app folder.
This package does not include AeroNav GNG data or user credentials.
