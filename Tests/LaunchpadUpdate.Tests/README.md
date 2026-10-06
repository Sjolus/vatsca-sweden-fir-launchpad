# Launchpad update regression checks

Run from the repository root:

```powershell
dotnet run --project Tests/LaunchpadUpdate.Tests/LaunchpadUpdate.Tests.csproj -c Release
```

The harness links the production service/model and the pinned Velopack SDK. It uses temporary installations, local feeds and synthetic packages. Network responses are injected, and a fake locator rejects process starts and exits; native update application and restart require separate testing.

Coverage includes installed/portable detection, missing feeds, stable-channel and package identity checks, SDK downloads, checksum and filename validation, corrupt caches, recovering staged updates offline, progress, cancellation, concurrent operations and retries. Temporary files are removed after each check.
