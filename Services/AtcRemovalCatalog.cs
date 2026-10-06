using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Discovers only configured applications and known data locations. Does not read profile contents.</summary>
public sealed class AtcRemovalCatalog
{
    // Read from the official EuroScopeSetup.3.2.3.2.msi Property/File/Component tables in read-only mode.
    internal const string EuroScope3232Product = EuroScopePolicy.ProductCode;
    internal const string EuroScope3232Component = EuroScopePolicy.MainComponentCode;
    private readonly AtcRemovalEnvironment _environment;
    private readonly SoftwareInstaller _software;
    public AtcRemovalCatalog() : this(new AtcRemovalEnvironment()) { }
    internal AtcRemovalCatalog(AtcRemovalEnvironment environment)
    {
        _environment = environment;
        _software = new SoftwareInstaller(environment.Software);
    }

    public IReadOnlyList<AtcRemovalTarget> Discover(AppSettings settings) => new[]
    {
        Safely(AtcRemovalApp.EuroScope, "EuroScope", () => EuroScope(settings)),
        Safely(AtcRemovalApp.Gng, "Swedish GNG package", () => Gng(settings.EuroscopeDataPath)),
        Safely(AtcRemovalApp.Vacs, "VACS", () => Software(SoftwareApp.Vacs, settings.VacsExePath)),
        Safely(AtcRemovalApp.Vatis, "vATIS", () => Software(SoftwareApp.Vatis, settings.VatisExePath)),
        Safely(AtcRemovalApp.TrackAudio, "TrackAudio", () => Software(SoftwareApp.TrackAudio, settings.TrackAudioExePath)),
        Safely(AtcRemovalApp.VatEfs, "VatEFS", () => VatEfs(settings.VatEfsPath)),
        Safely(AtcRemovalApp.Vatiris, "VATIRIS", Vatiris)
    };

    public AtcRemovalTarget Revalidate(AtcRemovalTarget target, AppSettings settings)
    {
        var fresh = Discover(settings).Single(t => t.Id == target.Id);
        if (fresh.CanRemoveApplication != target.CanRemoveApplication || fresh.CanRemoveData != target.CanRemoveData ||
            fresh.VendorSpec != target.VendorSpec || !SamePaths(fresh.ProgramRoots, target.ProgramRoots) ||
            !SamePaths(fresh.DataRoots, target.DataRoots))
            throw new IOException("The application or its removal paths changed. Review the removal plan again.");
        return fresh;
    }

    public bool IsRunning(AtcRemovalTarget target)
    {
        if (target.VendorSpec?.Installation is { } installation) return _software.IsRunning(installation);
        return target.Id switch
        {
            AtcRemovalApp.Vacs => _environment.Software.IsRunning(SoftwareApp.Vacs),
            AtcRemovalApp.Vatis => _environment.Software.IsRunning(SoftwareApp.Vatis),
            AtcRemovalApp.TrackAudio => _environment.Software.IsRunning(SoftwareApp.TrackAudio),
            AtcRemovalApp.Vatiris => _environment.IsProcessRunning("msedge"),
            AtcRemovalApp.VatEfs => _environment.IsProcessRunning("EuroScope") || _environment.IsProcessRunning("msedge"),
            _ => _environment.IsProcessRunning("EuroScope")
        };
    }

    private AtcRemovalTarget Software(SoftwareApp app, string exe)
    {
        var id = app switch { SoftwareApp.Vacs => AtcRemovalApp.Vacs, SoftwareApp.Vatis => AtcRemovalApp.Vatis, _ => AtcRemovalApp.TrackAudio };
        var name = app switch { SoftwareApp.Vacs => "VACS", SoftwareApp.Vatis => "vATIS", _ => "TrackAudio" };
        var installation = _software.Inspect(app, exe);
        var warnings = new List<string> { "Settings reset covers this Windows user's known data locations only; external/custom settings are not discovered." };
        var paths = app switch
        {
            SoftwareApp.Vacs => Existing(Path.Combine(_environment.RoamingAppData, "app.vacs.vacs-client"), Path.Combine(_environment.LocalAppData, "app.vacs.vacs-client")),
            SoftwareApp.TrackAudio => Existing(Path.Combine(_environment.RoamingAppData, "trackaudio")),
            _ => VatisData()
        };
        AtcRemovalVendorSpec? spec = null;
        if (installation.CanUpdate)
        {
            var uninstaller = app switch
            {
                SoftwareApp.Vatis => installation.UpdaterPath!,
                SoftwareApp.Vacs => Path.Combine(installation.RootPath, "uninstall.exe"),
                _ => Path.Combine(installation.RootPath, "Uninstall TrackAudio.exe")
            };
            RequireFile(uninstaller);
            spec = new(app == SoftwareApp.Vatis ? AtcRemovalVendorKind.Velopack : AtcRemovalVendorKind.Nsis,
                installation, uninstaller, Hash(uninstaller), app == SoftwareApp.Vatis);
        }
        if (app == SoftwareApp.Vatis)
            warnings.Add("vATIS uninstall removes its entire installation root, including profiles and settings. Keeping them means an external recovery copy for manual restoration, not keeping them in place.");
        if (app == SoftwareApp.Vacs)
            warnings.Add("A per-user VACS uninstaller may require elevation and fall back to manual uninstall. Additional configuration files outside the known AppData folders are retained.");
        return new(id, name, "Use the recognized vendor uninstaller, or reset known local settings separately.",
            installation.CanUpdate ? new[] { installation.RootPath } : Array.Empty<string>(), paths,
            spec != null, paths.Count > 0, spec == null ? "No supported configured installation: " + installation.Reason : null,
            spec, warnings.AsReadOnly());
    }

