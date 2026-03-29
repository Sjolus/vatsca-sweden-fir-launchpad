# Changelog

All notable changes to VATSCA Launchpad will be documented here.

---

## [1.0.0] — 2026-03-29

Initial public release.

### Features
- Update checker for EuroScope, GNG Pack, TrackAudio, VACS, and vATIS
- One-click launch and kill for all supported tools
- EuroScope profile picker — preselect a `.prf` file to skip the profile dialog on launch
- Profile sync — write VATSIM name, CID, password, rating, and Hoppie ACARS code to all EuroScope `.prf` files at once
- Dry-run preview — inspect every file change before applying
- Credentials stored in Windows Credential Manager (never written to disk in plain text)
- Light and dark theme, persisted across sessions
- Application log at `%APPDATA%\VatscaUpdateChecker\launchpad.log`
