using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public sealed record SaamanProfileChange(string DataFolder, SaamanProfilePolicy Policy, bool HasChanges)
{
    internal byte[] BeforeBytes { get; init; } = [];
    internal byte[] AfterBytes { get; init; } = [];
    internal string BeforeHash { get; init; } = "";
    internal string? DllHash { get; init; }
    internal IReadOnlyList<SaamanProfileStateDependency> MaestroDependencies { get; init; } = [];
    internal bool ProfileMissing { get; init; }
    public string Summary => ProfileMissing
        ? "The selected APP/ACC profile is missing. Save SAAMAN as disabled without changing any profile files. Renamed or moved profiles remain unchanged."
        : Policy.Enabled
        ? "Put SAAMAN first in this APP/ACC profile and remove MAESTRO plugin entries. Other plugins keep their order and permissions."
        : Policy.RestoreMaestro && Policy.MaestroState?.Maestro.Length > 0
        ? "Remove SAAMAN and restore the original MAESTRO entries, position and display permissions. Other current profile changes are kept."
        : !Policy.RestoreMaestro && Policy.MaestroState?.Maestro.Length > 0
        ? "Keep SAAMAN disabled and preserve the current MAESTRO configuration. Other current profile settings are kept."
        : "Remove SAAMAN plugin entries. No original MAESTRO entry was replaced, so no MAESTRO path will be invented. Other current profile settings are kept.";
}

