using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Explicit MSI installation for the reviewed all-users VatEFS layout. Never starts or stops ATC clients.</summary>
internal sealed class VatEfsInstaller
{
    private readonly VatEfsInstallerEnvironment _environment;
    private BackupReceipt? _backup;
    public bool RestartRequired { get; private set; }

    internal VatEfsInstaller() : this(new()) { }
    internal VatEfsInstaller(VatEfsInstallerEnvironment environment) => _environment = environment;

    internal static string? GetBlockingReason() => BlockingReason(EuroScopeProcessGuard.GetBlockingReason, VatEfsInstallerEnvironment.AnyBackendProcess);

    private static string? BlockingReason(Func<string?> euroScope, Func<bool> backend)
    {
        try
        {
            return euroScope() ?? (backend() ? "VatEFS is running. Close EuroScope and the VatEFS backend before installing or updating VatEFS." : null);
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or SecurityException)
        {
            return "Launchpad could not verify that EuroScope and the VatEFS backend are closed. Close both and try again.";
        }
    }

    public bool IsRunning(SoftwareInstallation installation) => BlockingReason(_environment.EuroScopeBlockingReason, _environment.BackendRunning) != null;

    public SoftwareInstallation Inspect(string exePath)
    {
        try
        {
            string exe = FullPath(exePath), root = Path.GetDirectoryName(exe)!;
            if (!Same(exe, Path.Combine(StandardRoot, "efs.exe")))
                throw new InvalidDataException("Background VatEFS installation supports its standard all-users Program Files folder. Use the official installer for this custom copy.");
            var proof = ReadFootprint(root);
            RequireInstalledFiles(root);
            // The MSI product code is an identity marker here, never an executable helper path.
            return new(SoftwareApp.VatEfs, exe, proof.Package.Version, root, "AllUsers", proof.Package.ProductCode, true, null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // A stricter installer-layout failure must not discard a version that the existing
            // read-only row probe can still establish for this exact configured folder.
            string? folder = null;
            try { folder = Path.GetDirectoryName(FullPath(exePath)); }
            catch (Exception pathError) when (pathError is IOException or ArgumentException or NotSupportedException) { }
            var detected = VatEfsInstallationProbe.Read(folder ?? "", _environment.ReadRegistrations, File.Exists, path =>
            {
                try { RejectReparse(path); return true; }
                catch (Exception pathError) when (pathError is IOException or UnauthorizedAccessException or SecurityException) { return false; }
            });
            return new(SoftwareApp.VatEfs, exePath, detected.Version ?? "", folder ?? "", detected.Version != null ? "AllUsers" : "", null, false, error.Message);
        }
    }

    public void ValidateFreshDestination(string root)
    {
        RequireReady();
        root = FullPath(root);
        if (!Same(root, StandardRoot)) throw new InvalidDataException("VatEFS must be installed in its standard all-users Program Files folder.");
        RejectReparse(root);
        if (_environment.ReadRegistrations().Count != 0 || EntryExists(root))
            throw new InvalidDataException("VatEFS or files left by an earlier installation already exist. Adopt the existing installation or resolve those files before a fresh install.");
    }

    public async Task VerifyPackageAsync(SoftwareRelease release, string packagePath, CancellationToken cancellationToken)
    {
        RequireReady();
        ValidateRelease(release);
        RequireFile(packagePath);
        await using var package = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (package.Length != release.Size || !Convert.ToHexString(await SHA256.HashDataAsync(package, cancellationToken).ConfigureAwait(false)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The VatEFS MSI does not match the official release size and SHA-256.");
        var metadata = _environment.PackageMetadata(packagePath);
        if (metadata.Version != release.Version)
            throw new InvalidDataException("The VatEFS MSI product version does not match the reviewed release.");
        cancellationToken.ThrowIfCancellationRequested();
        RequireReady();
    }

    public async Task<string?> BackupAsync(SoftwareInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        _backup = null;
        RequireReady(); Revalidate(installation);
        var proof = ReadFootprint(installation.RootPath);
        var plan = RemovalFileService.Preview([installation.RootPath]);
        RequireReady();
        string container = FullPath(Path.Combine(_environment.LocalAppData, "VatscaUpdateChecker", "SoftwareUpdates", "VatEfsRecovery", Guid.NewGuid().ToString("N")));
        if (Within(installation.RootPath, container) || Within(container, installation.RootPath))
            throw new InvalidDataException("VatEFS recovery must be outside the installation.");
        RejectReparse(container);
        _environment.CreatePrivateDirectory(container);
        Report(progress, "Backing up all VatEFS program files and local configuration…");
        string folder = await RemovalFileService.BackupAsync(plan, container, new GuardedProgress(this, progress), cancellationToken).ConfigureAwait(false);
        RequireReady(); Revalidate(installation);
        if (ReadFootprint(installation.RootPath).Fingerprint != proof.Fingerprint)
            throw new IOException("The VatEFS MSI registration changed during backup. Review the installation again.");
        RemovalFileService.Verify(plan); RemovalFileService.VerifyBackup(plan, folder);
        cancellationToken.ThrowIfCancellationRequested(); RequireReady();
        _backup = new(installation, proof.Fingerprint, plan, folder);
        return folder;
    }

    public async Task<SoftwareInstallResult> InstallAsync(SoftwareInstallation installation, SoftwareRelease release,
        string packagePath, IProgress<string>? progress)
    {
        RequireReady(); Revalidate(installation);
        if (!SoftwareVersion.TryCompare(release.Version, installation.Version, out int comparison) || comparison <= 0)
            throw new InvalidDataException("Only a newer reviewed VatEFS version may be installed as an update.");
        if (_backup == null || _backup.Installation != installation)
            throw new InvalidDataException("A completed VatEFS recovery backup is required before updating.");
        using var paths = new VatEfsMsiPathLease(packagePath, _environment.MsiexecPath);
        await using var package = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await VerifyPackageAsync(release, packagePath, CancellationToken.None).ConfigureAwait(false);
        var metadata = _environment.PackageMetadata(packagePath);
        VerifyBackup(installation);
        Report(progress, "Installing VatEFS in the background. Keep EuroScope and VatEFS closed…");
        VerifyBackup(installation); RequireReady();
        return await RunAsync(release, packagePath, installation.RootPath, metadata, progress).ConfigureAwait(false);
    }

    public async Task<SoftwareInstallResult> InstallFreshAsync(SoftwareRelease release, string packagePath, string root,
        IProgress<string>? progress, CancellationToken cancellationToken, Func<Task> beforeInstall)
    {
        ValidateFreshDestination(root);
        using var paths = new VatEfsMsiPathLease(packagePath, _environment.MsiexecPath);
        await using var package = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await VerifyPackageAsync(release, packagePath, cancellationToken).ConfigureAwait(false);
        var metadata = _environment.PackageMetadata(packagePath);
        cancellationToken.ThrowIfCancellationRequested(); ValidateFreshDestination(root);
        await beforeInstall().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); ValidateFreshDestination(root);
        Report(progress, "Installing VatEFS in the background. Keep EuroScope and VatEFS closed…");
        cancellationToken.ThrowIfCancellationRequested(); ValidateFreshDestination(root);
        return await RunAsync(release, packagePath, FullPath(root), metadata, progress).ConfigureAwait(false);
    }

    private async Task<SoftwareInstallResult> RunAsync(SoftwareRelease release, string packagePath, string root,
        VatEfsMsiPackage metadata, IProgress<string>? progress)
    {
        string folder = FullPath(Path.Combine(_environment.LocalAppData, "VatscaUpdateChecker", "SoftwareUpdates", "VatEfsLogs", Guid.NewGuid().ToString("N")));
        RejectReparse(folder);
        _environment.CreatePrivateDirectory(folder);
        string log = Path.Combine(folder, "install.log");
        using var logs = new VatEfsMsiPathLease(log);
        try { return await ApplyMsiAsync(release, packagePath, root, metadata, progress, log).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception)
        {
            throw new IOException(error.Message + "\nWindows Installer diagnostic log: " + log, error);
        }
    }

    private async Task<SoftwareInstallResult> ApplyMsiAsync(SoftwareRelease release, string packagePath, string root,
        VatEfsMsiPackage metadata, IProgress<string>? progress, string log)
    {
        RequireFile(_environment.MsiexecPath);
        using var executable = new FileStream(_environment.MsiexecPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _environment.VerifyMsiexec(_environment.MsiexecPath);
        RejectReparse(packagePath); RejectReparse(root); RequireReady();
        var elapsed = Stopwatch.StartNew();
        Report(progress, "Starting Windows Installer. Approve the Windows prompt if shown. Diagnostic log: " + log);
        using var timer = new PeriodicTimer(_environment.InstallationProgressInterval);
        var operation = _environment.RunAsync(BuildCommand(_environment.MsiexecPath, packagePath, root, log));
        // Once native installation starts, a progress callback or timer must never abandon it.
        while (!operation.IsCompleted)
        {
            var tick = timer.WaitForNextTickAsync().AsTask();
            if (await Task.WhenAny(operation, tick).ConfigureAwait(false) == operation) break;
            if (!await tick.ConfigureAwait(false)) break;
            try
            {
                progress?.Report($"Waiting for Windows Installer… {elapsed.Elapsed:mm\\:ss} elapsed. Keep Launchpad open. " +
                    "Check for any Windows approval or installer message. Diagnostic log: " + log);
            }
            catch { /* Display failures must not interrupt the native operation. */ }
        }
        int exit = await operation.ConfigureAwait(false);
        RestartRequired |= exit == 3010;
        if (exit == 1223) throw new IOException("Windows permission approval was cancelled; VatEFS installation was not completed.");
        if (exit is not (0 or 3010)) throw new IOException($"The VatEFS installer returned exit code {exit}. Keep any recovery backup and review the installed version before retrying.");
        // Latch 3010 before inspecting output: an incomplete postcheck must still require a restart.
        var after = Inspect(Path.Combine(root, "efs.exe"));
        if (!after.CanUpdate || after.Version != release.Version || !Same(after.RootPath, root) || after.UpdaterPath != metadata.ProductCode)
            throw new IOException("VatEFS installation finished, but its expected version, destination and MSI registration could not be verified. Keep any recovery backup" + (RestartRequired ? " and restart Windows before continuing." : "."));
        // MSI may retain locally edited, unversioned data/configuration. Verify the two binaries
        // against the package's file sizes without treating retained personal data as a failure.
        foreach (var file in metadata.Components.SelectMany(c => c.Files).Where(f => f.RelativePath is "efs.exe" or "VatEFS.dll"))
        {
            string installed = Path.Combine(root, file.RelativePath);
            RequireFile(installed);
            if (new FileInfo(installed).Length != file.Length)
                throw new IOException("A VatEFS installed file does not match the reviewed MSI. Keep any recovery backup" + (RestartRequired ? " and restart Windows before continuing." : "."));
        }
        if (IsRunning(after)) throw new IOException("EuroScope or VatEFS started during installation. Launchpad did not stop it; check the installation before continuing.");
        _backup = null;
        return new(after.ExePath);
    }

    internal static SoftwareInstallCommand BuildCommand(string msiexec, string packagePath, string root, string? logPath = null) =>
        new(msiexec, ["/i", packagePath, "/qn", "/norestart", "REBOOT=ReallySuppress", "ALLUSERS=1", "INSTALLDIR=" + root,
            "MSIRESTARTMANAGERCONTROL=Disable", "MSIDISABLERMRESTART=1", .. logPath == null ? Array.Empty<string>() : ["/L*V!", logPath]], true)
        { IsMsi = true };

    private string StandardRoot => FullPath(Path.Combine(_environment.ProgramFiles, "VatEFS"));

    private void VerifyBackup(SoftwareInstallation installation)
    {
        RequireReady(); Revalidate(installation);
        if (_backup == null || _backup.Installation != installation || ReadFootprint(installation.RootPath).Fingerprint != _backup.Fingerprint)
            throw new IOException("The VatEFS installation changed after its recovery backup. Review and back it up again.");
        RemovalFileService.Verify(_backup.Files); RemovalFileService.VerifyBackup(_backup.Files, _backup.Folder);
        RequireReady();
    }

    private void Revalidate(SoftwareInstallation installation)
    {
        if (!installation.CanUpdate || installation.App != SoftwareApp.VatEfs || Inspect(installation.ExePath) != installation)
            throw new InvalidDataException("The VatEFS installation changed or is not a supported MSI installation. Check again.");
    }

    private Footprint ReadFootprint(string root)
    {
        root = FullPath(root); RejectReparse(root);
        if (!Same(root, StandardRoot)) throw new InvalidDataException("Only VatEFS's standard all-users installation is supported.");
        var products = _environment.ReadRegistrations();
        if (products.Count != 1) throw new InvalidDataException("A single registered VatEFS installation is required. Unknown or multiple copies need manual review.");
        var product = products[0];
        if (!Guid.TryParseExact(product.ProductCode, "B", out _) || product.Name != "VatEFS" || product.Publisher != "Martin Insulander" ||
            !VatEfsMsiPackageReader.IsVersion(product.Version) || product.ReadmeComponentPath == null || !Same(product.ReadmeComponentPath, Path.Combine(root, "README.txt")))
            throw new InvalidDataException("The registered VatEFS identity or location does not match the selected folder.");
        string cache = FullPath(_environment.CachedPackage(product.ProductCode) ?? throw new InvalidDataException("VatEFS's registered MSI cache is missing."));
        if (!Same(Path.GetDirectoryName(cache)!, Path.Combine(_environment.WindowsDirectory, "Installer")) || !Path.GetExtension(cache).Equals(".msi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The registered VatEFS MSI is outside the Windows Installer cache.");
        RequireFile(cache);
        using var package = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (package.Length is 0 or > 512L * 1024 * 1024) throw new InvalidDataException("The cached VatEFS MSI has an unsupported size.");
        string hash = Convert.ToHexString(SHA256.HashData(package));
        var metadata = _environment.PackageMetadata(cache);
        if (!metadata.ProductCode.Equals(product.ProductCode, StringComparison.OrdinalIgnoreCase) || metadata.Version != product.Version)
            throw new InvalidDataException("VatEFS's cached MSI does not match its registered product and version.");
        var evidence = new List<string> { root.ToUpperInvariant(), product.ProductCode.ToUpperInvariant(), product.Version, cache.ToUpperInvariant(), hash };
        foreach (var component in metadata.Components)
        {
            if (component.KeyPath == null) continue; // The only accepted registry component is the reviewed firewall marker.
            string expected = FullPath(Path.Combine(root, component.KeyPath));
            string actual = _environment.ComponentPath(product.ProductCode, component.Id) ?? throw new InvalidDataException("A VatEFS MSI file destination could not be verified.");
            if (!Same(actual, expected)) throw new InvalidDataException("A VatEFS MSI component points outside its reviewed destination. Use its official installer manually.");
            RequireFile(actual);
            evidence.Add(component.Id.ToUpperInvariant() + "|" + expected.ToUpperInvariant());
            foreach (var file in component.Files) RejectReparse(Path.Combine(root, file.RelativePath));
        }
        return new(metadata, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', evidence)))));
    }

    private void RequireInstalledFiles(string root)
    {
        RequireFile(Path.Combine(root, "README.txt"));
        foreach (string name in new[] { "efs.exe", "VatEFS.dll" })
        {
            string path = Path.Combine(root, name); RequireFile(path);
            _environment.VerifyInstalledBinary(path, name == "VatEFS.dll");
        }
    }

    private void RequireReady()
    {
        if (RestartRequired) throw new InvalidOperationException("Restart Windows before another VatEFS installation or update.");
        if (BlockingReason(_environment.EuroScopeBlockingReason, _environment.BackendRunning) is { } problem) throw new InvalidOperationException(problem);
    }

    private void Report(IProgress<string>? progress, string message)
    {
        RequireReady(); progress?.Report(message); RequireReady();
    }

    internal static void ValidateRelease(SoftwareRelease release)
    {
        if (release.App != SoftwareApp.VatEfs || !VatEfsMsiPackageReader.IsVersion(release.Version) ||
            release.Size is <= 0 or > 512L * 1024 * 1024 || release.Sha256.Length != 64 || release.Sha256.Any(c => !Uri.IsHexDigit(c)) ||
            release.FileName != "vatefs-" + release.Version + ".msi" ||
            release.DownloadUri.AbsoluteUri != "https://github.com/minsulander/vatefs/releases/download/v" + release.Version + "/" + release.FileName)
            throw new InvalidDataException("The VatEFS release metadata is not a supported official Windows MSI.");
    }

    internal static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Length > 32700 || path.AsSpan(2).Contains(':') || path.Contains('"') ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Skip(1).Any(p => p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("VatEFS requires ordinary absolute local paths without redirects or alternate streams.");
        string result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (result.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(result)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A drive root cannot be a VatEFS file destination.");
        return result;
    }
    private static bool Same(string a, string b) => FullPath(a).Equals(FullPath(b), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string parent, string child) => Same(parent, child) || FullPath(child).StartsWith(FullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void RejectReparse(string path)
    {
        for (string? value = FullPath(path); value != null; value = Path.GetDirectoryName(value))
        {
            try { if ((File.GetAttributes(value) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked VatEFS package, installation and recovery paths are not supported."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static void RequireFile(string path)
    {
        RejectReparse(path);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("A required VatEFS installation or package file is missing.");
    }
    private sealed record Footprint(VatEfsMsiPackage Package, string Fingerprint);
    private sealed record BackupReceipt(SoftwareInstallation Installation, string Fingerprint, RemovalFilePlan Files, string Folder);
    private sealed class GuardedProgress(VatEfsInstaller owner, IProgress<string>? progress) : IProgress<string>
    {
        public void Report(string value) => owner.Report(progress, "Backing up VatEFS files…");
    }
}

internal sealed class VatEfsInstallerEnvironment
{
    public string ProgramFiles { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string WindowsDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public string MsiexecPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
    public Func<IReadOnlyList<VatEfsMsiRegistration>> ReadRegistrations { get; init; } = VatEfsInstallationProbe.ReadRegistrations;
    public Func<string, string?> CachedPackage { get; init; } = code => VatEfsMsiPackageReader.ProductProperty(code, "LocalPackage");
    public Func<string, VatEfsMsiPackage> PackageMetadata { get; init; } = VatEfsMsiPackageReader.Read;
    public Func<string, string, string?> ComponentPath { get; init; } = VatEfsMsiPackageReader.ComponentPath;
    public Func<string?> EuroScopeBlockingReason { get; init; } = EuroScopeProcessGuard.GetBlockingReason;
    public Func<bool> BackendRunning { get; init; } = AnyBackendProcess;
    public Action<string, bool> VerifyInstalledBinary { get; init; } = VerifyPe;
    public Action<string> VerifyMsiexec { get; init; } = VatEfsMsiTrust.VerifySystemInstaller;
    public Action<string> CreatePrivateDirectory { get; init; } = CreateRecoveryDirectory;
    public Func<SoftwareInstallCommand, Task<int>> RunAsync { get; init; } = SoftwareInstallerNative.RunAsync;
    public TimeSpan InstallationProgressInterval { get; init; } = TimeSpan.FromSeconds(10);

    internal static bool AnyBackendProcess()
    {
        var processes = Process.GetProcessesByName("efs");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static void VerifyPe(string path, bool plugin)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var input = new BinaryReader(stream);
        if (stream.Length < 256 || input.ReadUInt16() != 0x5a4d) throw new InvalidDataException("The registered VatEFS binary is not a Windows program.");
        stream.Position = 0x3c; uint pe = input.ReadUInt32();
        if (pe > stream.Length - 24 || pe < 64) throw new InvalidDataException("The registered VatEFS PE header is invalid.");
        stream.Position = pe;
        if (input.ReadUInt32() != 0x00004550 || input.ReadUInt16() != (plugin ? 0x14c : 0x8664))
            throw new InvalidDataException("The VatEFS binary architecture does not match the supported package.");
        stream.Position = pe + 22;
        if (((input.ReadUInt16() & 0x2000) != 0) != plugin) throw new InvalidDataException("The VatEFS plugin/backend file type is incorrect.");
    }

    private static void CreateRecoveryDirectory(string path)
    {
        VatEfsInstaller.RejectReparse(path);
        // A unique child avoids changing permissions on a directory owned by another operation.
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using var user = WindowsIdentity.GetCurrent();
        var sid = user.User ?? throw new InvalidOperationException("Windows account identity is unavailable for the private backup.");
        foreach (var account in new[] { sid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
        VatEfsInstaller.RejectReparse(path);
    }
}
