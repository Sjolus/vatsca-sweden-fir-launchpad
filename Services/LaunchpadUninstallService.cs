using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Velopack.Locators;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Registers the reviewed uninstall UI; only Uninstall starts native removal.</summary>
public static class LaunchpadUninstallService
{
    internal const string PackageId = "SwedenFirLaunchpad";
    internal const string MainExeName = "VatscaUpdateChecker.exe";
    internal const string UninstallArgument = "--uninstall-launchpad";
    internal const string FinalizerArgument = "--finalize-uninstall-registration";
    internal const string UninstallHelperArgument = "--complete-launchpad-uninstall";
    private const string Title = "Sweden FIR Launchpad";
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + PackageId;
    private static readonly UninstallEnvironment EnvironmentAccess = new();

    public static bool IsUninstallRequested(string[] args) => args.Length == 1 && args[0] == UninstallArgument;
    internal static bool IsFinalizerRequested(string[] args) => args.Length == 2 && args[0] == FinalizerArgument;
    internal static bool IsUninstallHelperRequested(string[] args) => args.Length == 2 && args[0] == UninstallHelperArgument;

    public static bool CanUninstall(out string? reason) => CanUninstall(EnvironmentAccess, out reason);
    internal static bool CanUninstall(UninstallEnvironment environment, out string? reason)
    {
        try { Validate(environment.Locate(), environment); reason = null; return true; }
        catch (Exception ex) when (ExpectedFailure(ex)) { reason = ex.Message; return false; }
    }

    public static bool TryRegister(out string? error) => TryRegister(EnvironmentAccess, out error);
    internal static bool TryRegister(UninstallEnvironment environment, out string? error)
    {
        try
        {
            var installation = Validate(environment.Locate(), environment);
            Register(installation, environment);
            CleanupCompletedFinalizers(environment.StorageRoot);
            error = null;
            return true;
        }
        catch (Exception ex) when (ExpectedFailure(ex)) { error = ex.Message; return false; }
    }

    /// <summary>Native removal deletes the complete managed root, outside the selected ATC inventory.</summary>
    public static void ValidateConfiguredPaths(AppSettings settings, params string?[] recoveryPaths) =>
        ValidateConfiguredPaths(settings, EnvironmentAccess, recoveryPaths);

    internal static void ValidateConfiguredPaths(AppSettings settings, UninstallEnvironment environment, params string?[] recoveryPaths)
    {
        var installation = Validate(environment.Locate(), environment);
        ValidateConfiguredPaths(settings, installation.Root, recoveryPaths);
    }

