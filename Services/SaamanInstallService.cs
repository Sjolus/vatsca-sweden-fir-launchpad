using System.IO;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Explicit DLL-only installation. SAAMAN's LocalAppData state and profiles are never changed here.</summary>
public sealed class SaamanInstallService
{
    private readonly SaamanReleaseService _releases;
    private readonly string _storage;
    private readonly Action _requireClosed;
    private readonly Func<string, SaamanDllInfo> _inspect;
    public string? LastRecoveryFolder { get; private set; }
    public SaamanInstallService() : this(new SaamanReleaseService(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VatscaUpdateChecker", "SAAMAN"),
        EuroScopeProcessGuard.RequireClosed, InspectDll) { }
    internal SaamanInstallService(SaamanReleaseService releases, string storage, Action requireClosed, Func<string, SaamanDllInfo> inspect)
    { _releases = releases; _storage = storage; _requireClosed = requireClosed; _inspect = inspect; }

    public static SaamanDllInfo InspectDll(string path)
    {
        path = SaamanFileSafety.Canonical(path);
        if (!Path.GetFileName(path).Equals("SAAMAN.dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select the existing plugin named SAAMAN.dll. Renamed DLLs cannot be adopted or managed safely.");
        using var parents = new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(path)!, false);
        using var stream = SaamanFileSafety.OpenRead(path);
        if (stream.Length <= 0 || stream.Length > SaamanReleaseService.MaximumDllSize)
            throw new InvalidDataException("The selected DLL exceeds the safe inspection limit.");
        using (var pe = new PEReader(stream, PEStreamOptions.LeaveOpen))
            if (pe.PEHeaders.CoffHeader.Machine != Machine.I386 ||
                !pe.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll) ||
                pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32 || pe.PEHeaders.CorHeader is not null)
                throw new InvalidDataException("SAAMAN must be a native x86 EuroScope plugin DLL.");
        var registration = ReportingPluginRegistrationReader.Read(path);
        if (registration?.Product != ReportingDllProduct.Saaman ||
            !SoftwareVersion.TryParse(registration.Version, out var version))
            throw new InvalidDataException("This DLL has no recognised SAAMAN plugin identity and version.");
        stream.Position = 0;
        return new(path, version.Value, stream.Length, SaamanFileSafety.Hash(stream));
    }