    private IReadOnlyList<string> VatisData()
    {
        var root = Path.Combine(_environment.LocalAppData, SoftwareInstaller.VatisId);
        return Existing(new[] { "Profiles", "Logs", "AppConfig.json", "Airports.json", "Navaids.json", "NavDataSerial.json" }
            .Select(name => Path.Combine(root, name)).ToArray());
    }

    private AtcRemovalTarget EuroScope(AppSettings settings)
    {
        var unavailable = "A matching EuroScope Windows Installer registration and configured executable are required. Portable/unknown copies must be removed manually.";
        if (string.IsNullOrWhiteSpace(settings.EuroscopeExePath)) return Empty(AtcRemovalApp.EuroScope, "EuroScope", unavailable);
        var exe = SoftwareInstaller.FullPath(settings.EuroscopeExePath);
        RequireFile(exe);
        var binary = _environment.Software.ReadBinary(exe);
        if (!Path.GetFileName(exe).Equals("EuroScope.exe", StringComparison.OrdinalIgnoreCase) ||
            !IsEuroScopeProduct(binary.ProductName))
            return Empty(AtcRemovalApp.EuroScope, "EuroScope", unavailable);
        var root = Path.GetDirectoryName(exe)!;
        var registrations = _environment.ReadMsiRegistrations();
        var matches = registrations.Where(r => MsiMatches(r, exe, binary, ComponentPath(r, _environment))).ToArray();
        if (matches.Length != 1) return Empty(AtcRemovalApp.EuroScope, "EuroScope", unavailable);
        var registration = matches[0];
        var footprint = _environment.ReadMsiFootprint(registration, root, _environment.RoamingAppData, _environment.WindowsDirectory);
        var msiexec = Path.Combine(_environment.WindowsDirectory, "System32", "msiexec.exe");
        RequireFile(msiexec);
        var spec = new AtcRemovalVendorSpec(AtcRemovalVendorKind.Msi, null, msiexec, Hash(msiexec), true,
            registration.ProductCode, root, registration.Scope, footprint.Fingerprint);
        return new(AtcRemovalApp.EuroScope, "EuroScope", "Remove only the matched MSI installation; manage Swedish package data in its separate row.",
            new[] { root }, Existing(Path.Combine(_environment.RoamingAppData, "EuroScope")), true, false, null, spec,
            new[] { "EuroScope MSI owns sample profiles/settings under AppData\\EuroScope as well as program files and its font. A recovery export is required before removal because these files may be deleted. Other FIRs and custom external paths are not separately wiped; review them before restoring." });
    }

    internal static string? ComponentPath(AtcMsiRegistration item, AtcRemovalEnvironment environment) =>
        item.ProductCode.Equals(EuroScope3232Product, StringComparison.OrdinalIgnoreCase)
            ? environment.GetMsiComponentPath(item.ProductCode, EuroScope3232Component) : null;

    internal static bool IsEuroScopeProduct(string productName) =>
        productName.Equals("EuroScope", StringComparison.OrdinalIgnoreCase) ||
        productName.Equals("EuroScope Application", StringComparison.OrdinalIgnoreCase);

