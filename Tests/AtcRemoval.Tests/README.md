# ATC removal regression checks

```powershell
dotnet run --project Tests/AtcRemoval.Tests/AtcRemoval.Tests.csproj -c Release
```

Run on Windows. The harness creates and deletes its own temporary files, including NTFS
junction and sparse-file fixtures. Application discovery, registry records and vendor
execution are injected; installed applications and user data are not used.

Coverage includes:

- Discovery and removal selection, protected paths, overlapping targets and vendor commands.
- Exact inventories, including empty folders and individual files; content and identity
  changes between review and deletion; handle locks and unreviewed files arriving later.
- Verified exports, corruption and interrupted backups, and revalidation before deletion.
- Rejection of junctions, broad/system roots, network paths and alternate data streams,
  including `Zone.Identifier`; inventory depth, count and byte limits.

The tests exercise file operations and simulated vendor outcomes. Installed uninstallers,
UAC and application recovery need separate integration testing. See the
[removal guide](../../docs/uninstallation.md) for supported targets and restoration limits.
