using System.Collections.ObjectModel;

namespace VatscaUpdateChecker.Models;

public sealed record RemovalFileSnapshot(string RootPath, string FullPath, string RelativePath,
    long Length, string Sha256)
{
    internal RemovalFileIdentity Identity { get; init; }
    internal uint Attributes { get; init; }
}

public sealed record RemovalDirectorySnapshot(string RootPath, string FullPath, string RelativePath)
{
    internal RemovalFileIdentity Identity { get; init; }
    internal uint Attributes { get; init; }
}

/// <summary>An immutable inventory, including root and empty directories, created only by Preview.</summary>
public sealed class RemovalFilePlan
{
    internal RemovalFilePlan(IEnumerable<string> roots, IEnumerable<RemovalFileSnapshot> files,
        IEnumerable<RemovalDirectorySnapshot> directories)
    {
        Roots = new ReadOnlyCollection<string>(roots.ToArray());
        Files = new ReadOnlyCollection<RemovalFileSnapshot>(files.ToArray());
        Directories = new ReadOnlyCollection<RemovalDirectorySnapshot>(directories.ToArray());
        TotalBytes = Files.Sum(file => file.Length);
    }

    public IReadOnlyList<string> Roots { get; }
    public IReadOnlyList<RemovalFileSnapshot> Files { get; }
    public IReadOnlyList<RemovalDirectorySnapshot> Directories { get; }
    public long TotalBytes { get; }
}

public sealed record RemovalFileError(string Path, string Message);

public sealed record RemovalDeleteResult(int DeletedFiles, int DeletedDirectories,
    IReadOnlyList<RemovalFileError> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

internal readonly record struct RemovalFileIdentity(uint Volume, ulong FileId, long CreationTime);
