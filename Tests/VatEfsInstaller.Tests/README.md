# VatEFS installer checks

Run `dotnet run --project Tests/VatEfsInstaller.Tests/VatEfsInstaller.Tests.csproj -c Release` on Windows.

The harness uses temporary synthetic files, MSI records, process checks and a non-executing installer delegate. It covers supported MSI identity/layout, closed EuroScope/backend requirements, fresh-install residue checks, full recovery exports, stale/corrupt backups, package locks, quiet installer arguments, cancellation and restart handling. It does not query installed applications, start clients, invoke MSI installation or use real settings.

An optional `--inspect-msi <path>` reads only MSI tables from an explicitly supplied original package. It neither installs nor extracts the package. `--verify-system-installer` checks the signature on the system's `msiexec.exe` without starting it. Synthetic success does not establish native UAC, upgrade, settings-preservation or restart behavior; those require a separately authorized disposable-machine test.
