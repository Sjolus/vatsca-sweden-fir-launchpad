# Microsoft prerequisites regression harness

Run `dotnet run --project Tests/SoftwarePrerequisite.Tests/SoftwarePrerequisite.Tests.csproj -c Release`.
The default run uses temporary files and injected network, registry, trust and process functions to test prerequisite detection, package verification and installer outcomes.

Optional static inspection of an already downloaded official Microsoft package:

```powershell
dotnet run --project Tests/SoftwarePrerequisite.Tests/SoftwarePrerequisite.Tests.csproj -c Release -- --verify-package Vacs <absolute-WebView-bootstrapper-path>
dotnet run --project Tests/SoftwarePrerequisite.Tests/SoftwarePrerequisite.Tests.csproj -c Release -- --verify-package TrackAudio <absolute-vc_redist.x64.exe-path>
```

These modes check executable identity and Authenticode trust without executing the package. Windows trust verification may contact certificate revocation services. Runtime installation, UAC and registration timing require native integration testing.