/// <summary>
/// One profile policy shared by Controller profile and the reviewed GNG merge.
/// Callers own the maintenance lease. No credentials are read here.
/// </summary>
public static partial class SaamanProfileService
{
    public const string DefaultProfileName = "ESAA APP ACC.prf";
    private const string DllName = "SAAMAN.dll";
    private const int MaxProfileBytes = 4 * 1024 * 1024;
    private static readonly Regex SlotKey = new(@"^Plugin(\d+)(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DisplayKey = new(@"^Display\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    private static readonly Encoding ProfileEncoding = CreateEncoding();
    private sealed record Plugin(string Path, List<(string Suffix, string Value)> Entries);
    internal sealed record OperationContext(string RecoveryRoot, Action RequireClosed);

    public static SaamanProfilePolicy? Capture(AppSettings settings, string dataFolder)
    {
        if (!settings.SaamanProfileManaged && !settings.PatchSaaman) return null;
        var profile = string.IsNullOrWhiteSpace(settings.SaamanProfilePath)
            ? Path.Combine(dataFolder, DefaultProfileName) : settings.SaamanProfilePath;
        return ValidatePolicy(new(profile, settings.SaamanDllPath, settings.PatchSaaman), dataFolder);
    }

    public static SaamanProfilePolicy ValidatePolicy(SaamanProfilePolicy policy, string dataFolder)
    {
        if (string.IsNullOrWhiteSpace(dataFolder))
            throw new InvalidOperationException("Choose a EuroScope data folder before configuring SAAMAN.");
        var root = SaamanFileSafety.Canonical(dataFolder).TrimEnd(Path.DirectorySeparatorChar);
        RejectReparse(root);
        var profile = SaamanFileSafety.Canonical(Path.IsPathFullyQualified(policy.ProfilePath)
            ? policy.ProfilePath : Path.Combine(root, policy.ProfilePath));
        RejectReparse(profile);
        if (!profile.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(profile).Equals(".prf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected SAAMAN APP/ACC profile must be a .prf file inside the configured EuroScope data folder.");
        var dll = policy.DllPath;
        if (policy.Enabled)
        {
            if (string.IsNullOrWhiteSpace(dll) || !Path.IsPathFullyQualified(dll))
                throw new InvalidOperationException("Choose the installed SAAMAN.dll in Setup → Applications before enabling it.");
            dll = SaamanFileSafety.Canonical(dll);
            RejectReparse(dll);
            if (!Names.Equals(Path.GetFileName(dll), DllName))
                throw new InvalidOperationException("The selected plugin must be named SAAMAN.dll.");
            try { _ = ProfileEncoding.GetByteCount(dll); }
            catch (EncoderFallbackException)
            {
                throw new InvalidOperationException("The SAAMAN folder name contains characters that EuroScope's Windows-1252 profiles cannot store. Choose a compatible folder.");
            }
        }
        return policy with { ProfilePath = profile, DllPath = dll };
    }

    public static SaamanProfileChange BuildPlan(SaamanProfilePolicy policy, string dataFolder)
    {
        policy = ValidatePolicy(policy, dataFolder);
        if (!policy.Enabled && !File.Exists(policy.ProfilePath))
        {
            if (Directory.Exists(policy.ProfilePath)) throw new IOException("A folder occupies the selected APP/ACC profile path.");
            return new(Path.GetFullPath(dataFolder), policy, false) { ProfileMissing = true };
        }
        var before = ReadProfile(policy.ProfilePath);
        var dllHash = policy.Enabled ? ValidateDll(policy.DllPath) : null;
        var (text, encoding, bom) = Decode(before);
        policy = PreparePolicy(policy, text, dataFolder);
        var maestroDependencies = GetMaestroDllPaths(policy, dataFolder).Select(SnapshotMaestroDll).ToArray();
        var transformed = Transform(text, policy);
        var after = text == transformed ? before : bom.Concat(encoding.GetBytes(transformed)).ToArray();
        return new(Path.GetFullPath(dataFolder), policy, !before.SequenceEqual(after))
        { BeforeBytes = before, AfterBytes = after, BeforeHash = Hash(before), DllHash = dllHash, MaestroDependencies = maestroDependencies };
    }

    /// <summary>
    /// Retarget an enabled profile without disabling SAAMAN or replacing the original
    /// MAESTRO baseline. The previous DLL may have been moved and need not still exist.
    /// </summary>
    public static SaamanProfileChange BuildRelocationPlan(SaamanProfilePolicy current, string selectedDll, string dataFolder)
    {
        if (!current.Enabled) throw new InvalidOperationException("SAAMAN is not enabled in this profile.");
        current = ValidatePolicy(current, dataFolder);
        var plan = BuildPlan(new(current.ProfilePath, selectedDll, true) { StateRoot = current.StateRoot }, dataFolder);
        var (_, _, plugins) = ParseProfile(Decode(plan.BeforeBytes).Text);
        var active = plugins.Where(plugin => IsPlugin(plugin.Path, DllName)).ToArray();
        if (active.Length == 0 || active.Any(plugin => !Names.Equals(
                ResolvePluginPath(plugin.Path, dataFolder), current.DllPath)))
            throw new InvalidOperationException("This profile no longer uses the saved SAAMAN location. Review its plugin entries before changing the DLL location.");
        if (plan.Policy.StateDependency?.Hash is null || plan.Policy.MaestroState is null)
            throw new InvalidOperationException("The original MAESTRO restoration record is missing. Restore its saved profile state before changing the SAAMAN location.");
        return plan;
    }

    public static string GeneratePreview(SaamanProfileChange plan)
    {
        var text = new StringBuilder();
        text.AppendLine("SAAMAN — " + plan.Policy.ProfilePath);
        text.AppendLine(plan.Summary);
        if (plan.ProfileMissing) return text.ToString();
        if (!plan.HasChanges) { text.AppendLine("  Already matches this choice; no SAAMAN changes."); return text.ToString(); }
        text.AppendLine("  A private recovery copy is made before the profile changes.");
        text.AppendLine("  Plugin entries after applying:");
        foreach (var line in Decode(plan.AfterBytes).Text.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t', 3);
            if (parts.Length == 3 && Names.Equals(parts[0], "Plugins"))
            {
                var key = SlotKey.Match(parts[1]);
                var value = key.Success && (key.Groups[2].Value.Length == 0 || DisplayKey.IsMatch(key.Groups[2].Value))
                    ? parts[2] : "[preserved]";
                text.AppendLine("  " + parts[0] + "\t" + parts[1] + "\t" + value);
            }
        }
        return text.ToString();
    }

    public static string? Apply(SaamanProfileChange plan) => Apply(plan, new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VatscaUpdateChecker", "Saaman", "ProfileRecovery"),
        EuroScopeProcessGuard.RequireClosed));

    internal static string? Apply(SaamanProfileChange plan, OperationContext context)
    {
        context.RequireClosed();
        var policy = ValidatePolicy(plan.Policy, plan.DataFolder);
        if (plan.ProfileMissing)
        {
            if (policy.Enabled || File.Exists(policy.ProfilePath) || Directory.Exists(policy.ProfilePath))
                throw new IOException("The selected APP/ACC profile appeared after review. Review it again before changing this choice.");
            return null;
        }
        RejectReparse(context.RecoveryRoot);
        var root = SaamanFileSafety.Canonical(context.RecoveryRoot);
        var data = SaamanFileSafety.Canonical(plan.DataFolder).TrimEnd(Path.DirectorySeparatorChar);
        if (root.Equals(data, StringComparison.OrdinalIgnoreCase) || root.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SAAMAN profile recovery must be outside the EuroScope data folder.");
        using var profileParents = new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(policy.ProfilePath)!, false);
        using var dllParents = policy.Enabled ? new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(policy.DllPath)!, false) : null;
        using var dll = policy.Enabled ? SaamanFileSafety.OpenRead(policy.DllPath) : null;
        if (dll is not null && Hash(dll) != plan.DllHash)
            throw new IOException("SAAMAN.dll changed after preview. Review the profile again.");
        // Keep the same file handle from comparison through the write. Its sharing mode
        // prevents another writer or a rename between the reviewed read and mutation.
        using var profile = SaamanFileSafety.OpenReadWrite(policy.ProfilePath);
        if (Hash(profile) != plan.BeforeHash)
            throw new IOException("The APP/ACC profile changed after preview. Review it again.");
        using var maestroFiles = GuardMaestroDlls(plan.MaestroDependencies);
        using var state = PersistRestoreState(policy);
        if (!plan.HasChanges) { state.Complete(); return null; }
        var backup = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        using var recoveryParents = new SaamanFileSafety.DirectoryGuard(backup, true);
        var original = Path.Combine(backup, Path.GetFileName(policy.ProfilePath));
        using var recovery = new FileStream(original, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        recovery.Write(plan.BeforeBytes); recovery.Flush(true); recovery.Position = 0;
        if (Hash(recovery) != plan.BeforeHash)
            throw new IOException("The SAAMAN profile recovery copy could not be verified; the profile was not changed.");
        context.RequireClosed();
        try
        {
            profile.Position = 0;
            profile.Write(plan.AfterBytes);
            profile.SetLength(plan.AfterBytes.Length);
            profile.Flush(true);
            profile.Position = 0;
            if (Hash(profile) != Hash(plan.AfterBytes)) throw new IOException("The written SAAMAN profile did not verify.");
            state.Complete();
        }
        catch
        {
            try
            {
                profile.Position = 0; profile.Write(plan.BeforeBytes);
                profile.SetLength(plan.BeforeBytes.Length); profile.Flush(true);
            }
            catch { throw new IOException("The APP/ACC profile could not be restored automatically. Recover its original copy from: " + backup); }
            throw;
        }
        return backup;
    }

    // This transformation performs no I/O. Every numbered suffix stays attached to
    // its plugin when groups move; unrelated preferences and text stay in place.
    public static string Transform(string text, SaamanProfilePolicy policy) => TransformCore(text, policy);

    // Once disabled, the user's current MAESTRO configuration supersedes the old
    // replacement snapshot. A package must not reset its order or permissions.
    internal static string TransformMerged(string localText, string mergedText, SaamanProfilePolicy policy)
    {
        if (!policy.Enabled && !policy.RestoreMaestro && !string.IsNullOrEmpty(localText))
        {
            var (_, _, current) = ParseProfile(localText);
            policy = policy with { RestoreMaestro = true, MaestroState = CaptureMaestro(policy.ProfilePath, current) };
        }
        return TransformCore(mergedText, policy);
    }

    private static string TransformCore(string text, SaamanProfilePolicy policy)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var (lines, slotLines, plugins) = ParseProfile(text);
        var saaman = plugins.Where(p => IsPlugin(p.Path, DllName)).ToList();
        var maestro = plugins.Where(p => IsPlugin(p.Path, "MAESTRO.dll")).ToList();
        if (!policy.Enabled && saaman.Count == 0 && !policy.RestoreMaestro) return text;
        var kept = plugins.Where(p => !IsPlugin(p.Path, DllName) && (!(policy.Enabled || policy.RestoreMaestro) || !IsPlugin(p.Path, "MAESTRO.dll"))).ToList();
        if (policy.Enabled)
        {
            var ownEntries = new List<(string Suffix, string Value)>();
            foreach (var item in saaman.SelectMany(p => p.Entries).Where(e => !DisplayKey.IsMatch(e.Suffix)))
            {
                var matches = ownEntries.Where(e => Names.Equals(e.Suffix, item.Suffix)).ToList();
                if (matches.Any(e => e.Value != item.Value))
                    throw new InvalidOperationException("Duplicate SAAMAN entries have conflicting plugin settings. Review the APP/ACC profile first.");
                if (matches.Count == 0) ownEntries.Add(item);
            }
            var displays = saaman.Concat(maestro).SelectMany(p => p.Entries).Where(e => DisplayKey.IsMatch(e.Suffix))
                .Select(e => e.Value).Distinct(StringComparer.Ordinal).ToList();
            if (displays.Count == 0) displays.Add("Standard ES radar screen");
            for (var i = 0; i < displays.Count; i++) ownEntries.Add(("Display" + i, displays[i]));
            kept.Insert(0, new(policy.DllPath, ownEntries));
        }
        else if (policy.RestoreMaestro && policy.MaestroState is { } saved)
            RestoreMaestroGroups(kept, saved);
        var replacement = new List<string>();
        for (var i = 0; i < kept.Count; i++)
        {
            replacement.Add($"Plugins\tPlugin{i}\t{kept[i].Path}");
            replacement.AddRange(kept[i].Entries.Select(e => $"Plugins\tPlugin{i}{e.Suffix}\t{e.Value}"));
        }
        var insert = slotLines.Count == 0 ? (lines.Count > 0 && lines[^1] == "" ? lines.Count - 1 : lines.Count) : slotLines.Min();
        var output = new List<string>();
        for (var i = 0; i <= lines.Count; i++)
        {
            if (i == insert) output.AddRange(replacement);
            if (i < lines.Count && !slotLines.Contains(i)) output.Add(lines[i]);
        }
        return string.Join(newline, output);
    }

