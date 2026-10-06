# Pinned EuroScope installation regression checks

```powershell
dotnet run --project Tests/EuroScopeInstall.Tests/EuroScopeInstall.Tests.csproj -c Release
```

Run on Windows. This harness tests the production service with temporary files, fake
downloads, injected MSI registrations and a non-executing process runner.

Coverage includes the pinned 3.2.3.2 policy; install, repair and replacement reviews;
explicit downgrades; conflicting installations; verified recovery before removal;
changed files and registrations; download verification; cancellation and concurrent
operations; and failed, cancelled or restart-required installer results. Success requires
the expected executable, registration, scope and data location after installation.

Related harnesses cover the [MSI footprint](../EuroScopeFootprint.Tests/README.md) and
the x86 runtime helper (`EuroScopePrerequisite.Tests`). Actual MSI installation, elevation
and recovery require integration testing. Package identity and recovery limits are
documented in [EuroScope management](../../docs/euroscope-management.md).
