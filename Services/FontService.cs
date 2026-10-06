using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace VatscaUpdateChecker.Services;

public enum FontsState { Unknown, AllOk, NeedsAction }

public sealed record FontEntry(
    string FileName,
    string? SourcePath,
    string? InstalledPath,
    double? SourceVersion,
    double? InstalledVersion,
    bool IsUpToDate);

public sealed record FontsCheckResult(
    FontsState State,
    IReadOnlyList<FontEntry> Entries,
    string Tooltip);

/// <summary>
/// Verifies that the TrueType fonts bundled with the GNG Pack are installed at the OS level
/// and at least as new as the source. Identification is by exact filename (case-insensitive)
/// — stray "FOO (1).TTF" Windows duplicate artifacts are ignored.
///
/// Version comparison uses WPF's GlyphTypeface.Version, interpreted from the font's NAME table.
/// Files with matching reported versions can differ in metadata or timestamps.
/// </summary>
public static class FontService
{
    // Fonts shipped inside the GNG Pack at <EuroscopeDataPath>\ESAA\<filename>.
    private static readonly string[] RequiredFonts = { "EuroScope.ttf", "SMR ESGG.ttf" };
    private const string EsaaSubdir = "ESAA";

    // Small numeric comparison tolerance for the version reported by WPF.
    private const double VersionTolerance = 0.0001;

    private static readonly string SystemFontsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static readonly string UserFontsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Fonts");

    public static FontsCheckResult Check(string euroscopeDataPath)
    {
        var entries = new List<FontEntry>(RequiredFonts.Length);

        foreach (var fontName in RequiredFonts)
        {
            var installed       = FindInstalledPath(fontName);
            var source          = !string.IsNullOrWhiteSpace(euroscopeDataPath)
                ? FindSourcePath(euroscopeDataPath, fontName)
                : null;
            var installedVer    = installed is not null ? ReadVersion(installed) : null;
            var sourceVer       = source    is not null ? ReadVersion(source)    : null;

            // "Up to date" rules:
            //   - not installed              → false (we want to install it)
            //   - source has no version      → true  (can't verify; don't false-flag)
            //   - installed has no version   → false (file unreadable / broken — reinstall)
            //   - both known                 → installed >= source (allow newer-installed)
            bool upToDate;
            if (installed is null)            upToDate = false;
            else if (sourceVer is null)       upToDate = true;
            else if (installedVer is null)    upToDate = false;
            else                              upToDate = installedVer.Value >= sourceVer.Value - VersionTolerance;

            entries.Add(new FontEntry(fontName, source, installed, sourceVer, installedVer, upToDate));
        }

        var state = AggregateState(euroscopeDataPath, entries);
        var tooltip = BuildTooltip(entries);
        return new FontsCheckResult(state, entries, tooltip);
    }

    public static void InstallMissing(FontsCheckResult result)
    {
        foreach (var entry in result.Entries)
        {
            if (entry.SourcePath is null) continue;
            if (entry.IsUpToDate) continue;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = entry.SourcePath,
                    UseShellExecute = true,   // routes .ttf to fontview.exe via shell association
                });
                Logger.Log("FONTS", $"Opened fontview for {entry.FileName} ({entry.SourcePath})");
            }
            catch (Exception ex)
            {
                Logger.Log("FONTS", $"Failed to open fontview for {entry.SourcePath}: {ex.Message}");
            }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string? FindInstalledPath(string fontName)
    {
        // Exact filename match. System dir wins (authoritative install) over per-user.
        // Stray "FOO (1).TTF" Windows artifacts are not exact matches and so don't qualify.
        var sys = Path.Combine(SystemFontsDir, fontName);
        if (File.Exists(sys)) return sys;

        var user = Path.Combine(UserFontsDir, fontName);
        if (File.Exists(user)) return user;

        return null;
    }

    private static string? FindSourcePath(string euroscopeDataPath, string fontName)
    {
        if (!Directory.Exists(euroscopeDataPath)) return null;
        var path = Path.Combine(euroscopeDataPath, EsaaSubdir, fontName);
        return File.Exists(path) ? path : null;
    }

    private static double? ReadVersion(string ttfPath)
    {
        try
        {
            var glyph = new GlyphTypeface(new Uri(ttfPath, UriKind.Absolute));
            return glyph.Version;   // Font version interpreted from the NAME table.
        }
        catch
        {
            return null;
        }
    }

    private static FontsState AggregateState(string euroscopeDataPath, IReadOnlyList<FontEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(euroscopeDataPath) || entries.All(e => e.SourcePath is null))
            return FontsState.Unknown;

        return entries.All(e => e.IsUpToDate)
            ? FontsState.AllOk
            : FontsState.NeedsAction;
    }

    private static string BuildTooltip(IReadOnlyList<FontEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            if (sb.Length > 0) sb.AppendLine();

            string Vi() => e.InstalledVersion is { } v ? $"v{v:0.00}" : "v?";
            string Vs() => e.SourceVersion    is { } v ? $"v{v:0.00}" : "v?";

            if (e.InstalledPath is null)
            {
                sb.Append(e.FileName).Append(" — missing");
                if (e.SourcePath is not null)
                    sb.Append(" — will install ").Append(Vs()).Append(" from ").Append(e.SourcePath);
                else
                    sb.Append(" — source not found in ESAA folder");
            }
            else if (e.SourcePath is null)
            {
                sb.Append(e.FileName).Append(" — installed ").Append(Vi()).Append(" (no source to verify)");
            }
            else if (e.IsUpToDate)
            {
                if (e.InstalledVersion is null && e.SourceVersion is null)
                    sb.Append(e.FileName).Append(" — installed (no version metadata)");
                else if (e.SourceVersion is null)
                    sb.Append(e.FileName).Append(" — installed ").Append(Vi()).Append(" (source version unreadable)");
                else if (e.InstalledVersion.HasValue && e.SourceVersion.HasValue
                         && e.InstalledVersion.Value > e.SourceVersion.Value + VersionTolerance)
                    sb.Append(e.FileName).Append(" — installed ").Append(Vi())
                        .Append(" (newer than GNG Pack ").Append(Vs()).Append(")");
                else
                    sb.Append(e.FileName).Append(" — installed ").Append(Vi())
                        .Append(" (matches GNG Pack ").Append(Vs()).Append(")");
            }
            else
            {
                sb.Append(e.FileName).Append(" — installed ").Append(Vi())
                    .Append(", GNG Pack ships ").Append(Vs())
                    .Append(" — will reinstall from ").Append(e.SourcePath);
            }
        }
        return sb.ToString();
    }
}
