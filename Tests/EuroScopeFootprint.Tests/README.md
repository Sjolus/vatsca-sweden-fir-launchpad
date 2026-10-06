# EuroScope MSI footprint checks

```powershell
dotnet run --project Tests/EuroScopeFootprint.Tests/EuroScopeFootprint.Tests.csproj -c Release
```

Run on Windows. The harness tests the production inspector with temporary files and injected MSI identity, installation scope and component paths. It does not query installed products or run Windows Installer actions.

Coverage includes exact machine/managed/unmanaged scope, cached product/family/version identity, complete component file paths, another user's AppData, unsupported MSI actions, missing/ambiguous registrations, path traversal/streams/reparse points, and fingerprints used for review/apply revalidation.

An optional static parser check accepts an already-downloaded copy of the official pinned MSI:

```powershell
dotnet run --project Tests/EuroScopeFootprint.Tests/EuroScopeFootprint.Tests.csproj -c Release -- --inspect-package C:\isolated-research\EuroScopeSetup.3.2.3.2.msi
```

This mode opens the package with `MsiOpenDatabase` in read-only mode and checks its tables. Testing an installed MSI, elevation and changes to Windows Installer state requires a separate environment.
