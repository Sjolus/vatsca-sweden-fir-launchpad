# GNG installer regression checks

Run on Windows from the repository root:

```powershell
dotnet run --project Tests/GngUpdate.Tests/GngUpdate.Tests.csproj -c Release
```

This dependency-free console harness links the production installer and models. It creates tiny ZIP packages, fake DLL/font files, Windows-1252 profiles, and fake credentials under a uniquely named temporary directory. An internal operation context keeps caches, journals and backups in that fixture directory and substitutes a synthetic process probe and maintenance gate. Production storage cannot be redirected through the public API. The harness never reads application settings or Credential Manager and never starts ATC applications. Fixtures are retained for inspection; the output prints their root.

The checks cover package validation, stale previews, configured-path protection, junction rejection, maintenance exclusion, profile and credential preservation, staged DLL promotion, RDF replacement, package-cache provenance, partial Update-Only plugin dependencies, recovery and rollback. Full and Update-Only ZIPs with the same AIRAC/revision still require nondecreasing build timestamps; equivalence cannot be inferred from the displayed version. Browser authentication, real package layouts and plugin compatibility require separate interactive verification.

Paired-download cases validate an Update-Only installation with a separate Full reference at the same AIRAC, package number and revision. The Full ZIP may have a different timestamp; it is cached unchanged and never extracted into the data folder. Tests cover reference changes after preview, mismatched versions, modified caches, legacy receipts and restoration of both package identities. Managed-plugin provenance continues to use the package actually applied. A cached Full reference does not establish that its sectors are installed or satisfy cleanup's separate compatibility checks.

Use `--filter "part of a test name"` to run a focused subset while investigating a failure.

Archive-only download validation is also checked independently of destination access: a locked synthetic local profile does not prevent selecting the matching reference, while an incomplete ZIP is refused. Full destination/profile validation still runs before the installation review.

File-comparison checks cover the exact proposed merged content, stale file/package rejection, promoted DLL mapping, credential masking, Windows-1252/BOM decoding, binary and large-file summaries, and bounded context diffs. All source text and credentials in these checks are synthetic.

List-layout cases cover the default-on preservation option, explicit package-default installation, native and TopSky list identities, profile-directed custom sources, conflicting routes, new/retired lists, encoding and exact restoration. They verify that only X/Y and whole-list visibility are retained, and that changed source files invalidate the review even when their coordinates remain the same.

Package-cache cases supply synthetic public AeroNav tables and original ZIPs. They cover full/partial reuse, AIRAC/package/revision changes, interrupted downloads, recovery-held originals, path boundaries and bounded failures. Cache lookup validates archives without reading the destination or changing cached files.

`--ui-fixture` creates one synthetic installation and update ZIP and prints both paths for manual Launchpad UI checks. `--preview <zip> <existing-isolated-data-folder>` only builds a plan, reporting package name, action counts, and warnings. Use an isolated fixture directory, never the user's active EuroScope installation.
