# GNG cleanup tests

Run the console harness on Windows with the .NET 9 SDK:

```powershell
dotnet run --project Tests/GngCleanup.Tests/GngCleanup.Tests.csproj -c Release -- --archive
```

The harness links the production cleanup and GNG recovery services, models, maintenance gate and path resolver. All package files, profiles and recovery backups are synthetic and stay under a unique `%TEMP%\VatscaCleanupTests` folder. Synthetic process probes, isolated storage and a separately named maintenance lock keep it independent of running ATC applications. It never reads Launchpad settings, credentials, real profiles or existing recovery backups.

Omit `--archive` to run planning checks only. Archive checks also cover byte-preserving restore, collisions, tampered manifests, configured-path protection, operation locking, pending-update recovery and concurrent sector edits. Cleanup restore remains available while GNG installation recovery is pending. Fixtures remain available for inspection; their locations are printed at the end. Use `--filter "exact test name"` to run one case.

The optional `--packages <previous-full.zip> <current-full.zip>` check reads supplied archives and copies only their root SCT/ESE files into a new temporary fixture. It checks package structure without installing or archiving anything.
