namespace VatscaUpdateChecker.Models;

/// <summary>A snapshot of the user's explicit choice for one reviewed APP/ACC profile.</summary>
public sealed record SaamanProfilePolicy(string ProfilePath, string DllPath, bool Enabled)
{
    internal string? StateRoot { get; init; }
    internal SaamanProfileStateDependency? StateDependency { get; init; }
    internal string RestoreStateFingerprint { get; init; } = string.Empty;
    internal SaamanMaestroState? MaestroState { get; init; }
    internal bool RestoreMaestro { get; init; }
    internal byte[]? StateAfterBytes { get; init; }
}

internal sealed record SaamanProfileStateDependency(string Path, string? Hash, long Length);
internal sealed record SaamanPluginSetting(string Suffix, string Value);
internal sealed record SaamanMaestroGroup(string Path, int OriginalIndex, SaamanPluginSetting[] Entries);
internal sealed record SaamanMaestroState(int Format, string ProfilePath, bool Active, string[] OriginalOrder, SaamanMaestroGroup[] Maestro);
