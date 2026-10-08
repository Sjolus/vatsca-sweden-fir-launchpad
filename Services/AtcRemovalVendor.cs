using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Runs recognized vendor uninstallers after the coordinator has reviewed the plan and completed any selected or required recovery export.</summary>
public sealed class AtcRemovalVendor
{
    private readonly AtcRemovalEnvironment _environment;
    private readonly SoftwareInstaller _software;
    public bool RestartRequired { get; private set; }
    public AtcRemovalVendor() : this(new AtcRemovalEnvironment()) { }
    internal AtcRemovalVendor(AtcRemovalEnvironment environment)
    {
        _environment = environment;
        _software = new SoftwareInstaller(environment.Software);
    }

    public async Task UninstallAsync(AtcRemovalTarget target, IProgress<string>? progress = null)
    {
        if (RestartRequired) throw new InvalidOperationException("Restart Windows before further removal.");
        var spec = target.VendorSpec ?? throw new InvalidOperationException("No recognized vendor uninstaller is available.");
        if (!target.CanRemoveApplication) throw new InvalidOperationException("This application cannot be removed automatically.");
        Revalidate(target, spec);
        string? stage = null;
        try
        {
            var executable = spec.UninstallerPath;
            if (spec.Kind == AtcRemovalVendorKind.Nsis)
            {
                // NSIS uninstallers otherwise self-copy and can exit before the child finishes. Run a
                // verified external copy with the documented final _?= directory, so this process is
                // the actual uninstaller and it can delete the original file inside the target root.
                var stagingRoot = Path.Combine(_environment.LocalAppData, "VatscaUpdateChecker", "Removal", "VendorStaging");
                SoftwareInstaller.RejectReparse(stagingRoot);
                Directory.CreateDirectory(stagingRoot);
                stage = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                executable = Path.Combine(stage, "uninstall.exe");
                using (var input = new FileStream(spec.UninstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output).ConfigureAwait(false);
                if (!AtcRemovalCatalog.Hash(executable).Equals(spec.UninstallerSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The vendor uninstaller changed while preparing removal.");
            }
            Revalidate(target, spec);
            progress?.Report("Running the recognized " + target.Name + " uninstaller. Do not close Launchpad until it finishes.");
            var command = BuildCommand(spec, executable);
            // Do not retain a file lock under a vendor's installation root: its uninstaller must remove
            // those files. The external NSIS copy is independent; vATIS deletes its own root/helper.
            using var stagedLock = stage == null ? null : new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            var exitCode = await _environment.RunAsync(command).ConfigureAwait(false);
            RestartRequired |= exitCode == 3010;
            if (exitCode == 1223) throw new IOException("Windows elevation was cancelled. Removal did not complete.");
            if (exitCode != 0 && !(spec.Kind == AtcRemovalVendorKind.Msi && exitCode == 3010))
                throw new IOException("The vendor uninstaller returned exit code " + exitCode + ". Keep the recovery copy and review the installation manually.");
            VerifyRemoved(target, spec);
            if (exitCode == 3010) progress?.Report("EuroScope was removed; Windows reports that a restart is required. Launchpad will not restart Windows.");
        }
        finally
        {
            if (stage != null)
            {
                // Only the exact temporary executable and now-empty directory are removed, never recursively.
                try
                {
                    SoftwareInstaller.RejectReparse(stage);
                    var path = Path.Combine(stage, "uninstall.exe");
                    SoftwareInstaller.RejectReparse(path);
                    if (File.Exists(path)) File.Delete(path);
                    Directory.Delete(stage);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
        }
    }

    internal static AtcRemovalCommand BuildCommand(AtcRemovalVendorSpec spec, string executable)
    {
        if (spec.Kind == AtcRemovalVendorKind.Msi)
        {
            if (!Guid.TryParseExact(spec.MsiProductCode, "B", out _) || spec.MsiScope is not ("CurrentUser" or "AllUsers"))
                throw new InvalidDataException("Invalid Windows Installer product identity or scope.");
            return new(executable, new[] { "/x", spec.MsiProductCode!, "/qn", "/norestart" }, spec.MsiScope == "AllUsers") { IsMsi = true };
        }
        var installation = spec.Installation ?? throw new InvalidDataException("The vendor installation identity is missing.");
        if (spec.Kind == AtcRemovalVendorKind.Velopack && installation.App == SoftwareApp.Vatis)
            return new(executable, new[] { "--silent", "uninstall" }, false);
        if (spec.Kind != AtcRemovalVendorKind.Nsis || installation.App == SoftwareApp.Vatis ||
            installation.Scope is not ("CurrentUser" or "AllUsers"))
            throw new InvalidDataException("Unsupported vendor removal command.");
        if (installation.App == SoftwareApp.TrackAudio && installation.Scope != "CurrentUser")
            throw new InvalidDataException("This TrackAudio installation scope is not supported.");
        var scope = installation.App == SoftwareApp.Vacs ? "/" + installation.Scope : "/currentuser";
        var arguments = installation.App == SoftwareApp.TrackAudio
            ? new[] { "/S", scope, "--updated" } // preserves data even if an older build opted into deletion
            : new[] { "/S", scope };
        return new(executable, arguments, installation.Scope == "AllUsers", installation.RootPath);
    }

    private void Revalidate(AtcRemovalTarget target, AtcRemovalVendorSpec spec)
    {
        AtcRemovalCatalog.RequireFile(spec.UninstallerPath);
        if (!AtcRemovalCatalog.Hash(spec.UninstallerPath).Equals(spec.UninstallerSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The vendor uninstaller changed since review. Review the plan again.");
        if (spec.Kind == AtcRemovalVendorKind.Msi)
        {
            if (target.Id != AtcRemovalApp.EuroScope || spec.MsiRootPath == null ||
                !SoftwareInstaller.SamePath(spec.UninstallerPath, Path.Combine(_environment.WindowsDirectory, "System32", "msiexec.exe")))
                throw new InvalidDataException("The Windows Installer route does not match EuroScope.");
            var exe = Path.Combine(SoftwareInstaller.FullPath(spec.MsiRootPath), "EuroScope.exe");
            AtcRemovalCatalog.RequireFile(exe);
            var identity = _environment.Software.ReadBinary(exe);
            var registrations = _environment.ReadMsiRegistrations().Where(r => r.ProductCode == spec.MsiProductCode && r.Scope == spec.MsiScope && AtcRemovalCatalog.MsiMatches(r, exe, identity, AtcRemovalCatalog.ComponentPath(r, _environment))).ToArray();
            if (!AtcRemovalCatalog.IsEuroScopeProduct(identity.ProductName) || registrations.Length != 1)
                throw new IOException("The matching EuroScope Windows Installer registration changed.");
            var footprint = _environment.ReadMsiFootprint(registrations[0], spec.MsiRootPath, _environment.RoamingAppData, _environment.WindowsDirectory);
            if (string.IsNullOrEmpty(spec.MsiFootprintFingerprint) || footprint.Fingerprint != spec.MsiFootprintFingerprint)
                throw new IOException("EuroScope's registered component paths changed. Review removal again.");
            if (_environment.IsProcessRunning("EuroScope")) throw new IOException("Close EuroScope in every Windows session before removal.");
            return;
        }
        var installation = spec.Installation ?? throw new InvalidDataException("The reviewed installation is missing.");
        var expectedId = installation.App switch { SoftwareApp.Vacs => AtcRemovalApp.Vacs, SoftwareApp.Vatis => AtcRemovalApp.Vatis, _ => AtcRemovalApp.TrackAudio };
        var expectedKind = installation.App == SoftwareApp.Vatis ? AtcRemovalVendorKind.Velopack : AtcRemovalVendorKind.Nsis;
        var expectedPath = installation.App switch
        {
            SoftwareApp.Vatis => installation.UpdaterPath!,
            SoftwareApp.Vacs => Path.Combine(installation.RootPath, "uninstall.exe"),
            _ => Path.Combine(installation.RootPath, "Uninstall TrackAudio.exe")
        };
        if (target.Id != expectedId || spec.Kind != expectedKind || spec.RemovesData != (installation.App == SoftwareApp.Vatis) ||
            !SoftwareInstaller.SamePath(expectedPath, spec.UninstallerPath) || _software.Inspect(installation.App, installation.ExePath) != installation)
            throw new IOException("The recognized installation changed since review.");
        if (_software.IsRunning(installation)) throw new IOException("Close " + target.Name + " in every Windows session before removal.");
    }

    private void VerifyRemoved(AtcRemovalTarget target, AtcRemovalVendorSpec spec)
    {
        if (spec.Kind == AtcRemovalVendorKind.Msi)
        {
            if (_environment.ReadMsiRegistrations().Any(r => r.ProductCode == spec.MsiProductCode))
                throw new IOException("Windows still reports this EuroScope MSI as installed. Keep the recovery copy and complete removal manually.");
            return;
        }
        var installation = spec.Installation!;
        var main = installation.App == SoftwareApp.Vatis ? Path.Combine(installation.RootPath, "current", "vATIS.exe") : installation.ExePath;
        if (File.Exists(main) || _software.IsRunning(installation))
            throw new IOException(target.Name + " still appears installed or running after the uninstaller finished. Keep the recovery copy and review manually.");
    }

    internal static async Task<int> RunNativeAsync(AtcRemovalCommand command)
    {
        var info = BuildStartInfo(command);
        Process? process;
        try { process = Process.Start(info); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return 1223; }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740 && !command.Elevate)
        { throw new IOException("This per-user uninstaller requires elevation. Use Windows Installed apps to remove it under the intended account.", ex); }
        if (process == null) throw new IOException("Windows did not provide an uninstaller process to monitor.");
        using (process)
        {
            // No cancellation, timeout, process-tree adoption or forced termination after uninstall starts.
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }

    internal static ProcessStartInfo BuildStartInfo(AtcRemovalCommand command)
    {
        var arguments = SoftwareInstallerNative.FormatArguments(command.Arguments, command.IsMsi);
        if (command.NsisInstallDirectory is { } root)
        {
            if (command.IsMsi) throw new InvalidDataException("An MSI command cannot contain an NSIS installation directory.");
            root = SoftwareInstaller.FullPath(root);
            if (root.Any(c => c is '"' or '\r' or '\n')) throw new InvalidDataException("The NSIS installation directory contains unsupported characters.");
            // NSIS consumes the remaining command line after _?=; it must be last and must not be quoted.
            arguments += " _?=" + root;
        }
        return new ProcessStartInfo
        {
            FileName = command.Executable, Arguments = arguments, WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = command.Elevate, Verb = command.Elevate ? "runas" : "",
            CreateNoWindow = !command.IsMsi, WindowStyle = command.IsMsi ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
        };
    }
}
