using System.IO;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Checks known registry entries and standard paths only. Never launches or modifies an application.</summary>
public sealed class InstallationDiscoveryService
{
    private readonly AtcRemovalEnvironment _environment;
    private readonly SoftwareInstaller _software;
    public InstallationDiscoveryService() : this(new AtcRemovalEnvironment()) { }
    internal InstallationDiscoveryService(AtcRemovalEnvironment environment)
    {
        _environment = environment;
        _software = new SoftwareInstaller(environment.Software);
    }

    public IReadOnlyList<InstallationCandidate> Discover()
    {
        var candidates = new List<InstallationCandidate>();
        var sectorRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPossibleRoot(sectorRoots, _environment.RoamingAppData, "EuroScope");
        AddPossibleRoot(sectorRoots, _environment.DocumentsDirectory, "EuroScope");
        foreach (var app in new[] { SoftwareApp.Vacs, SoftwareApp.TrackAudio })
        {
            try
            {
                var registrations = _environment.Software.ReadRegistrations(app);
                foreach (var item in registrations)
                {
                    try
                    {
                        var expected = app == SoftwareApp.Vacs ? "vacs-client.exe" : "trackaudio.exe";
                        if (!item.MainBinaryName.Equals(expected, StringComparison.OrdinalIgnoreCase)) continue;
                        var exe = SoftwareInstaller.FullPath(Path.Combine(item.RootPath, expected));
                        if (!IsRegularFile(exe)) continue;
                        var binary = _environment.Software.ReadBinary(exe);
                        var product = app == SoftwareApp.Vacs ? "vacs" : "TrackAudio";
                        if (!binary.ProductName.Equals(product, StringComparison.OrdinalIgnoreCase)) continue;
                        var recognized = _software.Inspect(app, exe);
                        candidates.Add(new(app == SoftwareApp.Vacs ? AtcRemovalApp.Vacs : AtcRemovalApp.TrackAudio,
                            app == SoftwareApp.Vacs ? "VACS" : "TrackAudio", exe, null, item.Scope,
                            recognized.CanUpdate ? "Recognized registered installation. Each update/removal is checked again before use." :
                                "Path can be imported; automatic management is unavailable: " + recognized.Reason, recognized.CanUpdate));
                    }
                    catch (Exception ex) when (Expected(ex)) { }
                }
            }
            catch (Exception ex) when (Expected(ex)) { }
        }
        try
        {
            var root = Path.Combine(_environment.LocalAppData, SoftwareInstaller.VatisId);
            var exe = Path.Combine(root, "current", "vATIS.exe");
            if (IsRegularFile(exe))
            {
                var binary = _environment.Software.ReadBinary(exe);
                if (binary.ProductName.Equals("vATIS", StringComparison.OrdinalIgnoreCase))
                {
                    var recognized = _software.Inspect(SoftwareApp.Vatis, exe);
                    candidates.Add(new(AtcRemovalApp.Vatis, "vATIS", exe, null, "CurrentUser",
                        recognized.CanUpdate ? "Recognized per-user installation with a verified updater." :
                            "Path can be imported; automatic management is unavailable: " + recognized.Reason, recognized.CanUpdate));
                }
            }
        }
        catch (Exception ex) when (Expected(ex)) { }

        var possibleEuroScope = new List<(string Exe, string Scope)>();
        try
        {
            foreach (var registration in _environment.ReadMsiRegistrations())
            {
                try
                {
                    var componentPath = AtcRemovalCatalog.ComponentPath(registration, _environment);
                    if (!string.IsNullOrWhiteSpace(componentPath)) possibleEuroScope.Add((componentPath, registration.Scope));
                    if (!string.IsNullOrWhiteSpace(registration.InstallLocation))
                        possibleEuroScope.Add((Path.Combine(registration.InstallLocation.Trim().Trim('"'), "EuroScope.exe"), registration.Scope));
                    var icon = registration.DisplayIcon.Trim();
                    if (icon.EndsWith(",0", StringComparison.Ordinal)) icon = icon[..^2];
                    icon = icon.Trim('"');
                    if (Path.GetFileName(icon).Equals("EuroScope.exe", StringComparison.OrdinalIgnoreCase))
                        possibleEuroScope.Add((icon, registration.Scope));
                }
                catch (Exception ex) when (Expected(ex)) { }
            }
        }
        catch (Exception ex) when (Expected(ex)) { }
        foreach (var standard in new[] { _environment.ProgramFilesX86Directory, _environment.ProgramFilesDirectory })
            if (!string.IsNullOrWhiteSpace(standard)) possibleEuroScope.Add((Path.Combine(standard, "EuroScope", "EuroScope.exe"), "Unknown"));
        foreach (var possible in possibleEuroScope)
        {
            try
            {
                var exe = SoftwareInstaller.FullPath(possible.Exe);
                if (!IsRegularFile(exe) || candidates.Any(c => c.Id == AtcRemovalApp.EuroScope && SoftwareInstaller.SamePath(c.ExecutablePath, exe))) continue;
                var binary = _environment.Software.ReadBinary(exe);
                if (!AtcRemovalCatalog.IsEuroScopeProduct(binary.ProductName)) continue;
                var root = Path.GetDirectoryName(exe)!;
                sectorRoots.Add(root);
                var recognized = new AtcRemovalCatalog(_environment).Discover(new AppSettings { EuroscopeExePath = exe })
                    .Single(t => t.Id == AtcRemovalApp.EuroScope);
                candidates.Add(new(AtcRemovalApp.EuroScope, "EuroScope", exe, null, possible.Scope,
                    recognized.CanRemoveApplication ? "Recognized Windows Installer installation; GNG data is imported separately." :
                        "Executable found. Launch is available; automatic uninstall requires a verified matching MSI registration.", recognized.CanRemoveApplication));
            }
            catch (Exception ex) when (Expected(ex)) { }
        }
        foreach (var root in sectorRoots)
        {
            try
            {
                SoftwareInstaller.RejectReparse(root);
                var marker = Path.Combine(root, "ESAA");
                SoftwareInstaller.RejectReparse(marker);
                if (!Directory.Exists(marker)) continue;
                candidates.Add(new(AtcRemovalApp.Gng, "Swedish GNG data", "", root, "EuroScope data folder",
                    "ESAA folder found in a known EuroScope location. Review this path; personal files may share the folder.", true));
            }
            catch (Exception ex) when (Expected(ex)) { }
        }
        foreach (var parent in new[] { _environment.ProgramFilesDirectory, _environment.ProgramFilesX86Directory }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(parent)) continue;
                var root = SoftwareInstaller.FullPath(Path.Combine(parent, "VatEFS"));
                var dll = Path.Combine(root, "VatEFS.dll");
                if (!IsRegularFile(dll)) continue;
                candidates.Add(new(AtcRemovalApp.VatEfs, "VatEFS plugin", dll, root, "Plugin folder",
                    "Configured plugin DLL can be managed. Other backend files and custom profile references need separate review.", true));
            }
            catch (Exception ex) when (Expected(ex)) { }
        }
        return candidates.DistinctBy(c => (c.Id, c.ExecutablePath.ToUpperInvariant(), c.DataPath?.ToUpperInvariant()))
            .OrderBy(c => c.Id).ThenBy(c => c.ExecutablePath, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.DataPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsRegularFile(string path) { SoftwareInstaller.RejectReparse(path); return File.Exists(path); }
    private static void AddPossibleRoot(ISet<string> roots, string parent, string child)
    { if (!string.IsNullOrWhiteSpace(parent)) roots.Add(Path.Combine(parent, child)); }
    private static bool Expected(Exception ex) => ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or
        InvalidOperationException or System.Security.SecurityException or System.Security.Cryptography.CryptographicException;
}
