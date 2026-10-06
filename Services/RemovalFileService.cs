using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Deletes only a verified inventory. It never follows links or recursively deletes a folder.</summary>
public static class RemovalFileService
{
    public const int MaximumEntries = 50000;
    public const int MaximumRoots = 128;
    public const int MaximumDepth = 64;
    public const long MaximumBytes = 32L * 1024 * 1024 * 1024;
    private const uint ReadAttributes = 0x80, DeleteAccess = 0x10000, GenericRead = 0x80000000;
    private const uint DirectoryAttribute = 0x10, ReparseAttribute = 0x400;
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private const string RestoreInstructions =
        "This is a verified file snapshot, not an automatic restore tool.\r\n" +
        "Keep the applications closed. Read removal-backup.json for each original root and its Files/NNNN folder.\r\n" +
        "For directory roots, copy that folder's contents and empty subfolders to the original root.\r\n" +
        "For file roots, copy the named file to OriginalPath. Do not overwrite newer files without reviewing them.\r\n" +
        "Verify file size and SHA-256 against the manifest before restoring. Original file attributes are recorded.\r\n" +
        "Registry records, Windows credentials, ACLs and timestamps are not part of this file snapshot.\r\n" +
        "Install the application using its official installer if registration or prerequisites are needed.\r\n";

    public static RemovalFilePlan Preview(IEnumerable<string> roots)
    {
        RequireWindows();
        var selected = NormalizeRoots(roots);
        var files = new List<RemovalFileSnapshot>();
        var directories = new List<RemovalDirectorySnapshot>();
        long total = 0;
        using var locks = new DirectoryLocks();
        foreach (var root in selected)
        {
            locks.AddAncestors(root, includeSelf: Directory.Exists(root));
            if (Directory.Exists(root)) Scan(root, root, 0);
            else AddFile(root, root, Path.GetFileName(root));
        }
        return new(selected, files.OrderBy(file => file.FullPath, Paths), directories.OrderBy(dir => dir.FullPath, Paths));

        void Scan(string root, string path, int depth)
        {
            if (depth > MaximumDepth) throw new IOException("The selected folder exceeds the supported depth.");
            locks.Add(path);
            var info = Information(locks[path]);
            RequireType(info, directory: true);
            RejectNamedStreams(path);
            directories.Add(new(root, path, Path.GetRelativePath(root, path) is "." ? "" : Path.GetRelativePath(root, path))
                { Identity = info.Identity, Attributes = info.Attributes });
            CheckLimits();
            foreach (var child in Directory.EnumerateFileSystemEntries(path).OrderBy(value => value, Paths))
            {
                RejectReparse(child);
                if (Directory.Exists(child)) Scan(root, child, depth + 1);
                else AddFile(root, child, Path.GetRelativePath(root, child));
            }
        }
        void AddFile(string root, string path, string relative)
        {
            using var file = OpenFile(path, delete: false);
            var info = Information(file.SafeFileHandle);
            RequireType(info, directory: false);
            total = checked(total + info.Length);
            if (total > MaximumBytes) throw new IOException("The selected files exceed the supported backup size.");
            RejectNamedStreams(path);
            var hash = Hash(file, CancellationToken.None);
            files.Add(new(root, path, relative, info.Length, hash) { Identity = info.Identity, Attributes = info.Attributes });
            CheckLimits();
        }
        void CheckLimits()
        {
            if (files.Count + directories.Count > MaximumEntries)
                throw new IOException("The selected folders contain too many entries.");
        }
    }

    public static void Verify(RemovalFilePlan plan)
    {
        using var locked = AcquireVerified(plan, delete: false, CancellationToken.None);
    }

    public static void VerifyRemaining(RemovalFilePlan plan) => Verify(RemainingPlan(plan));

