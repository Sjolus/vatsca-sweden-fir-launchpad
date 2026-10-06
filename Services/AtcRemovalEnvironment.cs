using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace VatscaUpdateChecker.Services;

internal sealed record AtcMsiRegistration(string ProductCode, string Name, string Publisher,
    string Version, string InstallLocation, string DisplayIcon, bool WindowsInstaller, string Scope);
internal sealed record AtcRemovalCommand(string Executable, IReadOnlyList<string> Arguments,
    bool Elevate, string? NsisInstallDirectory = null);

/// <summary>Tests replace all registry, trust, process and execution delegates.</summary>
internal sealed class AtcRemovalEnvironment
{
    public string RoamingAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string WindowsDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public string DocumentsDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string ProgramFilesDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public string ProgramFilesX86Directory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    public SoftwareInstallerEnvironment Software { get; init; } = new() { PrerequisiteProblem = _ => null };
    public Func<IReadOnlyList<AtcMsiRegistration>> ReadMsiRegistrations { get; init; } = ReadMsi;
    public Func<string, string, string?> GetMsiComponentPath { get; init; } = ReadMsiComponentPath;
    public Func<AtcMsiRegistration, string, string, string, EuroScopeMsiFootprint> ReadMsiFootprint { get; init; } = EuroScopeMsiFootprint.Read;
    public Func<string, bool> IsProcessRunning { get; init; } = IsProcessPresent;
    public Func<AtcRemovalCommand, Task<int>> RunAsync { get; init; } = AtcRemovalVendor.RunNativeAsync;

    private static bool IsProcessPresent(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static IReadOnlyList<AtcMsiRegistration> ReadMsi()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var result = new List<AtcMsiRegistration>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var parent = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (parent == null) continue;
            foreach (var name in parent.GetSubKeyNames())
            {
                if (!Guid.TryParseExact(name, "B", out _)) continue;
                using var key = parent.OpenSubKey(name);
                if (key == null) continue;
                string Value(string field) => key.GetValue(field) as string ?? "";
                var displayName = Value("DisplayName");
                if (!displayName.StartsWith("EuroScope", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new(name, displayName, Value("Publisher"), Value("DisplayVersion"),
                    Value("InstallLocation"), Value("DisplayIcon"), key.GetValue("WindowsInstaller") is int flag && flag == 1,
                    hive == RegistryHive.CurrentUser ? "CurrentUser" : "AllUsers"));
            }
        }
        // A shared HKCU key can be returned through both registry views.
        return result.Distinct().ToArray();
    }

    private static string? ReadMsiComponentPath(string productCode, string componentCode)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Guid.TryParseExact(productCode, "B", out _) || !Guid.TryParseExact(componentCode, "B", out _)) return null;
        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        // Read-only query. Never use Win32_Product, MsiProvideComponent or repair/configure APIs.
        return MsiGetComponentPathW(productCode, componentCode, path, ref length) == 3 // INSTALLSTATE_LOCAL
            ? path.ToString() : null;
    }

    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MsiGetComponentPathW(string productCode, string componentCode, StringBuilder path, ref uint length);
}
