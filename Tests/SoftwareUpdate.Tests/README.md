# Application update regression checks

Run from the repository root:

```powershell
dotnet run --project Tests/SoftwareUpdate.Tests/SoftwareUpdate.Tests.csproj -c Release
```

The harness links the production coordinator, release source, version parser and adapters.
Its default run uses temporary files and injected HTTP, installation, trust and process
fixtures; it is offline and does not run installers.

Coverage includes explicit update actions, version/channel selection, malformed catalogs,
changed installations and releases, running-client checks, download verification and
limits, cancellation, concurrent operations, backup retention and post-install verification.
Adapter checks inspect command arguments; vendor behavior requires integration testing.

An optional read-only check accepts an already downloaded official vATIS beta.19 or
beta.20 full package, retaining its original `org.vatsim.vatis-<version>-full.nupkg`
filename. It checks the pinned official size/digest and package contents using production
validation:

```powershell
dotnet run --project Tests/SoftwareUpdate.Tests/SoftwareUpdate.Tests.csproj -c Release -- --verify-vatis-package <absolute-package-path>
```

This mode inspects the package without executing it. Vendor package signatures are
validated independently of Launchpad's development signing certificate.
