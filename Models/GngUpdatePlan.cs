namespace VatscaUpdateChecker.Models;

internal sealed record GngUpdateDependency(string Path, string? Hash, long Length);

public sealed record GngUpdateFile(string RelativePath, string Action, string Detail)
{
    internal string? BeforeHash { get; init; }
    internal long BeforeLength { get; init; }
    internal string AfterHash { get; init; } = string.Empty;
    internal long AfterLength { get; init; }
    internal byte[]? MergedBytes { get; init; }
    internal IReadOnlyList<GngUpdateDependency> Dependencies { get; init; } = [];
    internal bool IsListLayout { get; init; }
    internal bool WritesFile => Action is not "Keep personal file" and not "Unchanged";
}

public sealed record GngUpdatePlan(
    string PackageName, string Version, string DataFolder, string PackagePath,
    IReadOnlyList<GngUpdateFile> Files, IReadOnlyList<string> Warnings)
{
    internal string PackageHash { get; init; } = string.Empty;
    internal string Generation { get; init; } = string.Empty;
    internal IReadOnlyList<string> ProtectedPaths { get; init; } = [];
    internal IReadOnlyList<GngUpdateDependency> Dependencies { get; init; } = [];
    public bool PreserveListLayout { get; init; } = true;
    public string? ReferencePackageName { get; init; }
    internal string? ReferencePackagePath { get; init; }
    internal string? ReferencePackageHash { get; init; }
}

public sealed record GngUpdateResult(string BackupFolder, int InstalledFileCount, IReadOnlyList<string> SkippedFiles)
{
    public string? CachedPackagePath { get; init; }
    public string? CompletePackagePath { get; init; }
    public string? PreviousCompletePackagePath { get; init; }
}