    private static (List<string> Lines, HashSet<int> SlotLines, List<Plugin> Plugins) ParseProfile(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var groups = new SortedDictionary<int, List<(string Suffix, string Value)>>();
        var slotLines = new HashSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var parts = lines[i].Split('\t', 3);
            if (parts.Length != 3 || !Names.Equals(parts[0], "Plugins")) continue;
            var key = SlotKey.Match(parts[1]);
            if (!key.Success) continue;
            if (!int.TryParse(key.Groups[1].Value, out var index) || index > 50000)
                throw new InvalidOperationException("The APP/ACC profile contains an invalid plugin slot.");
            if (!groups.TryGetValue(index, out var entries)) groups[index] = entries = [];
            entries.Add((key.Groups[2].Value, parts[2]));
            slotLines.Add(i);
        }
        var plugins = new List<Plugin>();
        foreach (var entries in groups.Values)
        {
            var main = entries.Where(e => e.Suffix.Length == 0).ToList();
            if (main.Count != 1 || entries.GroupBy(e => e.Suffix, Names).Any(g => g.Count() > 1))
                throw new InvalidOperationException("The APP/ACC profile has duplicate or orphaned plugin entries. Correct these before applying SAAMAN.");
            plugins.Add(new(main[0].Value, entries.Where(e => e.Suffix.Length > 0).ToList()));
        }
        return (lines, slotLines, plugins);
    }

    internal static string ValidateDll(string path)
    {
        RejectReparse(path);
        if (!File.Exists(path)) throw new FileNotFoundException("SAAMAN.dll is missing. Install or select it in Setup → Applications before enabling it.", path);
        using var parents = new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(path)!, false);
        using var file = SaamanFileSafety.OpenRead(path);
        if (file.Length == 0 || file.Length > 64 * 1024 * 1024)
            throw new InvalidOperationException("The selected SAAMAN.dll is empty or exceeds the supported size.");
        return Hash(file);
    }

    private static byte[] ReadProfile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The selected APP/ACC profile is missing. Check the data folder in Setup → Swedish GNG.", path);
        using var parents = new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(path)!, false);
        using var file = SaamanFileSafety.OpenRead(path);
        if (file.Length > MaxProfileBytes) throw new IOException("The APP/ACC profile is too large to modify safely.");
        using var bytes = new MemoryStream(); file.CopyTo(bytes); return bytes.ToArray();
    }

    private static bool IsPlugin(string path, string name) =>
        Names.Equals(Path.GetFileName(path.Trim().Trim('"').Replace('/', '\\')), name);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
    private static (string Text, Encoding Encoding, byte[] Bom) Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), ProfileEncoding, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var encoding = reader.CurrentEncoding;
        var preamble = encoding.GetPreamble();
        return (text, encoding, preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble : []);
    }
    private static void RejectReparse(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are not supported for SAAMAN profile changes.");
    }
}
