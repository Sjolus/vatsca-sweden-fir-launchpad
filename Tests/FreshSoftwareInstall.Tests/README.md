# Fresh client installation regression checks

```powershell
dotnet run --project Tests/FreshSoftwareInstall.Tests/FreshSoftwareInstall.Tests.csproj -c Release
```

Run on Windows. The harness links the production coordinator, package verifier and
release parser, using temporary files and injected network, registry and installer results.

Coverage includes existing/partial-installation conflicts, retained-settings exports,
package verification, changed files/releases, download failures, cancellation, concurrent
operations, installer failure and post-install identity/scope checks.

Command assertions cover machine-wide VACS and current-user TrackAudio installation,
the final unquoted NSIS `/D=` argument, and omission of run-after-install options. vATIS
checks reject every existing root and require beta opt-in without changing the update
channel of an existing stable installation.

See [vATIS setup checks](../VatisFreshInstall.Tests/README.md) and
[prerequisite checks](../SoftwarePrerequisite.Tests/README.md) for their adapters. These
tests simulate installation; vendor behavior, UAC and settings reuse require native testing.
