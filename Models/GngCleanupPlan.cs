namespace VatscaUpdateChecker.Models;

public sealed record GngCleanupCandidate(
    string RelativePath,
    long Length,
    string Reason,
    string? BlockReason,
    string LocalHash,
    string PackageHash)
{
    public bool IsEligible => BlockReason is null;
}

public sealed record GngCleanupPlan(
    string Root,
    string OldZip,
    string NewZip,
    string? SelectedProfile,
    IReadOnlyList<GngCleanupCandidate> Candidates,
    IReadOnlyList<string> Warnings,
    string OldZipHash,
    string NewZipHash,
    IReadOnlyList<string> ProtectedPaths);

public sealed record GngCleanupResult(
    string? BackupFolder,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Skipped);
