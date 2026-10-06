namespace VatscaUpdateChecker.Models;

public enum ConfiguredPathKind
{
    EuroScopeExecutable,
    EuroScopeDataFolder,
    TrackAudioExecutable,
    VacsExecutable,
    VatisExecutable,
    VatEfsFolder
}

public enum PathValidationStatus { NotSelected, Verified, Warning, Invalid }

/// <summary>Read-only path identity guidance, not installation support or readiness to control.</summary>
public sealed record ConfiguredPathValidation(
    ConfiguredPathKind Kind, string Path, PathValidationStatus Status, string Message);