    internal static bool MsiMatches(AtcMsiRegistration item, string exe, SoftwareBinary binary, string? componentPath = null)
    {
        if (!item.WindowsInstaller || item.Scope is not ("CurrentUser" or "AllUsers") || !Guid.TryParseExact(item.ProductCode, "B", out _) ||
            !Regex.IsMatch(item.Name, @"^EuroScope(?:\s+v?\d[\d.]*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            !new[] { "Gergely Csernak", "Gergely Csernák", "Csernak Gergely", "Csernák Gergely" }.Contains(item.Publisher, StringComparer.OrdinalIgnoreCase) ||
            !Version.TryParse(item.Version, out var msiVersion) || !Version.TryParse(binary.ProductVersion, out var exeVersion) ||
            msiVersion.Major != exeVersion.Major || msiVersion.Minor != exeVersion.Minor || msiVersion.Build != exeVersion.Build) return false;
        var location = item.InstallLocation.Trim().Trim('"');
        var icon = item.DisplayIcon.Trim();
        if (icon.EndsWith(",0", StringComparison.Ordinal)) icon = icon[..^2];
        icon = icon.Trim('"');
        return (!string.IsNullOrWhiteSpace(componentPath) && item.ProductCode.Equals(EuroScope3232Product, StringComparison.OrdinalIgnoreCase) && SoftwareInstaller.SamePath(componentPath, exe)) ||
            (!string.IsNullOrWhiteSpace(location) && SoftwareInstaller.SamePath(location, Path.GetDirectoryName(exe)!)) ||
            (!string.IsNullOrWhiteSpace(icon) && SoftwareInstaller.SamePath(icon, exe));
    }

    private static AtcRemovalTarget Gng(string configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot)) return Empty(AtcRemovalApp.Gng, "Swedish GNG package", "Configure the EuroScope data folder first.");
        var root = SoftwareInstaller.FullPath(configuredRoot);
        SoftwareInstaller.RejectReparse(root);
        if (!Directory.Exists(root)) return Empty(AtcRemovalApp.Gng, "Swedish GNG package", "The configured data folder does not exist.");
        var paths = new List<string>();
        var esaa = Path.Combine(root, "ESAA");
        if (Directory.Exists(esaa)) { SoftwareInstaller.RejectReparse(esaa); paths.Add(esaa); }
        var files = Directory.EnumerateFiles(root).Take(10001).ToArray();
        if (files.Length > 10000) throw new IOException("The configured data folder has too many root files to review safely.");
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("ESAA ", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(name, @"^ESAA-Sweden_\d{14}-\d{6}-\d{4}\.(sct|ese|rwy)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            { SoftwareInstaller.RejectReparse(file); paths.Add(file); }
        }
        var known = paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(AtcRemovalApp.Gng, "Swedish GNG package", "Remove the ESAA package and its named root files for a clean installation. Personal settings are inseparable from this package; preserving them requires a recovery copy.",
            known, known, known.Length > 0, known.Length > 0, known.Length == 0 ? "No recognized ESAA package files found." : null, null,
            new[] { "This removes personal overrides, profiles, Hoppie/sign-in files, and staged plugin updates inside ESAA. Export a recovery copy to preserve them.",
                "Other FIR folders, external references, installed Windows fonts and unrelated root files remain. Files beginning 'ESAA ' are included in the explicit review, including customized ones." });
    }

    private AtcRemovalTarget VatEfs(string pluginRoot)
    {
        var data = Existing(Path.Combine(_environment.RoamingAppData, "VatscaUpdateChecker", "VatEFSProfile"));
        var program = string.IsNullOrWhiteSpace(pluginRoot) ? Array.Empty<string>() : Existing(Path.Combine(SoftwareInstaller.FullPath(pluginRoot), "VatEFS.dll")).ToArray();
        return new(AtcRemovalApp.VatEfs, "VatEFS", "Remove the configured plugin DLL only; optionally reset Launchpad's VatEFS browser session.",
            program, data, program.Length == 1, data.Count > 0, program.Length == 0 ? "No configured VatEFS.dll. Other VatEFS files/installers are not identified." : null, null,
            new[] { "EuroScope profile plugin references are retained and must be disabled or repaired separately. Other VatEFS binaries, configuration and backend data are not deleted because their ownership/layout is not established.",
                "Close EuroScope and all Microsoft Edge processes before proceeding. The browser reset does not reset VatEFS backend data." });
    }

    private AtcRemovalTarget Vatiris()
    {
        var data = Existing(Path.Combine(_environment.RoamingAppData, "VatscaUpdateChecker", "VATIRISProfile"));
        return new(AtcRemovalApp.Vatiris, "VATIRIS", "Reset only Launchpad's isolated VATIRIS browser profile (including sign-in sessions).",
            Array.Empty<string>(), data, false, data.Count > 0, "VATIRIS is a web app; there is no application installed by Launchpad to uninstall.", null,
            new[] { "Account-stored presets/settings are unaffected. Use VATIRIS SYSTEM → RESET for website settings. Independently installed browser PWAs must be removed in that browser. Microsoft Edge itself is retained.",
                "Close all Microsoft Edge processes before clearing the isolated profile; Launchpad does not terminate them." });
    }

    private static IReadOnlyList<string> Existing(params string[] paths) => paths.Select(SoftwareInstaller.FullPath)
        .Where(path => { SoftwareInstaller.RejectReparse(path); return File.Exists(path) || Directory.Exists(path); })
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    private static bool SamePaths(IReadOnlyList<string> left, IReadOnlyList<string> right) => left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
    internal static string Hash(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static void RequireFile(string path) { SoftwareInstaller.RejectReparse(path); if (!File.Exists(path)) throw new FileNotFoundException("A required application file is missing.", path); }
    private static AtcRemovalTarget Empty(AtcRemovalApp id, string name, string reason) => new(id, name, "Manual removal required for this layout.", Array.Empty<string>(), Array.Empty<string>(), false, false, reason, null, Array.Empty<string>());
    private static AtcRemovalTarget Safely(AtcRemovalApp id, string name, Func<AtcRemovalTarget> factory)
    {
        try { return factory(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or CryptographicException)
        { return Empty(id, name, "This target cannot be reviewed safely: " + ex.Message); }
    }
}
