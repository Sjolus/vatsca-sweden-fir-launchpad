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

public sealed record GngReviewPlugin(string FileName, string Location, IReadOnlyList<string> Profiles, bool SuppliedByPackage = false);

/// <summary>Read-only installation checks gathered while building the package plan.</summary>
public sealed record GngReviewContext
{
    public bool HasExistingFiles { get; init; }
    public bool HasVerifiedBaseline { get; init; }
    public bool ReinstallsGeneration { get; init; }
    public int CustomizedReplacementCount { get; init; }
    public int UnmatchedReplacementCount { get; init; }
    public int PreservedCustomPluginCount { get; init; }
    public IReadOnlyList<GngReviewPlugin> PreservedCustomPlugins { get; init; } = [];
    public int RdfOverrideCount { get; init; }
    public int UnverifiedDependencyCount { get; init; }
    public IReadOnlyList<GngReviewPlugin> UnverifiedDependencies { get; init; } = [];
    public IReadOnlyList<string> UnchangedProfiles { get; init; } = [];
    internal IReadOnlyList<string> ClassifiedWarnings { get; init; } = [];
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
    public bool IsTestingPackage { get; init; }
    public GngReviewContext? ReviewContext { get; init; }
    public SaamanProfilePolicy? SaamanPolicy { get; init; }
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
