namespace VatscaUpdateChecker.Models;

public enum AtcRemovalApp { EuroScope, Gng, Vacs, Vatis, TrackAudio, VatEfs, Vatiris }
public enum AtcRemovalVendorKind { Nsis, Velopack, Msi }

/// <summary>A reviewed, recognized vendor identity. Never contains an arbitrary registry command.</summary>
public sealed record AtcRemovalVendorSpec(
    AtcRemovalVendorKind Kind,
    SoftwareInstallation? Installation,
    string UninstallerPath,
    string UninstallerSha256,
    bool RemovesData,
    string? MsiProductCode = null,
    string? MsiRootPath = null,
    string? MsiScope = null,
    string? MsiFootprintFingerprint = null);

/// <summary>Paths are individual files or narrow directories, not permission to remove their parents.</summary>
public sealed record AtcRemovalTarget(
    AtcRemovalApp Id,
    string Name,
    string Description,
    IReadOnlyList<string> ProgramRoots,
    IReadOnlyList<string> DataRoots,
    bool CanRemoveApplication,
    bool CanRemoveData,
    string? Reason,
    AtcRemovalVendorSpec? VendorSpec,
    IReadOnlyList<string> Warnings);
