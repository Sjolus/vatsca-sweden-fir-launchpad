# VatEFS version checks

Run from the repository root on Windows:

```powershell
dotnet run --project Tests/VatEfs.Tests/VatEfs.Tests.csproj -c Release
```

The harness supplies synthetic Windows Installer registrations and file inventories. It checks matching the configured installation and handling unknown/custom copies. Release selection and update coordination are covered by `Tests/SoftwareUpdate.Tests`; MSI installation checks live in `Tests/VatEfsInstaller.Tests`. No test queries real registrations, reads user settings, contacts a backend, launches applications or installs anything.