    public static string GetSuggestedPath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SaamanDllPath)) return settings.SaamanDllPath;
        if (string.IsNullOrWhiteSpace(settings.EuroscopeDataPath) || !Directory.Exists(settings.EuroscopeDataPath))
            throw new InvalidOperationException("Choose an existing EuroScope data folder before installing SAAMAN.");
        return Path.Combine(settings.EuroscopeDataPath, "ESAA", "Plugins", "SAAMAN", "SAAMAN.dll");
    }

    public SaamanInstallPlan Preview(AppSettings settings, SaamanRelease release)
    {
        string target = ValidateTarget(GetSuggestedPath(settings), settings, allowUnconfigured: false);
        var previous = File.Exists(target) ? _inspect(target) : null;
        if (previous is not null && SoftwareVersion.TryCompare(previous.Version, release.Version, out int order) && order > 0)
            throw new InvalidOperationException("The selected DLL is newer than the latest stable release. Managed downgrade is not offered.");
        var protectedPaths = ProtectedPaths(settings).ToArray();
        RequireSeparateStorage(target);
        if (FindRecovery(target) is { } recovery && IsPending(recovery))
            throw new InvalidOperationException("An earlier SAAMAN change is incomplete. Open Setup → Activity & recovery before installing again.");
        return new(target, release, previous, protectedPaths);
    }

    public SaamanDllInfo Adopt(string selectedPath, AppSettings settings)
    {
        LastRecoveryFolder = null;
        using var lease = Acquire();
        string target = ValidateTarget(selectedPath, settings, allowUnconfigured: true);
        RequireSeparateStorage(target);
        return _inspect(target);
    }

    public async Task<SaamanInstallResult> InstallAsync(SaamanInstallPlan plan, AppSettings settings,
        IProgress<SaamanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        LastRecoveryFolder = null;
        using var lease = Acquire();
        string target = ValidateTarget(plan.TargetPath, settings, allowUnconfigured: false);
        if (!target.Equals(SaamanFileSafety.Canonical(GetSuggestedPath(settings)), StringComparison.OrdinalIgnoreCase) ||
            !plan.ProtectedPaths.SequenceEqual(ProtectedPaths(settings), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected paths changed while preparing SAAMAN. Try again from Setup → Applications.");
        RequireSeparateStorage(target);
        if (FindRecovery(target) is { } pending && IsPending(pending))
        {
            LastRecoveryFolder = Path.GetDirectoryName(pending.ReceiptPath);
            throw new InvalidOperationException("An earlier SAAMAN change is incomplete. Open Setup → Activity & recovery before installing again.");
        }
        _requireClosed();
        RequireOriginal(target, plan.Previous);
        string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        string transaction = SaamanFileSafety.Canonical(Path.Combine(_storage, "Recovery", id));
        using var storageParents = new SaamanFileSafety.DirectoryGuard(transaction, true);
        progress?.Report(new("Downloading SAAMAN and licence notices…"));
        foreach (var asset in plan.Release.Assets)
            await _releases.DownloadAsync(asset, Path.Combine(transaction, asset.Name), progress, cancellationToken).ConfigureAwait(false);
        string downloaded = Path.Combine(transaction, "SAAMAN.dll");
        var identity = _inspect(downloaded);
        if (!identity.Version.Equals(plan.Release.Version, StringComparison.Ordinal) ||
            !identity.Sha256.Equals(plan.Release.Dll.Sha256, StringComparison.OrdinalIgnoreCase) || identity.Size != plan.Release.Dll.Size)
            throw new InvalidDataException("The DLL's plugin version and identity do not match the reviewed release.");
        ValidateChecksums(transaction, plan.Release);
        cancellationToken.ThrowIfCancellationRequested();
        _requireClosed();
        RequireOriginal(target, plan.Previous);
        using var targetParents = new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(target)!, true);
        string staging = Path.Combine(Path.GetDirectoryName(target)!, ".saaman-" + id + ".staged");
        SaamanFileSafety.Copy(downloaded, staging, identity.Size, identity.Sha256);
        string backup = Path.Combine(transaction, "previous.dll");
        if (plan.Previous is { } old) SaamanFileSafety.Copy(target, backup, old.Size, old.Sha256);
        // Keep the verified recovery bytes immutable until replacement has completed.
        using var backupHandle = plan.Previous is null ? null : SaamanFileSafety.OpenRead(backup);
        if (backupHandle is not null &&
            (backupHandle.Length != plan.Previous!.Size || SaamanFileSafety.Hash(backupHandle) != plan.Previous.Sha256))
            throw new IOException("The SAAMAN recovery copy could not be verified before replacement.");
        string receipt = Path.Combine(transaction, "receipt.json");
        var recovery = new SaamanRecovery(receipt, target, identity.Version, plan.Previous?.Version,
            identity.Sha256, plan.Previous?.Sha256, plan.Previous?.Size ?? 0, backup);
        WriteNew(receipt, JsonSerializer.Serialize(recovery));
        LastRecoveryFolder = transaction;
        // From here the durable receipt permits recovery after any failure or interruption.
        // Cancellation is preparation-only so replacement cannot be interrupted by the dialog.
        _requireClosed();
        progress?.Report(new("Applying the verified DLL; keep EuroScope closed…"));
        if (plan.Previous is { } previous) SaamanFileSafety.Delete(target, previous.Size, previous.Sha256);
        SaamanFileSafety.Move(staging, target, identity.Size, identity.Sha256);
        var installed = _inspect(target);
        if (installed.Sha256 != identity.Sha256) throw new IOException("SAAMAN verification failed; the recovery receipt was retained.");
        WriteNew(Path.Combine(transaction, "completed"), identity.Sha256);
        return new(target, identity.Version, receipt);
    }

    public SaamanRecovery? FindRecovery(string targetPath)
    {
        string target = SaamanFileSafety.Canonical(targetPath);
        string root = SaamanFileSafety.Canonical(Path.Combine(_storage, "Recovery"));
        if (!Directory.Exists(root)) return null;
        SaamanFileSafety.NoLinks(root);
        // Bound work before reading manifests and never enumerate linked directories.
        var directories = Directory.EnumerateDirectories(root).Take(1001).ToArray();
        if (directories.Length > 1000) throw new IOException("Too many SAAMAN recovery entries. Review the recovery folder manually.");
        foreach (string directory in directories.OrderDescending(StringComparer.Ordinal))
        {
            SaamanFileSafety.NoLinks(directory);
            string receipt = Path.Combine(directory, "receipt.json");
            if (!File.Exists(receipt) || File.Exists(Path.Combine(directory, "restored"))) continue;
            var record = ReadRecovery(receipt);
            if (record.TargetPath.Equals(target, StringComparison.OrdinalIgnoreCase))
                return record.PreviousHash is null ? null : record;
        }
        return null;
    }

    public void Restore(SaamanRecovery reviewed, AppSettings settings)
    {
        using var lease = Acquire();
        var recovery = ReadRecovery(reviewed.ReceiptPath);
        if (recovery != reviewed) throw new IOException("The recovery record changed after review.");
        if (recovery.PreviousHash is null)
            throw new InvalidOperationException("A fresh installation has no previous DLL to restore. Its DLL is retained because EuroScope profiles may reference it.");
        string target = ValidateTarget(recovery.TargetPath, settings, allowUnconfigured: true);
        RequireSeparateStorage(target);
        _requireClosed();
        using var targetParents = new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(target)!, true);
        string folder = Path.GetDirectoryName(recovery.ReceiptPath)!;
        using var receiptParents = new SaamanFileSafety.DirectoryGuard(folder, false);
        if (File.Exists(Path.Combine(folder, "restored"))) throw new InvalidOperationException("This recovery has already been applied.");
        SaamanDllInfo? current = File.Exists(target) ? _inspect(target) : null;
        bool pending = IsPending(recovery);
        if (current is null && !pending)
            throw new IOException("The installed DLL was removed after installation. Recovery will not change this path.");
        if (current is not null && current.Sha256 != recovery.InstalledHash &&
            !(pending && current.Sha256 == recovery.PreviousHash))
            throw new IOException("The DLL changed after this installation. Recovery will not overwrite a newer or edited copy.");
        if (recovery.PreviousHash is not null)
        {
            var snapshot = SaamanFileSafety.Snapshot(recovery.BackupPath);
            if (snapshot.Hash != recovery.PreviousHash || snapshot.Size != recovery.PreviousSize)
                throw new IOException("The SAAMAN recovery copy has changed.");
        }
        _requireClosed();
        if (current is not null && current.Sha256 == recovery.PreviousHash)
        { WriteNew(Path.Combine(folder, "restored"), "Original retained"); return; }
        // Copy the restored bytes before touching the current file. The original backup remains intact.
        string staging = Path.Combine(Path.GetDirectoryName(target)!, ".saaman-restore-" + Guid.NewGuid().ToString("N"));
        if (recovery.PreviousHash is not null)
            SaamanFileSafety.Copy(recovery.BackupPath, staging, recovery.PreviousSize, recovery.PreviousHash);
        _requireClosed();
        // Mark recovery in progress before deleting current bytes: a retry accepts the intentionally absent target.
        string recovering = Path.Combine(folder, "recovering");
        if (!File.Exists(recovering)) WriteNew(recovering, recovery.InstalledHash);
        if (current is not null) SaamanFileSafety.Delete(target, current.Size, current.Sha256);
        if (recovery.PreviousHash is not null)
            SaamanFileSafety.Move(staging, target, recovery.PreviousSize, recovery.PreviousHash);
        WriteNew(Path.Combine(folder, "restored"), recovery.PreviousHash!);
    }

    private static IDisposable Acquire()
    {
        if (!MaintenanceLock.TryAcquire(out var lease)) throw new InvalidOperationException("Another Launchpad maintenance operation is active.");
        return lease!;
    }
    private static IEnumerable<string> ProtectedPaths(AppSettings settings) =>
        new[] { settings.EuroscopeExePath, settings.TrackAudioExePath, settings.VacsExePath, settings.VatisExePath, settings.VatEfsPath }
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(SaamanFileSafety.Canonical).Order(StringComparer.OrdinalIgnoreCase);
    private static string ValidateTarget(string path, AppSettings settings, bool allowUnconfigured)
    {
        path = SaamanFileSafety.Canonical(path);
        if (!Path.GetFileName(path).Equals("SAAMAN.dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select the plugin named SAAMAN.dll. Renamed DLLs cannot be adopted or managed safely.");
        if (ProtectedPaths(settings).Any(other => SaamanFileSafety.Overlaps(path, other)))
            throw new InvalidOperationException("The SAAMAN DLL overlaps another configured application path.");
        string state = SaamanFileSafety.Canonical(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAMAN"));
        if (SaamanFileSafety.Overlaps(path, state)) throw new InvalidOperationException("SAAMAN's personal state folder cannot be an installation target.");
        if (!allowUnconfigured && !path.Equals(SaamanFileSafety.Canonical(GetSuggestedPath(settings)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The SAAMAN destination changed after review.");
        return path;
    }
    private void RequireSeparateStorage(string target)
    {
        if (SaamanFileSafety.Overlaps(target, SaamanFileSafety.Canonical(_storage)))
            throw new InvalidOperationException("The SAAMAN DLL and Launchpad recovery store must be separate.");
    }
    private void RequireOriginal(string target, SaamanDllInfo? previous)
    {
        if (previous is null)
        {
            if (File.Exists(target) || Directory.Exists(target)) throw new IOException("The installation target appeared after review.");
            return;
        }
        var current = _inspect(target);
        if (current != previous) throw new IOException("The installed DLL changed after review.");
    }
    private SaamanRecovery ReadRecovery(string path)
    {
        path = SaamanFileSafety.Canonical(path);
        string root = SaamanFileSafety.Canonical(Path.Combine(_storage, "Recovery"));
        string directory = Path.GetDirectoryName(path)!;
        if (!Path.GetDirectoryName(directory)!.Equals(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != "receipt.json")
            throw new IOException("The recovery receipt is outside Launchpad's SAAMAN store.");
        using var guard = new SaamanFileSafety.DirectoryGuard(directory, false);
        using var input = SaamanFileSafety.OpenRead(path);
        if (input.Length > 32768) throw new IOException("The SAAMAN recovery receipt is too large.");
        var receipt = JsonSerializer.Deserialize<SaamanRecovery>(input) ?? throw new IOException("Invalid SAAMAN recovery receipt.");
        if (receipt.ReceiptPath != path || string.IsNullOrWhiteSpace(receipt.TargetPath) ||
            !SaamanReleaseService.IsHash(receipt.InstalledHash) ||
            receipt.PreviousHash is not null && (!SaamanReleaseService.IsHash(receipt.PreviousHash) ||
                receipt.PreviousSize is <= 0 or > SaamanReleaseService.MaximumDllSize) ||
            receipt.BackupPath != Path.Combine(directory, "previous.dll") ||
            !SoftwareVersion.TryParse(receipt.Version, out _))
            throw new IOException("Invalid SAAMAN recovery receipt.");
        return receipt with { TargetPath = SaamanFileSafety.Canonical(receipt.TargetPath) };
    }
    private static bool IsPending(SaamanRecovery recovery) =>
        !File.Exists(Path.Combine(Path.GetDirectoryName(recovery.ReceiptPath)!, "completed")) ||
        File.Exists(Path.Combine(Path.GetDirectoryName(recovery.ReceiptPath)!, "recovering"));
    private static void ValidateChecksums(string directory, SaamanRelease release)
    {
        string[] lines = File.ReadAllLines(Path.Combine(directory, "SHA256SUMS.txt"));
        foreach (var asset in release.Assets.Where(asset => asset.Name != "SHA256SUMS.txt"))
        {
            var matches = lines.Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == asset.Name).ToArray();
            if (matches.Length != 1 || !matches[0][0].Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The release checksum manifest disagrees with GitHub's asset digest.");
        }
    }
    private static void WriteNew(string path, string text)
    {
        SaamanFileSafety.NoLinks(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(output))
        { writer.Write(text); writer.Flush(); output.Flush(true); }
        var snapshot = SaamanFileSafety.Snapshot(temporary);
        SaamanFileSafety.Move(temporary, path, snapshot.Size, snapshot.Hash);
    }
}
