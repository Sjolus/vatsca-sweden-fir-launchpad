# Launchpad update regression checks

Run from the repository root:

```powershell
dotnet run --project Tests/LaunchpadUpdate.Tests/LaunchpadUpdate.Tests.csproj -c Release
```

This console harness links the production service/model and uses the pinned Velopack SDK. Each check creates a temporary installation, local release feed, and synthetic `.nupkg` files. A fake locator rejects process starts and exits. The harness never calls Apply/Restart, launches an app, installs anything, or contacts the network. The GitHub compatibility check uses an in-memory downloader with a legacy release response.

Coverage includes installed/portable guards; missing feeds versus current releases; stable-channel and package identity checks; actual SDK downloads; checksum and filename validation; corrupt cached downloads; verified pending updates after restart while offline; progress; cancellation; overlapping operations; and retry behavior. Temporary fixture directories are removed after each check.