    /// <summary>Checks a recovery export against the trusted preview, never paths supplied by its manifest.</summary>
    public static void VerifyBackup(RemovalFilePlan plan, string backupFolder)
    {
        RequireWindows();
        var folder = NormalizePath(backupFolder);
        RejectReparse(folder);
        folder = CanonicalExistingPath(folder);
        if (plan.Roots.Any(root => IsWithin(folder, root))) throw new IOException("The backup overlaps a removal root.");
        var rootMap = BackupRoots(plan);
        string Destination(string root, string relative) => Path.Combine(folder,
            rootMap.Single(item => Paths.Equals(item.OriginalPath, root)).BackupRelativePath, relative);
        var directories = new HashSet<string>(Paths) { folder, Path.Combine(folder, "Files") };
        foreach (var root in rootMap) directories.Add(Path.Combine(folder, root.BackupRelativePath));
        foreach (var directory in plan.Directories) directories.Add(Destination(directory.RootPath, directory.RelativePath));
        var expectedFiles = plan.Files.Select(file => file with
        {
            RootPath = folder, FullPath = Destination(file.RootPath, file.RelativePath),
            RelativePath = Path.GetRelativePath(folder, Destination(file.RootPath, file.RelativePath))
        }).ToList();
        using var locked = new LockedPlan();
        locked.Directories.AddAncestors(folder, includeSelf: true);
        foreach (var directory in directories.OrderBy(path => path.Length))
        {
            locked.Directories.Add(directory);
            RejectNamedStreams(directory);
        }
        foreach (var file in expectedFiles)
        {
            var stream = OpenFile(file.FullPath, delete: false);
            locked.Files.Add(file.FullPath, stream);
            RejectNamedStreams(file.FullPath);
            if (stream.Length != file.Length || Hash(stream, CancellationToken.None) != file.Sha256)
                throw new IOException("A recovery export file changed: " + file.FullPath);
        }
        var manifestPath = Path.Combine(folder, "removal-backup.json");
        var instructionsPath = Path.Combine(folder, "RESTORE.txt");
        var manifest = ReadLockedText(manifestPath, 64 * 1024 * 1024);
        var instructions = ReadLockedText(instructionsPath, 16384);
        if (instructions != RestoreInstructions) throw new IOException("The recovery instructions changed.");
        try
        {
            using var document = JsonDocument.Parse(manifest);
            var date = document.RootElement.GetProperty("CreatedUtc").GetDateTimeOffset();
            if (!JsonNode.DeepEquals(JsonNode.Parse(manifest), JsonNode.Parse(BackupManifest(plan, date))))
                throw new IOException("The recovery manifest no longer matches the reviewed snapshot.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { throw new IOException("The recovery manifest is invalid.", ex); }
        VerifyInventory(new RemovalFilePlan([folder], expectedFiles,
            directories.Select(path => new RemovalDirectorySnapshot(folder, path, Path.GetRelativePath(folder, path)))),
            extraDepth: 2, extraEntries: MaximumRoots + 4);

        string ReadLockedText(string path, long limit)
        {
            var stream = OpenFile(path, delete: false);
            locked.Files.Add(path, stream);
            RejectNamedStreams(path);
            if (stream.Length > limit) throw new IOException("Recovery metadata exceeds the supported size.");
            expectedFiles.Add(new(folder, path, Path.GetFileName(path), stream.Length, ""));
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            return reader.ReadToEnd();
        }
    }

    public static Task<string> BackupAsync(RemovalFilePlan plan, string backupRoot,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Backup(plan, backupRoot, progress, cancellationToken), cancellationToken);

    public static Task<RemovalDeleteResult> DeleteAsync(RemovalFilePlan plan, IProgress<string>? progress = null) =>
        Task.Run(() => Delete(plan, progress));

    /// <summary>After a vendor uninstall, permits missing reviewed entries but never added or replaced entries.</summary>
    public static Task<RemovalDeleteResult> DeleteRemainingAsync(RemovalFilePlan plan, IProgress<string>? progress = null) =>
        Task.Run(() => Delete(RemainingPlan(plan), progress));

    private static RemovalFilePlan RemainingPlan(RemovalFilePlan plan)
    {
            RequireWindows();
            var survivingRoots = new List<string>();
            foreach (var root in plan.Roots)
            {
                RejectReparse(root);
                if (File.Exists(root) || Directory.Exists(root)) survivingRoots.Add(root);
            }
            var remaining = Preview(survivingRoots);
            var files = plan.Files.ToDictionary(file => file.FullPath, Paths);
            var directories = plan.Directories.ToDictionary(dir => dir.FullPath, Paths);
            if (remaining.Files.Any(file => !files.TryGetValue(file.FullPath, out var original) || file != original)
                || remaining.Directories.Any(dir => !directories.TryGetValue(dir.FullPath, out var original) || dir != original))
                throw new IOException("Files or folders changed after the removal preview. Review the remaining files before removing them.");
            return remaining;
    }

    private static string Backup(RemovalFilePlan plan, string backupRoot, IProgress<string>? progress, CancellationToken token)
    {
        RequireWindows();
        var container = NormalizePath(backupRoot);
        RejectReparse(container);
        if (!Directory.Exists(container)) throw new DirectoryNotFoundException("Choose an existing backup folder.");
        container = CanonicalExistingPath(container);
        if (plan.Roots.Any(root => IsWithin(container, root)))
            throw new IOException("The backup folder must be outside every selected removal root.");
        if (IsWithinWindows(container)) throw new IOException("The Windows folder cannot hold a removal backup.");
        using var locked = AcquireVerified(plan, delete: false, token);
        using var backupLocks = new DirectoryLocks();
        backupLocks.AddAncestors(container, includeSelf: true);
        token.ThrowIfCancellationRequested();
        var folder = Path.Combine(container, "ATC-backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        backupLocks.Add(folder);
        var rootMap = BackupRoots(plan);
        try
        {
            CreateBackupDirectory(Path.Combine(folder, "Files"));
            foreach (var root in rootMap) CreateBackupDirectory(Path.Combine(folder, root.BackupRelativePath));
            foreach (var directory in plan.Directories.OrderBy(dir => dir.FullPath.Length))
            {
                token.ThrowIfCancellationRequested();
                CreateBackupDirectory(Destination(directory.RootPath, directory.RelativePath));
            }
            foreach (var file in plan.Files)
            {
                token.ThrowIfCancellationRequested();
                Report(progress, "Backing up " + file.FullPath);
                var destination = Destination(file.RootPath, file.RelativePath);
                CreateBackupDirectory(Path.GetDirectoryName(destination)!);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                var source = locked.Files[file.FullPath];
                source.Position = 0;
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
                output.Flush(flushToDisk: true);
                if (output.Length != file.Length || Hash(output, token) != file.Sha256)
                    throw new IOException("A backup copy did not match its verified source: " + file.FullPath);
            }
            token.ThrowIfCancellationRequested();
            // Only a fully copied and verified snapshot receives the completed manifest.
            var manifest = BackupManifest(plan, DateTimeOffset.UtcNow);
            WriteNewText(Path.Combine(folder, "RESTORE.txt"), RestoreInstructions);
            token.ThrowIfCancellationRequested();
            WriteNewText(Path.Combine(folder, "removal-backup.json"), manifest);
            return folder;
        }
        catch
        {
            // Preserve evidence and any copied bytes; never silently delete a partial recovery backup.
            try { WriteNewText(Path.Combine(folder, "INCOMPLETE.txt"), "This backup did not finish. Do not treat it as a complete recovery snapshot. The original selected files were not removed by the backup operation.\r\n"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }

        string Destination(string root, string relative) => Path.Combine(folder,
            rootMap.Single(item => Paths.Equals(item.OriginalPath, root)).BackupRelativePath, relative);
        void CreateBackupDirectory(string path)
        {
            if (Paths.Equals(path, folder)) return;
            var parent = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(parent)) CreateBackupDirectory(parent);
            RejectReparse(parent);
            Directory.CreateDirectory(path);
            backupLocks.Add(path);
        }
    }

    private sealed record BackupRoot(int Index, string OriginalPath, bool IsDirectory, string BackupRelativePath);
    private static BackupRoot[] BackupRoots(RemovalFilePlan plan) => plan.Roots.Select((root, index) =>
        new BackupRoot(index, root, plan.Directories.Any(dir => Paths.Equals(dir.FullPath, root)),
            Path.Combine("Files", index.ToString("D4")))).ToArray();

    private static string BackupManifest(RemovalFilePlan plan, DateTimeOffset created)
    {
        var roots = BackupRoots(plan);
        return JsonSerializer.Serialize(new
        {
            Format = 1, CreatedUtc = created, Roots = roots,
            Files = plan.Files.Select(file => new { RootIndex = Array.FindIndex(roots, root => Paths.Equals(root.OriginalPath, file.RootPath)), file.RelativePath, file.Length, file.Sha256, file.Attributes }),
            Directories = plan.Directories.Select(dir => new { RootIndex = Array.FindIndex(roots, root => Paths.Equals(root.OriginalPath, dir.RootPath)), dir.RelativePath, dir.Attributes })
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static RemovalDeleteResult Delete(RemovalFilePlan plan, IProgress<string>? progress)
    {
        // Acquire every selected file and verify the entire inventory before the first deletion.
        using var locked = AcquireVerified(plan, delete: true, CancellationToken.None);
        int deletedFiles = 0, deletedDirectories = 0;
        foreach (var file in plan.Files)
        {
            try
            {
                Report(progress, "Removing " + file.FullPath);
                RequireType(Information(locked.Files[file.FullPath].SafeFileHandle), directory: false);
                RejectNamedStreams(file.FullPath);
                MarkForDeletion(locked.Files[file.FullPath].SafeFileHandle);
                locked.Files[file.FullPath].Dispose();
                deletedFiles++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed(file.FullPath, ex);
            }
        }
        foreach (var directory in plan.Directories.OrderByDescending(dir => dir.FullPath.Length))
        {
            try
            {
                Report(progress, "Removing empty folder " + directory.FullPath);
                RequireType(Information(locked.Directories[directory.FullPath]), directory: true);
                RejectNamedStreams(directory.FullPath);
                // Handle-based deletion fails if an unpreviewed child appeared; no recursive fallback.
                MarkForDeletion(locked.Directories[directory.FullPath]);
                locked.Directories[directory.FullPath].Dispose();
                deletedDirectories++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed(directory.FullPath, ex);
            }
        }
        return new(deletedFiles, deletedDirectories, Array.Empty<RemovalFileError>());
        RemovalDeleteResult Failed(string path, Exception ex) => new(deletedFiles, deletedDirectories,
            Array.AsReadOnly(new[] { new RemovalFileError(path, ex.Message + " Remaining items were left untouched.") }));
    }

    private static LockedPlan AcquireVerified(RemovalFilePlan plan, bool delete, CancellationToken token)
    {
        RequireWindows();
        ArgumentNullException.ThrowIfNull(plan);
        var normalized = NormalizeRoots(plan.Roots);
        if (!normalized.SequenceEqual(plan.Roots, Paths)) throw new IOException("The selected roots changed since preview.");
        var locked = new LockedPlan();
        try
        {
            var ownedDirectories = plan.Directories.Select(dir => dir.FullPath).ToHashSet(Paths);
            foreach (var root in plan.Roots)
                locked.Directories.AddAncestors(root, Directory.Exists(root), ownedDirectories, delete);
            foreach (var directory in plan.Directories.OrderBy(dir => dir.FullPath.Length))
            {
                token.ThrowIfCancellationRequested();
                locked.Directories.Add(directory.FullPath, delete);
                var info = Information(locked.Directories[directory.FullPath]);
                RequireType(info, directory: true);
                RejectNamedStreams(directory.FullPath);
                if (info.Identity != directory.Identity || info.Attributes != directory.Attributes)
                    throw new IOException("A selected directory changed since preview: " + directory.FullPath);
            }
            foreach (var file in plan.Files)
            {
                token.ThrowIfCancellationRequested();
                var stream = OpenFile(file.FullPath, delete);
                locked.Files.Add(file.FullPath, stream);
                var info = Information(stream.SafeFileHandle);
                RequireType(info, directory: false);
                if (info.Identity != file.Identity || info.Length != file.Length || info.Attributes != file.Attributes)
                    throw new IOException("A selected file changed since preview: " + file.FullPath);
                RejectNamedStreams(file.FullPath);
                if (Hash(stream, token) != file.Sha256)
                    throw new IOException("A selected file's contents changed since preview: " + file.FullPath);
            }
            VerifyInventory(plan);
            token.ThrowIfCancellationRequested();
            return locked;
        }
        catch { locked.Dispose(); throw; }
    }

    private static void VerifyInventory(RemovalFilePlan plan, int extraDepth = 0, int extraEntries = 0)
    {
        var expectedFiles = plan.Files.Select(file => file.FullPath).ToHashSet(Paths);
        var expectedDirectories = plan.Directories.Select(dir => dir.FullPath).ToHashSet(Paths);
        var actualFiles = new HashSet<string>(Paths);
        var actualDirectories = new HashSet<string>(Paths);
        foreach (var root in plan.Roots)
        {
            if (Directory.Exists(root)) Scan(root, 0);
            else if (File.Exists(root)) actualFiles.Add(root);
            else throw new IOException("A selected root no longer exists: " + root);
        }
        if (!actualFiles.SetEquals(expectedFiles) || !actualDirectories.SetEquals(expectedDirectories))
            throw new IOException("The selected file or directory inventory changed. Preview the removal again.");
        void Scan(string path, int depth)
        {
            if (depth > MaximumDepth + extraDepth || actualFiles.Count + actualDirectories.Count > MaximumEntries + extraEntries)
                throw new IOException("The selected inventory exceeds supported limits.");
            RejectReparse(path);
            if (!expectedDirectories.Contains(path)) throw new IOException("An unpreviewed directory appeared: " + path);
            actualDirectories.Add(path);
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                RejectReparse(child);
                if (Directory.Exists(child)) Scan(child, depth + 1);
                else
                {
                    if (!expectedFiles.Contains(child)) throw new IOException("An unpreviewed file appeared: " + child);
                    actualFiles.Add(child);
                }
            }
        }
    }

    private static string[] NormalizeRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var selected = new List<string>();
        foreach (var input in roots)
        {
            if (selected.Count >= MaximumRoots) throw new IOException("Too many removal roots were selected.");
            var path = NormalizePath(input);
            // Reject ordinary protected paths before opening or enumerating them; check canonical aliases again below.
            ProtectRoot(path);
            RejectReparse(path);
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("A selected removal path does not exist.", path);
            path = CanonicalExistingPath(path);
            ProtectRoot(path);
            if (selected.Any(other => IsWithin(path, other) || IsWithin(other, path)))
                throw new IOException("Removal roots must not overlap or refer to the same path.");
            selected.Add(path);
        }
        return selected.OrderBy(path => path, Paths).ToArray();
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length < 3
            || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')
            || path.AsSpan(2).Contains(':') || path.Split('\\', '/').Any(part => part is "." or ".."
                || part.EndsWith(' ') || part.EndsWith('.') || IsDeviceName(part)))
            throw new IOException("Only ordinary absolute local paths are supported; links, streams, devices and network paths are excluded.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new IOException("Network and unsupported drives cannot be used for removal or backup.");
        return full;
    }

    private static bool IsDeviceName(string component)
    {
        var name = component.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$"
            || name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(name[3]);
    }

    private static void ProtectRoot(string path)
    {
        if (Paths.Equals(path, Path.GetPathRoot(path)) || IsWithinWindows(path))
            throw new IOException("Drive roots and Windows system folders cannot be removed.");
        var driveRoot = Path.GetPathRoot(path)!;
        if (new[] { "$Recycle.Bin", "System Volume Information", "Recovery", "Config.Msi" }
            .Any(name => IsWithin(path, Path.Combine(driveRoot, name))))
            throw new IOException("Windows-managed system data cannot be removed.");
        var protectedFolders = new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.CommonDocuments,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos };
        if (protectedFolders.Select(Environment.GetFolderPath).Where(folder => folder.Length > 0)
            .Any(folder => IsWithin(folder, path)))
            throw new IOException("A broad user, application-data or shared system folder cannot be removed.");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && IsWithin(Path.Combine(home, "Downloads"), path))
            throw new IOException("The broad downloads folder cannot be removed.");
        var profilesRoot = Path.GetDirectoryName(home);
        if (Directory.Exists(path) && profilesRoot != null && IsWithin(path, profilesRoot))
        {
            var parts = Path.GetRelativePath(profilesRoot, path).Split(Path.DirectorySeparatorChar);
            if (parts.Length == 1 || parts.Length == 2 && Paths.Equals(parts[1], "AppData")
                || parts.Length == 3 && Paths.Equals(parts[1], "AppData")
                    && new[] { "Local", "LocalLow", "Roaming" }.Contains(parts[2], Paths))
                throw new IOException("A broad user profile or application-data root cannot be removed.");
        }
    }

    private static bool IsWithinWindows(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 && IsWithin(path, windows);
    }

    private static bool IsWithin(string path, string parent)
    {
        parent = Path.TrimEndingDirectorySeparator(parent);
        return Paths.Equals(Path.TrimEndingDirectorySeparator(path), parent)
            || path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparse(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                // Exists can return false for a dangling link, while its own attributes remain readable.
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked folders and files cannot be removed or used for backups: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string CanonicalExistingPath(string path)
    {
        using var handle = OpenHandle(path, ReadAttributes, directory: Directory.Exists(path));
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw NativeError("Could not resolve a selected path");
        var canonical = buffer.ToString();
        if (!canonical.StartsWith("\\\\?\\", StringComparison.Ordinal)) throw new IOException("The selected path is not on an ordinary local drive.");
        canonical = NormalizePath(canonical[4..]);
        RejectReparse(canonical);
        return canonical;
    }

    private static FileStream OpenFile(string path, bool delete)
    {
        RejectReparse(path);
        var handle = OpenHandle(path, GenericRead | (delete ? DeleteAccess : 0), directory: false);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenHandle(string path, uint access, bool directory)
    {
        var handle = CreateFile("\\\\?\\" + path, access, directory ? 3u : 1u, IntPtr.Zero, 3,
            0x00200000u | (directory ? 0x02000000u : 0u), IntPtr.Zero);
        if (handle.IsInvalid) { var error = NativeError("Cannot lock " + path); handle.Dispose(); throw error; }
        try { RequireType(Information(handle), directory); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static FileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw NativeError("Cannot inspect a selected file");
        return info;
    }

    private static void RequireType(FileInformation info, bool directory)
    {
        if ((info.Attributes & ReparseAttribute) != 0 || ((info.Attributes & DirectoryAttribute) != 0) != directory)
            throw new IOException("A selected path changed type or is a link.");
    }

    private static string Hash(Stream stream, CancellationToken token)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        token.ThrowIfCancellationRequested();
        stream.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void MarkForDeletion(SafeFileHandle handle)
    {
        // Windows 10+ can ignore only the readonly attribute without altering the original file metadata.
        uint flags = 0x11; // FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE
        if (SetFileInformationByHandle(handle, 21, ref flags, sizeof(uint))) return;
        int error = Marshal.GetLastWin32Error();
        if (error is 87 or 50 or 1)
        {
            byte delete = 1;
            if (SetLegacyDisposition(handle, 4, ref delete, 1)) return;
            error = Marshal.GetLastWin32Error();
        }
        throw new IOException("Could not remove the verified file or empty directory: " + new Win32Exception(error).Message);
    }

    private static void RejectNamedStreams(string path)
    {
        var handle = FindFirstStream("\\\\?\\" + path, 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 38) return;
            var format = new DriveInfo(Path.GetPathRoot(path)!).DriveFormat;
            if (error is 1 or 50 or 87 && (format.Equals("FAT32", StringComparison.OrdinalIgnoreCase) || format.Equals("exFAT", StringComparison.OrdinalIgnoreCase))) return;
            throw NativeError("Cannot inspect alternate file streams");
        }
        try
        {
            do
            {
                if (data.StreamName != "::$DATA") throw new IOException("Named alternate data streams need a manual backup/removal: " + path);
            } while (FindNextStream(handle, out data));
            if (Marshal.GetLastWin32Error() != 38) throw NativeError("Cannot finish inspecting alternate file streams");
        }
        finally { FindClose(handle); }
    }

    private static void WriteNewText(string path, string text)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        writer.Write(text);
        writer.Flush();
        file.Flush(flushToDisk: true);
    }
    private static void Report(IProgress<string>? progress, string message) { try { progress?.Report(message); } catch { } }
    private static void RequireWindows() { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Removal is supported only on Windows."); }
    private static IOException NativeError(string message) => new(message + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);

    private sealed class LockedPlan : IDisposable
    {
        public DirectoryLocks Directories { get; } = new();
        public Dictionary<string, FileStream> Files { get; } = new(Paths);
        public void Dispose() { foreach (var file in Files.Values) file.Dispose(); Directories.Dispose(); }
    }

    private sealed class DirectoryLocks : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle> _handles = new(Paths);
        public SafeFileHandle this[string path] => _handles[path];
        public void Add(string path, bool delete = false)
        {
            if (_handles.ContainsKey(path)) return;
            RejectReparse(path);
            _handles.Add(path, OpenHandle(path, ReadAttributes | (delete ? DeleteAccess : 0), directory: true));
        }
        public void AddAncestors(string path, bool includeSelf, HashSet<string>? owned = null, bool delete = false)
        {
            var stack = new Stack<string>();
            for (var current = includeSelf ? path : Path.GetDirectoryName(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                stack.Push(current);
            foreach (var current in stack) Add(current, delete && owned?.Contains(current) == true);
        }
        public void Dispose() { foreach (var handle in _handles.Values) handle.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        public readonly long Length => checked((long)(((ulong)SizeHigh << 32) | SizeLow));
        public readonly RemovalFileIdentity Identity => new(Volume, ((ulong)IndexHigh << 32) | IndexLow, unchecked((long)(((ulong)CreationHigh << 32) | CreationLow)));
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string StreamName;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref uint info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLegacyDisposition(SafeFileHandle file, int kind, ref byte info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStream(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStream(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
}
