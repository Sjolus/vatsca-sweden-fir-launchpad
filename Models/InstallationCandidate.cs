namespace VatscaUpdateChecker.Models;

/// <summary>A read-only discovery suggestion. It is imported into settings only after user selection.</summary>
public sealed record InstallationCandidate(AtcRemovalApp Id, string AppName, string ExecutablePath,
    string? DataPath, string Scope, string SupportSummary, bool CanManage);
