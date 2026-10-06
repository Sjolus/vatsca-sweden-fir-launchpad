# Profile regression checks

```powershell
dotnet run --project Tests/Profile.Tests/Profile.Tests.csproj -c Release
```

This harness links `ProfileService` and `AppSettings`, with in-memory credential and logger substitutes. It creates and removes temporary profile files using fake credentials.

Coverage includes unavailable targets, masked previews, preview/apply agreement, Windows-1252 Swedish text and preservation of unrelated plugin rows when adding VatEFS. Known multi-profile sync, blank-Hoppie preview and existing-display reconciliation limitations remain outside these cases.