    private static void ValidateConfiguredPaths(AppSettings settings, string managedRoot, string?[] recoveryPaths)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var paths = new (string Label, string? Path)[]
        {
            ("EuroScope executable", settings.EuroscopeExePath),
            ("EuroScope/GNG data", settings.EuroscopeDataPath),
            ("last EuroScope profile", settings.LastEuroscopeProfile),
            ("TrackAudio executable", settings.TrackAudioExePath),
            ("VACS executable", settings.VacsExePath),
            ("vATIS executable", settings.VatisExePath),
            ("VatEFS path", settings.VatEfsPath)
        }.Concat(recoveryPaths.Select(path => ("recovery export", path)));
        string root = PreservationPath.Normalize(managedRoot);
        string resolvedRoot = PreservationPath.Resolve(root);
        foreach (var (label, configured) in paths)
        {
            if (string.IsNullOrWhiteSpace(configured)) continue;
            var path = PreservationPath.Normalize(configured);
            // Also block stale paths: a missing executable does not prove that its parent/data is gone.
            if (Inside(root, path) || Inside(resolvedRoot, PreservationPath.Resolve(path)))
                throw new InvalidOperationException($"Launchpad uninstall would also remove the configured {label}: {configured}. " +
                    "Move it outside Launchpad's installation folder and update its setting, or export/remove it manually and clear the stale setting before uninstalling. Choose a recovery folder outside that installation.");
        }
        static bool Inside(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static void Uninstall(AppSettings settings, string? recoveryFolder = null) =>
        Uninstall(EnvironmentAccess, MaintenanceLock.IsHeldByCurrentProcess, settings, recoveryFolder);

    // Kept only for the existing synthetic lifecycle tests; production callers must provide settings.
    internal static void Uninstall(UninstallEnvironment environment, bool maintenanceHeld)
        => Uninstall(environment, maintenanceHeld, new AppSettings());

    internal static void Uninstall(UninstallEnvironment environment, bool maintenanceHeld, AppSettings settings, string? recoveryFolder = null)
    {
        if (!maintenanceHeld) throw new InvalidOperationException("Acquire the maintenance lock before uninstalling Launchpad.");
        var installation = Validate(environment.Locate(), environment);
        ValidateConfiguredPaths(settings, installation.Root, [recoveryFolder]);
        // The outside-root helper holds the handoff gate until native removal finishes.
        // Return only once it is ready; the caller then shuts down without restoring normal controls.
        environment.PrepareHandoff(installation);
    }

    internal static void CompleteUninstall(RegistrationTicket ticket, UninstallEnvironment environment)
    {
        var installation = ValidatePreparedInstallation(ticket, environment);
        environment.RunNativeUninstall(new ProcessStartInfo
        {
            FileName = installation.Updater,
            UseShellExecute = false,
            WorkingDirectory = installation.Root,
            ArgumentList = { "--uninstall" }
        });
    }

    private static ManagedInstallation Validate(ManagedInstallation? installation, UninstallEnvironment environment)
    {
        if (installation is null || installation.Portable || installation.AppId != PackageId)
            throw new InvalidOperationException("This copy is portable or is not a recognized Launchpad installation.");
        var root = LocalPath(installation.Root);
        var executable = Path.Combine(root, "current", MainExeName);
        var updater = Path.Combine(root, "Update.exe");
        if (!SamePath(installation.Executable, executable) || !SamePath(installation.Updater, updater))
            throw new InvalidDataException("The Launchpad executable is outside its managed installation.");
        foreach (var path in new[] { root, executable, updater, Path.Combine(root, "current", "sq.version") }) RejectReparse(path);
        if (!File.Exists(executable) || !File.Exists(updater)) throw new InvalidDataException("The Launchpad installation is incomplete.");
        if (File.Exists(Path.ChangeExtension(executable, ".runtimeconfig.json")))
            throw new InvalidOperationException("This installation is not a self-contained Launchpad package. Use its native Windows uninstaller.");
        ValidateManifest(Path.Combine(root, "current", "sq.version"), installation.Version);
        var registration = environment.ReadRegistration() ?? throw new InvalidDataException("Launchpad's Windows uninstall registration is missing.");
        if (!SamePath(registration.InstallLocation, root) || registration.DisplayName != Title ||
            registration.QuietUninstallString != NativeCommand(updater, quiet: true) ||
            (registration.UninstallString != NativeCommand(updater) && registration.UninstallString != WrapperCommand(executable)))
            throw new InvalidDataException("Launchpad's Windows uninstall registration does not match this installation.");
        return installation with { Root = root, Executable = executable, Updater = updater };
    }

    private static void Register(ManagedInstallation installation, UninstallEnvironment environment) =>
        environment.WriteCommands(WrapperCommand(installation.Executable), NativeCommand(installation.Updater, quiet: true));

    internal static string WrapperCommand(string executable) => QuotePath(executable) + " " + UninstallArgument;
    internal static string NativeCommand(string updater, bool quiet = false) => QuotePath(updater) + " --uninstall" + (quiet ? " --silent" : "");
    private static string QuotePath(string path) => '"' + LocalPath(path) + '"';

    internal static void ValidateManifest(string path, string expectedVersion)
    {
        if (!SoftwareVersion.TryParse(expectedVersion, out _)) throw new InvalidDataException("The Launchpad package version is invalid.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 65536) throw new InvalidDataException("The Launchpad installation manifest is missing or invalid.");
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
        var document = XDocument.Load(reader);
        var metadata = document.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata");
        string? Field(string name) => metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == name)?.Value;
        if (document.Root?.Name.LocalName != "package" || Field("id") != PackageId || Field("version") != expectedVersion)
            throw new InvalidDataException("The installed Launchpad manifest does not match this package.");
    }

    // Native Setup writes its registration AFTER the install hook, then force-stops hook children
    // inside the install root. The finalizer therefore runs a copied self-contained executable outside it.
    public static void ScheduleRegistrationAfterSetup()
    {
        var installation = EnvironmentAccess.Locate();
        if (installation is null || installation.Portable || installation.AppId != PackageId) return;
        var root = LocalPath(installation.Root);
        var main = Path.Combine(root, "current", MainExeName);
        if (!SamePath(main, installation.Executable) || !SamePath(main, Environment.ProcessPath ?? "")) return;
        ValidateManifest(Path.Combine(root, "current", "sq.version"), installation.Version);
        RejectReparse(main);
        // This mechanism is only for our self-contained single-file installer builds.
        if (File.Exists(Path.ChangeExtension(main, ".runtimeconfig.json"))) return;
        using var parent = Process.GetProcessById(ParentProcessId());
        ScheduleHelper(installation, parent, FinalizerArgument);
    }

    private static void ScheduleUninstall(ManagedInstallation installation)
    {
        // Portable/framework-dependent builds cannot run independently after removal of their root.
        if (File.Exists(Path.ChangeExtension(installation.Executable, ".runtimeconfig.json")))
            throw new InvalidOperationException("This installation is not a self-contained Launchpad package. Use its native Windows uninstaller.");
        using var parent = Process.GetCurrentProcess();
        ScheduleHelper(installation, parent, UninstallHelperArgument);
    }

    private static void ScheduleHelper(ManagedInstallation installation, Process parent, string argument)
    {
        string main = installation.Executable;
        _ = parent.SafeHandle; // Open the actual parent now; capture its creation identity, not just its PID.
        long parentStarted = parent.StartTime.ToUniversalTime().Ticks;
        var folder = Path.Combine(EnvironmentAccess.StorageRoot, Guid.NewGuid().ToString("N"));
        RejectReparse(folder);
        Directory.CreateDirectory(folder);
        var copiedExe = Path.Combine(folder, MainExeName);
        string hash;
        using (var source = new FileStream(main, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using (var destination = new FileStream(copiedExe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(destination);
            source.Position = 0;
            hash = Convert.ToHexString(SHA256.HashData(source));
        }
        string readyName = @"Local\SwedenFirLaunchpad.Finalizer." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        using var execute = new EventWaitHandle(false, EventResetMode.ManualReset, readyName + ".Execute");
        var ticket = new RegistrationTicket(installation.Root, installation.Version, hash, parent.Id, parentStarted, readyName, DateTime.UtcNow.AddMinutes(5));
        var ticketPath = Path.Combine(folder, "registration.json");
        File.WriteAllText(ticketPath, JsonSerializer.Serialize(ticket));
        var start = new ProcessStartInfo(copiedExe)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = folder, ArgumentList = { argument, ticketPath }
        };
        using var child = Process.Start(start) ?? throw new IOException("Could not prepare uninstall registration.");
        if (!ready.WaitOne(TimeSpan.FromSeconds(10)))
            throw new IOException("The Launchpad maintenance helper did not become ready. No native uninstall was requested.");
        execute.Set();
    }

    internal static int RunUninstallHelper(string ticketPath)
    {
        string? folder = null;
        try
        {
            var ticket = ReadOwnedTicket(ticketPath, out folder);
            using var caller = Process.GetProcessById(ticket.ParentId);
            _ = caller.SafeHandle;
            if (caller.StartTime.ToUniversalTime().Ticks != ticket.ParentStartedUtcTicks) return 1;
            ValidatePreparedInstallation(ticket, EnvironmentAccess);
            using var handoff = MaintenanceLock.BeginUninstallHandoff();
            using var execute = EventWaitHandle.OpenExisting(ticket.ReadyEvent + ".Execute");
            using (var ready = EventWaitHandle.OpenExisting(ticket.ReadyEvent)) ready.Set();
            if (!execute.WaitOne(TimeSpan.FromSeconds(10))) return 1;
            // The caller holds its lease until Shutdown. The handoff event prevents another process
            // acquiring it between caller exit and native removal; this copied helper survives force-stop.
            if (!caller.WaitForExit(120000)) return 1;
            CompleteUninstall(ticket, EnvironmentAccess);
            return 0;
        }
        catch (Exception ex) when (ExpectedFailure(ex) || ex is WaitHandleCannotBeOpenedException)
        {
            if (folder is not null)
                try { File.WriteAllText(Path.Combine(folder, "failure.txt"), "Launchpad uninstall did not complete: " + ex.Message); } catch { }
            return 1;
        }
        finally { MarkCompleted(folder); }
    }

    internal static int RunRegistrationFinalizer(string ticketPath)
    {
        string? folder = null;
        try
        {
            var ticket = ReadOwnedTicket(ticketPath, out folder);
            using var setup = Process.GetProcessById(ticket.ParentId);
            _ = setup.SafeHandle;
            if (setup.StartTime.ToUniversalTime().Ticks != ticket.ParentStartedUtcTicks) return 1;
            using var execute = EventWaitHandle.OpenExisting(ticket.ReadyEvent + ".Execute");
            using (var ready = EventWaitHandle.OpenExisting(ticket.ReadyEvent)) ready.Set();
            if (!execute.WaitOne(TimeSpan.FromSeconds(10))) return 1;
            // Keep the verified Setup process handle until it completes; PID reuse cannot satisfy this wait.
            if (!setup.WaitForExit(120000) || setup.ExitCode != 0) return 1;
            CompleteRegistration(ticket, EnvironmentAccess);
            return 0;
        }
        catch (Exception ex) when (ExpectedFailure(ex) || ex is WaitHandleCannotBeOpenedException) { return 1; }
        finally { MarkCompleted(folder); }
    }

    private static RegistrationTicket ReadOwnedTicket(string ticketPath, out string? ownedFolder)
    {
        ownedFolder = null;
        var folder = Path.GetDirectoryName(LocalPath(Environment.ProcessPath ?? ""))!;
        if (!SamePath(Path.GetDirectoryName(folder)!, EnvironmentAccess.StorageRoot) ||
            !Guid.TryParseExact(Path.GetFileName(folder), "N", out _) ||
            !SamePath(ticketPath, Path.Combine(folder, "registration.json")))
            throw new InvalidDataException("The maintenance helper is outside its private staging folder.");
        RejectReparse(folder);
        ownedFolder = folder;
        RejectReparse(ticketPath);
        if (new FileInfo(ticketPath).Length > 65536) throw new InvalidDataException("The maintenance request is too large.");
        var ticket = JsonSerializer.Deserialize<RegistrationTicket>(File.ReadAllText(ticketPath)) ?? throw new InvalidDataException("Missing maintenance request.");
        ValidateTicket(ticket, DateTime.UtcNow);
        return ticket;
    }

    private static void MarkCompleted(string? folder)
    {
        if (folder is null) return;
        try { File.WriteAllText(Path.Combine(folder, "completed"), "done"); }
        catch { /* Never start normal UI from this helper. */ }
    }

    internal static void ValidateTicket(RegistrationTicket ticket, DateTime now)
    {
        LocalPath(ticket.Root);
        if (!SoftwareVersion.TryParse(ticket.Version, out _) || string.IsNullOrEmpty(ticket.ExecutableHash) || ticket.ExecutableHash.Length != 64 || !ticket.ExecutableHash.All(Uri.IsHexDigit) ||
            ticket.ParentId <= 0 || ticket.ParentStartedUtcTicks <= 0 || ticket.ExpiresUtc < now || ticket.ExpiresUtc > now.AddMinutes(6) ||
            string.IsNullOrEmpty(ticket.ReadyEvent) || !ticket.ReadyEvent.StartsWith(@"Local\SwedenFirLaunchpad.Finalizer.", StringComparison.Ordinal) ||
            !Guid.TryParseExact(ticket.ReadyEvent["Local\\SwedenFirLaunchpad.Finalizer.".Length..], "N", out _))
            throw new InvalidDataException("The uninstall registration request is invalid or expired.");
    }

    // Called only after the held Setup handle exits successfully. Keep the installed executable
    // immutable while validating its identity and changing the corresponding registration.
    internal static void CompleteRegistration(RegistrationTicket ticket, UninstallEnvironment environment)
    {
        ValidateTicket(ticket, DateTime.UtcNow);
        var main = Path.Combine(ticket.Root, "current", MainExeName);
        RejectReparse(main);
        using var input = new FileStream(main, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), ticket.ExecutableHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed executable changed after Setup prepared uninstall registration.");
        var installation = new ManagedInstallation(PackageId, ticket.Root, main, Path.Combine(ticket.Root, "Update.exe"), ticket.Version, false);
        Register(Validate(installation, environment), environment);
    }

    private static ManagedInstallation ValidatePreparedInstallation(RegistrationTicket ticket, UninstallEnvironment environment)
    {
        ValidateTicket(ticket, DateTime.UtcNow);
        var main = Path.Combine(ticket.Root, "current", MainExeName);
        RejectReparse(main);
        using var input = new FileStream(main, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), ticket.ExecutableHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed executable changed after maintenance was prepared.");
        return Validate(new ManagedInstallation(PackageId, ticket.Root, main, Path.Combine(ticket.Root, "Update.exe"), ticket.Version, false), environment);
    }

    internal static void CleanupCompletedFinalizers(string storageRoot)
    {
        RejectReparse(storageRoot);
        if (!Directory.Exists(storageRoot)) return;
        foreach (var folder in Directory.EnumerateDirectories(storageRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) continue;
            try
            {
                RejectReparse(folder);
                var files = Directory.GetFileSystemEntries(folder);
                string[] allowed = [MainExeName, "registration.json", "completed"];
                if (!File.Exists(Path.Combine(folder, "completed")) || files.Any(path => !allowed.Contains(Path.GetFileName(path), StringComparer.Ordinal))) continue;
                foreach (var file in files) { RejectReparse(file); if (Directory.Exists(file)) throw new IOException("Unexpected finalizer directory."); }
                // The helper may have written completion immediately before exiting: a sharing failure
                // simply leaves it for a later launch. Never recursively remove arbitrary cache contents.
                File.Delete(Path.Combine(folder, MainExeName));
                File.Delete(Path.Combine(folder, "registration.json"));
                File.Delete(Path.Combine(folder, "completed"));
                Directory.Delete(folder, recursive: false);
            }
            catch (Exception ex) when (ExpectedFailure(ex)) { }
        }
    }

    private static int ParentProcessId()
    {
        using var process = Process.GetCurrentProcess();
        var information = new ProcessBasicInformation();
        int status = NtQueryInformationProcess(process.Handle, 0, ref information, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status != 0) throw new IOException("Could not identify the installer process.");
        return checked((int)information.ParentId.ToInt64());
    }

    internal static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains('"'))
            throw new InvalidDataException("A local absolute installation path is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full == Path.GetPathRoot(full)) throw new InvalidDataException("An installation cannot be a drive root.");
        return full;
    }
    private static bool SamePath(string left, string right) => string.Equals(LocalPath(left), LocalPath(right), StringComparison.OrdinalIgnoreCase);
    internal static void RejectReparse(string path)
    {
        for (string? current = LocalPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked installation or maintenance paths are not supported.");
    }
    private static bool ExpectedFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
        InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException or XmlException or JsonException;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation { public IntPtr Reserved1, Peb, Reserved2, Reserved3, ProcessId, ParentId; }
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation information, int length, out int returnedLength);

    internal sealed record ManagedInstallation(string AppId, string Root, string Executable, string Updater, string Version, bool Portable);
    internal sealed record Registration(string InstallLocation, string DisplayName, string UninstallString, string QuietUninstallString);
    internal sealed record RegistrationTicket(string Root, string Version, string ExecutableHash, int ParentId, long ParentStartedUtcTicks, string ReadyEvent, DateTime ExpiresUtc);

    internal sealed class UninstallEnvironment
    {
        // The executable cannot remove its own mapped image. A completed temporary copy may remain
        // after uninstall, until a later Launchpad launch or Windows/user temporary-file cleanup.
        public string StorageRoot { get; init; } = Path.Combine(Path.GetTempPath(), "SwedenFirLaunchpad-Maintenance");
        public Func<ManagedInstallation?> Locate { get; init; } = () =>
        {
            var locator = VelopackLocator.Current;
            if (locator.CurrentlyInstalledVersion is null || locator.RootAppDir is null || Environment.ProcessPath is null) return null;
            return new(locator.AppId ?? "", locator.RootAppDir, Environment.ProcessPath, locator.UpdateExePath ?? "", locator.CurrentlyInstalledVersion.ToString(), locator.IsPortable);
        };
        public Func<Registration?> ReadRegistration { get; init; } = () =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            return key is null ? null : new(key.GetValue("InstallLocation") as string ?? "", key.GetValue("DisplayName") as string ?? "",
                key.GetValue("UninstallString") as string ?? "", key.GetValue("QuietUninstallString") as string ?? "");
        };
        public Action<string, string> WriteCommands { get; init; } = (interactive, quiet) =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true) ?? throw new InvalidDataException("The uninstall registration disappeared.");
            key.SetValue("UninstallString", interactive, RegistryValueKind.String);
            key.SetValue("QuietUninstallString", quiet, RegistryValueKind.String);
        };
        public Action<ManagedInstallation> PrepareHandoff { get; init; } = ScheduleUninstall;
        public Action<ProcessStartInfo> RunNativeUninstall { get; init; } = start =>
        {
            using var process = Process.Start(start) ?? throw new IOException("Windows did not start the uninstaller.");
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("The native Launchpad uninstaller did not complete successfully.");
        };
    }
}
