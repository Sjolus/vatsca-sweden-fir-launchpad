using System.IO;
using System.Text;
using System.Text.Json;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public static partial class SaamanProfileService
{
    private const int MaximumStateBytes = 128 * 1024;
    internal const int MaximumProtectedStateEntries = 256;
    internal static string DefaultStateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VatscaUpdateChecker", "Saaman", "ProfileState");

    /// <summary>Read-only capture of the MAESTRO groups this enable operation replaces.</summary>
    internal static SaamanProfilePolicy PreparePolicy(SaamanProfilePolicy policy, string sourceText, string dataFolder)
    {
        policy = ValidatePolicy(policy, dataFolder);
        if (policy.StateDependency is not null)
        {
            ValidateStateDependency(policy);
            return policy;
        }
        var statePath = StatePath(policy.ProfilePath, policy.StateRoot, dataFolder);
        var (old, before, beforeHash) = ReadState(statePath, policy.ProfilePath);
        var (_, _, plugins) = ParseProfile(sourceText);
        var hasSaaman = plugins.Any(p => IsPlugin(p.Path, DllName));
        var hasMaestro = plugins.Any(p => IsPlugin(p.Path, "MAESTRO.dll"));
        if (!policy.Enabled && hasSaaman && old is null)
            throw new InvalidOperationException("The original MAESTRO restoration record is missing. SAAMAN was left unchanged. Restore its saved profile state or review the profile manually before disabling it.");
        SaamanMaestroState? state = old;
        bool restore = false;
        if (policy.Enabled)
        {
            // A failed/rolled-back enable can leave its durable baseline behind.
            // Original MAESTRO with no SAAMAN proves a fresh capture is appropriate.
            bool reuse = old is not null && (old.Active || hasSaaman) && (hasSaaman || !hasMaestro);
            state = reuse ? old! with { Active = true } : CaptureMaestro(policy.ProfilePath, plugins);
        }
        else if (old is not null)
        {
            // A GNG restore can bring back an enabled profile after a completed
            // disable. Its retained snapshot still describes that replacement.
            restore = old.Active || hasSaaman;
            state = old with { Active = false };
        }
        var after = state is null ? null : SerializeState(state);
        var fingerprint = Hash(Encoding.UTF8.GetBytes(
            statePath + "\n" + beforeHash + "\n" + (after is null ? "" : Hash(after)) + "\n" + restore));
        return policy with
        {
            StateDependency = new(statePath, beforeHash, before?.LongLength ?? 0),
            MaestroState = state,
            RestoreMaestro = restore,
            StateAfterBytes = after,
            RestoreStateFingerprint = fingerprint,
        };
    }

    internal static void ValidateStateDependency(SaamanProfilePolicy policy)
    {
        if (policy.StateDependency is not { } dependency) return;
        var (_, bytes, hash) = ReadState(dependency.Path, policy.ProfilePath);
        if (!Names.Equals(hash, dependency.Hash) || (bytes?.LongLength ?? 0) != dependency.Length)
            throw new IOException("The saved MAESTRO restoration state changed after preview. Review the profile again.");
    }

    internal static IReadOnlyList<string> GetMaestroDllPaths(SaamanProfilePolicy policy, string dataFolder)
    {
        if (!policy.RestoreMaestro || policy.MaestroState is null) return [];
        return policy.MaestroState.Maestro.Select(p => ResolvePluginPath(p.Path, dataFolder))
            .Distinct(Names).ToArray();
    }

    /// <summary>State and dormant MAESTRO files remain protected while SAAMAN replaces them.</summary>
    internal static IReadOnlyList<string> GetProtectedPaths(AppSettings settings, string? stateRoot = null)
    {
        if (string.IsNullOrWhiteSpace(settings.EuroscopeDataPath)) return [];
        if (!Directory.Exists(stateRoot ?? DefaultStateRoot)) return [];
        // Protection is read-only and independent of the current profile choice:
        // preferences can be reset while a custom profile still contains SAAMAN.
        var data = PreservationPath.Resolve(settings.EuroscopeDataPath);
        var root = SaamanFileSafety.Canonical(stateRoot ?? DefaultStateRoot);
        if (SaamanFileSafety.Overlaps(root, data))
            throw new InvalidOperationException("MAESTRO restoration state must be stored separately from EuroScope data.");
        using var parents = new SaamanFileSafety.DirectoryGuard(root, false);
        var entries = Directory.EnumerateFileSystemEntries(root).Take(MaximumProtectedStateEntries + 1).ToArray();
        if (entries.Length > MaximumProtectedStateEntries)
            throw new IOException("There are too many saved SAAMAN profile-state entries to check safely. Review the state folder before removing files.");
        var result = new HashSet<string>(Names);
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            var mainName = name.EndsWith(".json.previous", StringComparison.OrdinalIgnoreCase) ? name[..^9] : name;
            if (mainName.Length != 69 || !mainName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !mainName[..64].All(Uri.IsHexDigit)) continue;
            var (state, _, _) = ReadState(entry, null);
            if (state is null)
                throw new IOException("A saved SAAMAN profile state disappeared while checking protected files. Review removal again.");
            var savedProfile = PreservationPath.Normalize(state.ProfilePath);
            var expectedName = Hash(Encoding.UTF8.GetBytes(savedProfile.ToUpperInvariant())) + ".json";
            if (!Names.Equals(mainName, expectedName))
                throw new InvalidDataException("A saved MAESTRO restoration-state filename does not match its profile.");
            var profile = PreservationPath.Resolve(savedProfile);
            if (!profile.StartsWith(data.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Names.Equals(Path.GetExtension(profile), ".prf"))
                throw new InvalidDataException("The saved MAESTRO restoration state does not name a EuroScope profile.");
            var mainPath = Path.Combine(root, mainName);
            result.Add(mainPath);
            result.Add(mainPath + ".previous");
            // GNG recovery can restore an enabled profile after its state became
            // inactive. Both retained versions remain protective, never authority
            // to modify a profile whose option is currently unmanaged.
            foreach (var group in state.Maestro)
            {
                var dll = ResolvePluginPath(group.Path, data);
                result.Add(dll);
                var parent = Path.GetDirectoryName(dll)!;
                if (Names.Equals(Path.GetFileName(parent), "MAESTRO")) result.Add(parent);
            }
        }
        return result.ToArray();
    }

    internal static SaamanRestoreStateLease PersistRestoreState(SaamanProfilePolicy policy) => new(policy);

    /// <summary>
    /// Holds the reviewed sidecar through profile mutation. Enabling saves the
    /// original plugin groups before changing files. Complete marks disabling only
    /// after restoration succeeds, so failed operations retain restoration data.
    /// </summary>
    internal sealed class SaamanRestoreStateLease : IDisposable
    {
        private readonly SaamanProfilePolicy _policy;
        private readonly SaamanFileSafety.DirectoryGuard? _parents;
        private readonly FileStream? _file;
        private FileStream? _previous;
        private readonly byte[]? _beforeBytes;
        private readonly string? _expectedAfterHash;
        private bool _unchanged;
        private bool _completed;

        internal SaamanRestoreStateLease(SaamanProfilePolicy policy)
        {
            _policy = policy;
            if (policy.StateDependency is not { } dependency)
                throw new InvalidOperationException("Review MAESTRO restoration state before applying SAAMAN.");
            if (policy.StateAfterBytes is null)
            {
                ValidateStateDependency(policy);
                return;
            }
            try
            {
                _parents = new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(dependency.Path)!, true);
                if (dependency.Hash is null)
                {
                    // CreateNew never overwrites a state file that appeared since review.
                    _file = new FileStream(dependency.Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                }
                else
                {
                    _file = SaamanFileSafety.OpenReadWrite(dependency.Path);
                    if (_file.Length != dependency.Length || !Names.Equals(Hash(_file), dependency.Hash))
                        throw new IOException("The saved MAESTRO restoration state changed after preview. Review the profile again.");
                    _file.Position = 0;
                    _beforeBytes = new byte[(int)_file.Length];
                    _file.ReadExactly(_beforeBytes);
                }
                _expectedAfterHash = Hash(policy.StateAfterBytes);
                _unchanged = Names.Equals(dependency.Hash, _expectedAfterHash);
                if (policy.Enabled) WriteAndVerify(policy.StateAfterBytes);
            }
            catch { Dispose(); throw; }
        }

        internal void Complete()
        {
            if (_completed) return;
            if (!_policy.Enabled && _policy.StateAfterBytes is { } after) WriteAndVerify(after);
            _completed = true;
        }

        private void WriteAndVerify(byte[] bytes)
        {
            if (_file is null) throw new InvalidOperationException("The MAESTRO restoration state is not locked.");
            if (_unchanged) return;
            if (_beforeBytes is not null && _previous is null)
            {
                // Keep one bounded, verified prior version before overwriting a
                // sidecar. A crash during this write leaves the current version
                // intact; a crash during the next write leaves this copy intact.
                var priorPath = _policy.StateDependency!.Path + ".previous";
                SaamanFileSafety.NoLinks(priorPath);
                if (Directory.Exists(priorPath))
                    throw new IOException("A folder occupies the previous MAESTRO restoration-state file.");
                _previous = File.Exists(priorPath)
                    ? SaamanFileSafety.OpenReadWrite(priorPath)
                    : new FileStream(priorPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                WriteStateBytes(_previous, _beforeBytes, Hash(_beforeBytes));
            }
            try { WriteStateBytes(_file, bytes, _expectedAfterHash!); }
            catch
            {
                if (_beforeBytes is not null)
                {
                    try { WriteStateBytes(_file, _beforeBytes, Hash(_beforeBytes)); }
                    catch { throw new IOException("The MAESTRO restoration state could not be restored automatically. Its verified previous copy is at: " + _policy.StateDependency!.Path + ".previous"); }
                }
                throw;
            }
            _unchanged = true;
        }
        private static void WriteStateBytes(FileStream file, byte[] bytes, string expectedHash)
        {
            file.Position = 0; file.Write(bytes); file.SetLength(bytes.Length); file.Flush(true);
            file.Position = 0;
            if (!Names.Equals(Hash(file), expectedHash))
                throw new IOException("The MAESTRO restoration state could not be verified. Keep its previous copy and review the profile again.");
        }
        public void Dispose() { _previous?.Dispose(); _file?.Dispose(); _parents?.Dispose(); }
    }

    private static SaamanMaestroState CaptureMaestro(string profile, List<Plugin> plugins)
    {
        var state = new SaamanMaestroState(1, profile, true, plugins.Select(p => p.Path).ToArray(),
            plugins.Select((p, i) => (Plugin: p, Index: i)).Where(p => IsPlugin(p.Plugin.Path, "MAESTRO.dll"))
                .Select(p => new SaamanMaestroGroup(p.Plugin.Path, p.Index,
                    p.Plugin.Entries.Select(e => new SaamanPluginSetting(e.Suffix, e.Value)).ToArray())).ToArray());
        ValidateState(state, profile);
        return state;
    }

    private static void RestoreMaestroGroups(List<Plugin> current, SaamanMaestroState state)
    {
        var restored = new Dictionary<int, Plugin>();
        foreach (var saved in state.Maestro.OrderBy(m => m.OriginalIndex))
        {
            int? before = null;
            foreach (var anchor in state.OriginalOrder.Skip(saved.OriginalIndex + 1).Where(p => !IsPlugin(p, "MAESTRO.dll")))
                if (FindAnchor(current, anchor) is { } found) { before = found; break; }
            int insertion;
            if (before is { } next) insertion = next;
            else
            {
                int? after = null;
                for (var i = saved.OriginalIndex - 1; i >= 0; i--)
                {
                    var anchor = state.OriginalOrder[i];
                    var found = restored.TryGetValue(i, out var previousGroup)
                        ? current.IndexOf(previousGroup)
                        : IsPlugin(anchor, "MAESTRO.dll") ? null : FindAnchor(current, anchor);
                    if (found is not null) { after = found; break; }
                }
                insertion = after is { } previous ? previous + 1 : Math.Min(saved.OriginalIndex, current.Count);
            }
            var plugin = new Plugin(saved.Path, saved.Entries.Select(e => (e.Suffix, e.Value)).ToList());
            current.Insert(insertion, plugin);
            restored.Add(saved.OriginalIndex, plugin);
        }
    }

    private static int? FindAnchor(List<Plugin> current, string path)
    {
        var exact = current.Select((p, i) => (p, i)).Where(x => Names.Equals(NormalizeAnchor(x.p.Path), NormalizeAnchor(path))).ToArray();
        if (exact.Length == 1) return exact[0].i;
        if (exact.Length > 1) throw new InvalidOperationException("MAESTRO's original plugin order is ambiguous. Resolve duplicate neighbouring plugins before disabling SAAMAN.");
        var sameName = current.Select((p, i) => (p, i)).Where(x => Names.Equals(
            Path.GetFileName(NormalizeAnchor(x.p.Path)), Path.GetFileName(NormalizeAnchor(path)))).ToArray();
        if (sameName.Length > 1) throw new InvalidOperationException("MAESTRO's original plugin order is ambiguous. Resolve duplicate neighbouring plugins before disabling SAAMAN.");
        return sameName.Length == 1 ? sameName[0].i : null;
    }
    private static string NormalizeAnchor(string path) => path.Trim().Trim('"').Replace('/', '\\').TrimStart('\\', '.');

    private static string StatePath(string profilePath, string? stateRoot, string dataFolder)
    {
        var root = SaamanFileSafety.Canonical(stateRoot ?? DefaultStateRoot);
        var data = SaamanFileSafety.Canonical(dataFolder);
        if (SaamanFileSafety.Overlaps(root, data))
            throw new InvalidOperationException("MAESTRO restoration state must be stored separately from EuroScope data.");
        var key = Hash(Encoding.UTF8.GetBytes(SaamanFileSafety.Canonical(profilePath).ToUpperInvariant()));
        return Path.Combine(root, key + ".json");
    }

    private static (SaamanMaestroState? State, byte[]? Bytes, string? Hash) ReadState(string path, string? profilePath)
    {
        path = SaamanFileSafety.Canonical(path);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new IOException("A folder occupies the MAESTRO restoration-state file.");
            return (null, null, null);
        }
        using var parents = new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(path)!, false);
        using var file = SaamanFileSafety.OpenRead(path);
        if (file.Length is <= 0 or > MaximumStateBytes) throw new InvalidDataException("The MAESTRO restoration-state file has an unsupported size.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        SaamanMaestroState? state;
        try { state = JsonSerializer.Deserialize<SaamanMaestroState>(bytes, new JsonSerializerOptions { MaxDepth = 12 }); }
        catch (JsonException) { throw new InvalidDataException("The MAESTRO restoration state is unreadable. Keep the existing recovery files and repair the saved state before applying."); }
        if (state is null) throw new InvalidDataException("The MAESTRO restoration state is empty.");
        if (string.IsNullOrWhiteSpace(state.ProfilePath) || state.ProfilePath.Length > 4096)
            throw new InvalidDataException("The saved MAESTRO restoration state has an invalid profile path.");
        ValidateState(state, profilePath ?? state.ProfilePath);
        return (state, bytes, Hash(bytes));
    }

    private static void ValidateState(SaamanMaestroState state, string profilePath)
    {
        static bool Text(string? value, int limit) => value is not null && value.Length <= limit &&
            !value.Contains('\r') && !value.Contains('\n') && !value.Contains('\0');
        if (state.Format != 1 || !Names.Equals(state.ProfilePath, profilePath) ||
            state.OriginalOrder is null || state.OriginalOrder.Length > 512 || state.Maestro is null || state.Maestro.Length > 64 ||
            state.OriginalOrder.Any(p => !Text(p, 4096)) ||
            state.Maestro.Any(m => m is null || !Text(m.Path, 4096) || !IsPlugin(m.Path, "MAESTRO.dll") ||
                m.OriginalIndex < 0 || m.OriginalIndex >= state.OriginalOrder.Length || state.OriginalOrder[m.OriginalIndex] != m.Path ||
                m.Entries is null || m.Entries.Length > 128 || m.Entries.Any(e => e is null || string.IsNullOrEmpty(e.Suffix) ||
                    !Text(e.Suffix, 64) || e.Suffix.Contains('\t') || !Text(e.Value, 4096)) ||
                m.Entries.GroupBy(e => e.Suffix, Names).Any(g => g.Count() > 1)) ||
            state.Maestro.GroupBy(m => m.OriginalIndex).Any(g => g.Count() > 1))
            throw new InvalidDataException("The saved MAESTRO restoration state is invalid or belongs to another profile.");
    }

    private static byte[] SerializeState(SaamanMaestroState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (bytes.Length > MaximumStateBytes) throw new InvalidDataException("MAESTRO restoration state exceeds the supported size.");
        return bytes;
    }

    private static string ResolvePluginPath(string path, string dataFolder)
    {
        path = path.Trim().Trim('"').Replace('/', '\\');
        // EuroScope package paths may have a single leading separator.
        if (path.StartsWith('\\') && !path.StartsWith(@"\\", StringComparison.Ordinal)) path = path.TrimStart('\\');
        return SaamanFileSafety.Canonical(Path.IsPathFullyQualified(path) ? path : Path.Combine(dataFolder, path));
    }

    private static SaamanProfileStateDependency SnapshotMaestroDll(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The original MAESTRO DLL is missing. Restore this file before disabling SAAMAN: " + path, path);
        using var parents = new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(path)!, false);
        using var file = SaamanFileSafety.OpenRead(path);
        if (file.Length is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("The original MAESTRO DLL is empty or too large.");
        return new(path, Hash(file), file.Length);
    }

    private static IDisposable GuardMaestroDlls(IReadOnlyList<SaamanProfileStateDependency> dependencies) => new MaestroFileLease(dependencies);
    private sealed class MaestroFileLease : IDisposable
    {
        private readonly List<IDisposable> _held = [];
        internal MaestroFileLease(IReadOnlyList<SaamanProfileStateDependency> dependencies)
        {
            try
            {
                foreach (var dependency in dependencies)
                {
                    _held.Add(new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(dependency.Path)!, false));
                    var file = SaamanFileSafety.OpenRead(dependency.Path); _held.Add(file);
                    if (file.Length != dependency.Length || !Names.Equals(Hash(file), dependency.Hash))
                        throw new IOException("The original MAESTRO DLL changed after preview. Review the profile again.");
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { for (var i = _held.Count - 1; i >= 0; i--) _held[i].Dispose(); _held.Clear(); }
    }
}
