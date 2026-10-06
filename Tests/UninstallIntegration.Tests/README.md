# Uninstall integration regression checks

Run on Windows:

```powershell
dotnet run --project Tests/UninstallIntegration.Tests/UninstallIntegration.Tests.csproj -c Release
```

The harness links the uninstall-registration and maintenance-lock services, using temporary
files, injected registration records and non-executing process adapters.

Coverage includes registration and quiet-uninstall preservation, helper commands and
identity checks, malformed manifests, stale finalizer tickets, helper cleanup and named-lock
behavior. Temporary junctions check that configured clients and recovery exports cannot
be hidden inside Launchpad's installation root through filesystem aliases.

Setup registration, the copied helper's process handoff, Windows Apps UI and native
uninstallation require separate integration testing. See
[removal and recovery](../../docs/uninstallation.md) for retained data and helper files.
