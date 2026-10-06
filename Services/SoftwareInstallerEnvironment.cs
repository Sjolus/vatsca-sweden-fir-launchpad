using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

internal sealed record SoftwareBinary(string ProductVersion, string ProductName);
internal sealed record SoftwareRegistration(string RootPath, string RestoreRootPath, string Scope, string Version, string MainBinaryName);
internal sealed record SoftwareInstallCommand(string Executable, IReadOnlyList<string> Arguments, bool Elevate);

/// <summary>Small OS seams; the regression harness supplies synthetic registrations, trust, and a non-executing runner.</summary>
internal sealed class SoftwareInstallerEnvironment
{
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public Func<SoftwareApp, IReadOnlyList<SoftwareRegistration>> ReadRegistrations { get; init; } = ReadWindowsRegistrations;
    public Func<string, SoftwareBinary> ReadBinary { get; init; } = ReadFileIdentity;
    public Action<string, string> VerifyPublisher { get; init; } = SoftwareInstallerNative.VerifyPublisher;
    public Func<SoftwareApp, bool> IsRunning { get; init; } = AnyProductProcess;
    public Func<SoftwareApp, string?> PrerequisiteProblem { get; init; } = ReadPrerequisiteProblem;
    public Func<SoftwareInstallCommand, Task<int>> RunAsync { get; init; } = SoftwareInstallerNative.RunAsync;

    private static SoftwareBinary ReadFileIdentity(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new(info.ProductVersion ?? info.FileVersion ?? "", info.ProductName ?? "");
    }

    private static IReadOnlyList<SoftwareRegistration> ReadWindowsRegistrations(SoftwareApp app)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var registrations = new List<SoftwareRegistration>();
        var keyName = app == SoftwareApp.Vacs ? "vacs" : SoftwareInstaller.TrackAudioGuid;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + keyName, writable: false);
            if (uninstall == null) continue;
            string Get(string key) => uninstall.GetValue(key) as string ?? "";
            var scope = hive == RegistryHive.CurrentUser ? "CurrentUser" : "AllUsers";
            var expectedName = app == SoftwareApp.Vacs ? "vacs" : "TrackAudio";
            if (!Get("DisplayName").Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The product registration has an unexpected identity.");
            using var destinationKey = baseKey.OpenSubKey(app == SoftwareApp.Vacs ? @"Software\vacs\vacs" : @"Software\" + SoftwareInstaller.TrackAudioGuid, writable: false);
            var restoreRoot = Unquote(destinationKey?.GetValue(app == SoftwareApp.Vacs ? "" : "InstallLocation") as string ?? "");
            var root = app == SoftwareApp.Vacs ? Unquote(Get("InstallLocation")) : restoreRoot;
            var mainName = app == SoftwareApp.Vacs ? Get("MainBinaryName") : "trackaudio.exe";
            if (!mainName.Equals(app == SoftwareApp.Vacs ? "vacs-client.exe" : "trackaudio.exe", StringComparison.OrdinalIgnoreCase) ||
                !SoftwareInstaller.SamePath(root, restoreRoot))
                throw new InvalidDataException("The registered installer destination/main executable is not recognized.");
            SoftwareInstaller.RejectReparse(root);
            var uninstallerName = app == SoftwareApp.Vacs ? "uninstall.exe" : "Uninstall TrackAudio.exe";
            var uninstaller = Path.Combine(root, uninstallerName);
            var expectedUninstall = '"' + uninstaller + '"' + (app == SoftwareApp.TrackAudio ? " /" + scope.ToLowerInvariant() : "");
            if (!Get("UninstallString").Equals(expectedUninstall, StringComparison.OrdinalIgnoreCase) || !File.Exists(uninstaller))
                throw new InvalidDataException("The registered uninstaller is not the expected vendor uninstaller.");
            SoftwareInstaller.RejectReparse(uninstaller);
            var displayIcon = Get("DisplayIcon").Trim().Trim('"');
            if (displayIcon.EndsWith(",0", StringComparison.Ordinal)) displayIcon = displayIcon[..^2].Trim('"');
            if (!SoftwareInstaller.SamePath(displayIcon, Path.Combine(root, mainName)))
                throw new InvalidDataException("The registration points at a different application executable.");
            registrations.Add(new(root, restoreRoot, scope, Get("DisplayVersion"), mainName));
        }
        // HKCU's shared uninstall key can appear through both registry views. Only exact duplicates coalesce.
        return registrations.Distinct().ToArray();
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.StartsWith('"') && value.EndsWith('"')) value = value[1..^1];
        if (value.Contains('"')) throw new InvalidDataException("An installer path contains unexpected quoting.");
        return SoftwareInstaller.FullPath(value);
    }

    private static bool AnyProductProcess(SoftwareApp app)
    {
        // Process-name enumeration spans sessions without reading another user's credentials or profiles.
        // Any matching process blocks; we never terminate one on the user's behalf.
        var name = app switch { SoftwareApp.Vacs => "vacs-client", SoftwareApp.Vatis => "vATIS", _ => "trackaudio" };
        try
        {
            var processes = Process.GetProcessesByName(name);
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new IOException("The application process state could not be checked in every Windows session. Update manually.");
        }
    }

    private static string? ReadPrerequisiteProblem(SoftwareApp app)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (app == SoftwareApp.TrackAudio)
        {
            // TrackAudio's customInit runs a visible /passive redist when absent, even with installer /S.
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var runtime = machine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            return runtime?.GetValue("Installed") is int installed && installed == 1 ? null :
                "Install the Microsoft Visual C++ x64 runtime using TrackAudio's official installer before using background updates.";
        }
        if (app == SoftwareApp.Vacs)
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var runtime = root.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
                if (Version.TryParse(runtime?.GetValue("pv") as string, out var version) && version.Major > 0) return null;
            }
            return "Install the WebView2 runtime using VACS's official installer before using background updates.";
        }
        return null;
    }
}
