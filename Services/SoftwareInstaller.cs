using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Vendor installers are invoked only by InstallAsync, after an explicit reviewed update.</summary>
public sealed class SoftwareInstaller : ISoftwareInstaller
{
    internal const string VatisId = "org.vatsim.vatis";
    internal const string TrackAudioGuid = "0ea8a3fe-afd3-53a8-a5c8-7769ac8d84c5";
    private const int MaximumFiles = 25000;
    private const long MaximumBytes = 4L * 1024 * 1024 * 1024;
    private readonly SoftwareInstallerEnvironment _environment;
    private readonly string _storage;
    private BackupReceipt? _backup;
    public bool RestartRequired { get; private set; }

    public SoftwareInstaller() : this(new SoftwareInstallerEnvironment()) { }
    internal SoftwareInstaller(SoftwareInstallerEnvironment environment)
    {
        _environment = environment;
        _storage = Path.Combine(environment.LocalAppData, "VatscaUpdateChecker", "SoftwareUpdates");
    }

    public SoftwareInstallation Inspect(SoftwareApp app, string exePath)
    {
        try
        {
            var exe = FullPath(exePath);
            RequireFile(exe);
            if (app == SoftwareApp.Vatis) return InspectVatis(exe);

            var expectedName = app == SoftwareApp.Vacs ? "vacs-client.exe" : "trackaudio.exe";
            if (!Path.GetFileName(exe).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The configured executable is not the supported product executable.");
            var registrations = _environment.ReadRegistrations(app);
            // Multiple installations/scopes are ambiguous to the vendor's default-location selection.
            var matches = registrations.Where(r => SamePath(Path.Combine(r.RootPath, r.MainBinaryName), exe)).ToArray();
            if (matches.Length != 1 || registrations.Count != 1)
                throw new InvalidDataException("A single matching registered installation is required; portable or ambiguous installations need a manual update.");
            var registration = matches[0];
            if (app == SoftwareApp.TrackAudio && registration.Scope != "CurrentUser")
                throw new InvalidDataException("TrackAudio's current one-click installer supports per-user upgrades only. Update this machine-wide installation manually.");
            if (registration.Scope is not ("CurrentUser" or "AllUsers") ||
                !SamePath(registration.RootPath, registration.RestoreRootPath))
                throw new InvalidDataException("The registered scope or installer destination does not match the configured executable.");
            var root = FullPath(registration.RootPath);
            RejectReparse(root);
            if (!SamePath(Path.GetDirectoryName(exe)!, root) || Path.GetPathRoot(root) == root)
                throw new InvalidDataException("Unsupported installation layout.");
            var binary = _environment.ReadBinary(exe);
            var version = NormalizeVersion(binary.ProductVersion);
            var registeredVersion = NormalizeVersion(registration.Version);
            var appName = app == SoftwareApp.Vacs ? "VACS" : "TrackAudio";
            if (!binary.ProductName.Equals(app == SoftwareApp.Vacs ? "vacs" : "TrackAudio", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The selected program is not identified as {appName}. Choose the installed {expectedName} file in Settings.");
            if (registeredVersion != version)
                throw new InvalidDataException($"{appName} is version {VersionForMessage(version)}, but Windows lists version {VersionForMessage(registeredVersion)}. Repair or reinstall {appName} using its official installer before updating it in Launchpad.");
            if (app == SoftwareApp.Vacs && !version.StartsWith("2.", StringComparison.Ordinal))
                throw new InvalidDataException("Only the VACS 2.x NSIS installation layout is supported.");
            var prerequisite = _environment.PrerequisiteProblem(app);
            if (prerequisite != null) throw new InvalidDataException(prerequisite);
            return new(app, exe, version, root, registration.Scope, null, true, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or XmlException or InvalidOperationException or CryptographicException)
        {
            return new(app, exePath, "", "", "", null, false, ex.Message);
        }
    }

    private SoftwareInstallation InspectVatis(string configuredExe)
    {
        var root = FullPath(Path.Combine(_environment.LocalAppData, VatisId));
        var main = Path.Combine(root, "current", "vATIS.exe");
        if (!SamePath(configuredExe, main) && !SamePath(configuredExe, Path.Combine(root, "vATIS.exe")))
            throw new InvalidDataException("Only vATIS's standard per-user Velopack installation is supported.");
        RequireFile(main);
        var updater = Path.Combine(root, "Update.exe");
        RequireFile(updater);
        var manifest = ReadManifestFile(Path.Combine(root, "current", "sq.version"));
        var binary = _environment.ReadBinary(main);
        var helper = _environment.ReadBinary(updater);
        RequireVatisManifest(manifest, NormalizeVersion(binary.ProductVersion));
        if (!binary.ProductName.Equals("vATIS", StringComparison.OrdinalIgnoreCase) ||
            NormalizeVersion(helper.ProductVersion) != "0.0.1251")
            throw new InvalidDataException("This vATIS executable or updater version has not been verified for background upgrades.");
        _environment.VerifyPublisher(main, "Justin Shannon");
        _environment.VerifyPublisher(updater, "Justin Shannon");
        return new(SoftwareApp.Vatis, configuredExe, manifest.Version, root, "CurrentUser", updater, true, null);
    }

    public bool IsRunning(SoftwareInstallation installation) => _environment.IsRunning(installation.App);

    public async Task VerifyPackageAsync(SoftwareRelease release, string packagePath, CancellationToken cancellationToken)
    {
        ValidateRelease(release);
        RequireFile(packagePath);
        await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != release.Size ||
            !Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The package does not match the official release size and SHA-256.");
        if (release.App != SoftwareApp.Vatis)
        {
            var binary = _environment.ReadBinary(packagePath);
            if (NormalizeVersion(binary.ProductVersion) != release.Version ||
                !binary.ProductName.Equals(release.App == SoftwareApp.Vacs ? "vacs" : "TrackAudio", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer product/version does not match the selected release.");
            return;
        }

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = SafeRelative(entry.FullName.TrimEnd('/'));
            if (!names.Add(name) || names.Count > MaximumFiles || (total += entry.Length) > MaximumBytes ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("The package has duplicate, linked, or excessive entries.");
        }
        RequireVatisManifest(ReadManifestEntry(zip, VatisId + ".nuspec"), release.Version);
        RequireVatisManifest(ReadManifestEntry(zip, "lib/app/sq.version"), release.Version);
        var verificationFolder = Path.Combine(_storage, "Verification", Guid.NewGuid().ToString("N"));
        RejectReparse(verificationFolder);
        Directory.CreateDirectory(verificationFolder);
        try
        {
            foreach (var name in new[] { "vATIS.exe", "Squirrel.exe" })
            {
                var entry = zip.GetEntry("lib/app/" + name) ?? throw new InvalidDataException("The vATIS package is missing a required executable.");
                var destination = Path.Combine(verificationFolder, name);
                await using (var source = entry.Open())
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                _environment.VerifyPublisher(destination, "Justin Shannon");
                var identity = _environment.ReadBinary(destination);
                if (name == "vATIS.exe" && (NormalizeVersion(identity.ProductVersion) != release.Version || identity.ProductName != "vATIS") ||
                    name == "Squirrel.exe" && NormalizeVersion(identity.ProductVersion) != "0.0.1251")
                    throw new InvalidDataException("The package contains an unsupported vATIS executable/updater.");
            }
        }
        finally
        {
            // This directory is unique, created by this method, and contains only the two extracted files.
            RejectReparse(verificationFolder);
            foreach (var name in new[] { "vATIS.exe", "Squirrel.exe" })
            {
                var file = Path.Combine(verificationFolder, name);
                RejectReparse(file);
                if (File.Exists(file)) File.Delete(file);
            }
            Directory.Delete(verificationFolder);
        }
    }

    public async Task<string?> BackupAsync(SoftwareInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        _backup = null;
        Revalidate(installation);
        RequireStopped(installation);
        if (installation.App != SoftwareApp.Vatis) return null;
        progress?.Report("Backing up all vATIS program files and local data…");
        var backupFolder = Path.Combine(_storage, "Backups", "vATIS-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        if (IsWithin(installation.RootPath, backupFolder)) throw new InvalidDataException("A backup cannot be inside the installation.");
        RejectReparse(backupFolder);
        Directory.CreateDirectory(backupFolder);
        var files = Inventory(installation.RootPath);
        var entries = new List<BackupFile>();
        foreach (var relative in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.Combine(installation.RootPath, relative);
            var destination = Path.Combine(backupFolder, "Files", relative);
            RequireFile(sourcePath);
            RejectReparse(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            source.Position = 0;
            entries.Add(new(relative, source.Length, Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false))));
        }
        var receipt = new BackupReceipt(installation, backupFolder, entries);
        await VerifyBackupSnapshot(receipt, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(backupFolder, "restore-manifest.json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(backupFolder, "RESTORE.txt"),
            "Keep this private backup: it includes vATIS profiles and sign-in settings.\r\n" +
            "With vATIS and its updater closed, preserve any changed installation separately, then copy the entire Files folder contents back to the Original.RootPath in restore-manifest.json.\r\n" +
            "Do not run Setup over an existing vATIS data folder. The backup includes binaries and root data so recovery does not require a reinstall.\r\n", cancellationToken).ConfigureAwait(false);
        _backup = receipt;
        return backupFolder;
    }

    public async Task<SoftwareInstallResult> InstallAsync(SoftwareInstallation installation, SoftwareRelease release,
        string packagePath, IProgress<string>? progress)
    {
        if (RestartRequired) throw new InvalidOperationException("Restart Windows before further application updates.");
        if (release.App != installation.App) throw new InvalidDataException("Installation and package products differ.");
        Revalidate(installation);
        await using var packageLock = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await VerifyPackageAsync(release, packagePath, CancellationToken.None).ConfigureAwait(false);
        if (!IsNewer(release.Version, installation.Version)) throw new InvalidDataException("Only an explicitly selected newer version may be installed.");
        if (installation.App == SoftwareApp.Vatis)
        {
            if (_backup == null || _backup.Original != installation) throw new InvalidDataException("A completed vATIS backup is required before applying its update.");
            await VerifyBackupSnapshot(_backup, CancellationToken.None).ConfigureAwait(false);
        }
        Revalidate(installation);
        RequireStopped(installation);
        var command = BuildCommand(installation, packagePath);
        // Keep verified input paths from being replaced while the vendor process opens/uses them.
        // The supported 0.0.1251 apply command replaces current only; updating the root helper is a
        // separate update-self command. Its fast hooks do not replace this helper, so this lock is safe.
        using var helperLock = installation.UpdaterPath == null ? null : new FileStream(installation.UpdaterPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Revalidate(installation);
        RequireStopped(installation);
        progress?.Report("Installing in the background. Do not start the application until this finishes…");
        var exitCode = await _environment.RunAsync(command).ConfigureAwait(false);
        RestartRequired |= exitCode == 3010;
        if (exitCode == 1223) throw new IOException("Windows permission approval was cancelled; the update was not completed.");
        if (exitCode != 0) throw new IOException($"The vendor installer returned exit code {exitCode}. Check the installed version before retrying; keep the backup.");
        var after = Inspect(installation.App, installation.ExePath);
        if (!after.CanUpdate || !SamePath(after.RootPath, installation.RootPath) || after.Scope != installation.Scope || after.Version != release.Version)
            throw new IOException("The installer exited, but the expected installed version and destination could not be verified. Keep the backup and update manually if needed.");
        if (IsRunning(after)) throw new IOException("The vendor installation finished, but the application is running unexpectedly. It was not stopped by Launchpad.");
        _backup = null;
        return new(after.ExePath);
    }

    internal static SoftwareInstallCommand BuildCommand(SoftwareInstallation installation, string packagePath) => installation.App switch
    {
        SoftwareApp.Vacs => new(packagePath, ["/S", "/UPDATE", installation.Scope == "AllUsers" ? "/AllUsers" : "/CurrentUser"], installation.Scope == "AllUsers"),
        SoftwareApp.TrackAudio when installation.Scope == "CurrentUser" => new(packagePath, ["/S"], false),
        SoftwareApp.Vatis => new(installation.UpdaterPath ?? throw new InvalidDataException("Missing vATIS updater."),
            ["--silent", "apply", "--norestart", "--package", packagePath], false),
        _ => throw new InvalidDataException("Unsupported product.")
    };

    private void Revalidate(SoftwareInstallation installation)
    {
        var current = Inspect(installation.App, installation.ExePath);
        if (!installation.CanUpdate || current != installation)
            throw new InvalidDataException("The installed application changed or is no longer recognized. Check for updates again.");
    }

    private void RequireStopped(SoftwareInstallation installation)
    {
        if (IsRunning(installation)) throw new IOException("Close the application in every Windows session before updating it.");
    }

    private async Task VerifyBackupSnapshot(BackupReceipt receipt, CancellationToken cancellationToken)
    {
        if (!Inventory(receipt.Original.RootPath).SequenceEqual(receipt.Files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase))
            throw new IOException("The vATIS installation changed while creating its backup. Check and try again.");
        foreach (var entry in receipt.Files)
        foreach (var path in new[] { Path.Combine(receipt.Original.RootPath, entry.RelativePath), Path.Combine(receipt.BackupFolder, "Files", entry.RelativePath) })
        {
            RequireFile(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != entry.Length || Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) != entry.Sha256)
                throw new IOException("The vATIS installation or backup changed. A fresh backup is required.");
        }
    }

    internal static List<string> Inventory(string root)
    {
        RejectReparse(root);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var files = new List<string>();
        long length = 0;
        int entries = 0;
        while (pending.TryPop(out var folder))
        {
            if (folder.Depth > 24) throw new IOException("Installation backup exceeds the supported directory depth.");
            RejectReparse(folder.Path);
            foreach (var path in Directory.EnumerateFileSystemEntries(folder.Path))
            {
                if (++entries > MaximumFiles) throw new IOException("Installation backup exceeds the supported file count.");
                RejectReparse(path);
                if (Directory.Exists(path)) pending.Push((path, folder.Depth + 1));
                else
                {
                    if ((length += new FileInfo(path).Length) > MaximumBytes) throw new IOException("Installation backup exceeds 4 GiB; make a manual backup/update.");
                    files.Add(Path.GetRelativePath(root, path));
                }
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static void ValidateRelease(SoftwareRelease release)
    {
        if (release.Size <= 0 || release.Size > MaximumBytes || !Regex.IsMatch(release.Sha256, "\\A[0-9a-fA-F]{64}\\z") ||
            NormalizeVersion(release.Version) != release.Version || release.DownloadUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(release.DownloadUri.UserInfo) || !release.DownloadUri.IsDefaultPort)
            throw new InvalidDataException("Invalid release metadata.");
        if (release.App != SoftwareApp.Vatis && release.Version.Contains('-') ||
            release.App == SoftwareApp.Vacs && !release.Version.StartsWith("2.", StringComparison.Ordinal))
            throw new InvalidDataException("This release generation/channel has not been verified for background installation.");
        var expected = release.App switch
        {
            SoftwareApp.Vatis => $"{VatisId}-{release.Version}-full.nupkg",
            SoftwareApp.TrackAudio => $"trackaudio-{release.Version}-x64-setup.exe",
            SoftwareApp.Vacs => $"vacs_{release.Version}_x64-setup.exe",
            _ => ""
        };
        if (release.FileName != expected || !release.DownloadUri.AbsolutePath.EndsWith("/" + expected, StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected official installer filename.");
        var expectedPrefix = release.App switch
        {
            SoftwareApp.Vatis => "https://vatis.app/updates/windows/",
            SoftwareApp.TrackAudio => "https://github.com/pierr3/TrackAudio/releases/download/",
            _ => "https://github.com/vacs-project/vacs/releases/download/"
        };
        if (!release.DownloadUri.AbsoluteUri.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(release.DownloadUri.Query) || !string.IsNullOrEmpty(release.DownloadUri.Fragment))
            throw new InvalidDataException("The release is not from the supported official distribution source.");
    }

    private static Manifest ReadManifestFile(string path)
    {
        RequireFile(path);
        using var stream = File.OpenRead(path);
        return ReadManifest(stream);
    }
    private static Manifest ReadManifestEntry(ZipArchive zip, string name)
    {
        using var stream = (zip.GetEntry(name) ?? throw new InvalidDataException("Missing vATIS package manifest.")).Open();
        return ReadManifest(stream);
    }
    private static Manifest ReadManifest(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
        var document = XDocument.Load(reader);
        var metadata = document.Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "metadata") ?? throw new InvalidDataException("Invalid package manifest.");
        string Value(string key) => metadata.Elements().SingleOrDefault(e => e.Name.LocalName == key)?.Value ?? "";
        return new(Value("id"), Value("version"), Value("channel"), Value("mainExe"), Value("os"));
    }
    private static void RequireVatisManifest(Manifest manifest, string version)
    {
        if (manifest.Id != VatisId || manifest.Version != version || manifest.Channel != "win" || manifest.MainExe != "vATIS.exe" || manifest.Os != "win")
            throw new InvalidDataException("The vATIS package identity/version/channel does not match the installation.");
    }
    private static string VersionForMessage(string version) => version.Length <= 64 ? version : version[..64] + "…";

    internal static string NormalizeVersion(string value)
    {
        var match = Regex.Match(value.Trim(), "\\A[vV]?(?<v>[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?)(?:\\.0)?(?:\\+[0-9A-Za-z.-]+)?\\z");
        if (!match.Success) throw new InvalidDataException("The product version is not recognized.");
        return match.Groups["v"].Value;
    }
    private static bool IsNewer(string candidate, string installed) => SoftwareVersion.TryCompare(candidate, installed, out var comparison) && comparison > 0;
    internal static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("A local absolute installation path is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    internal static bool SamePath(string left, string right) => string.Equals(FullPath(left), FullPath(right), StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string root, string path) => SamePath(root, path) || FullPath(path).StartsWith(FullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void RejectReparse(string path)
    {
        var current = FullPath(path);
        while (current != null)
        {
            try
            {
                // Exists returns false for some dangling links. Inspect the entry itself instead.
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked/reparse-point installation and backup paths are not supported.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
    private static void RequireFile(string path)
    {
        RejectReparse(path);
        if (!File.Exists(path)) throw new IOException("A required installation or package file is missing.");
    }
    private static string SafeRelative(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrEmpty(normalized) || normalized.StartsWith('/') || normalized.Split('/').Any(p =>
                p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Unsafe package entry path.");
        return normalized;
    }
    private sealed record Manifest(string Id, string Version, string Channel, string MainExe, string Os);
    private sealed record BackupFile(string RelativePath, long Length, string Sha256);
    private sealed record BackupReceipt(SoftwareInstallation Original, string BackupFolder, List<BackupFile> Files);
}
