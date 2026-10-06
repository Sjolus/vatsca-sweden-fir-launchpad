using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>
/// Explicit fresh installation only. The caller reviews beta opt-in, rejects discovered copies,
/// and holds MaintenanceLock. Setup must never repair/overwrite an existing vATIS data root.
/// </summary>
public sealed class VatisFreshInstaller
{
    public const string ReviewedVersion = "4.1.0-beta.19";
    public const string SetupUrl = "https://hub.vatis.app/download/windows";
    internal const string SetupSha256 = "54F7259B2A1F5A021B8633B3EC33997F2C79FCEA54E0BCB579A0EA509009C815";
    internal const long SetupSize = 36034248;
    internal const string PackageSha256 = "29903D495EA7CCD8C64DA60104EB84E2E23B547EC09E11A0037A2856E3BAD248";
    internal const long PackageSize = 33176430;
    internal static readonly byte[] BundleMarker = Convert.FromHexString("94F0B17B6893E02937EB34EF53AAE7D42B54F5707EF5D6F57854983E5E94ED7D");

    private readonly HttpClient _http;
    private readonly VatisFreshInstallerEnvironment _environment;
    private readonly SoftwareInstaller _software;
    private readonly VatisSetupPin _pin;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool RestartRequired { get; private set; }

    /// <param name="http">Caller-owned client with automatic redirects disabled.</param>
    public VatisFreshInstaller(HttpClient http) : this(http, new(), VatisSetupPin.Reviewed) { }
    internal VatisFreshInstaller(HttpClient http, VatisFreshInstallerEnvironment environment, VatisSetupPin pin)
    {
        _http = http;
        _environment = environment;
        _software = new(environment.Software);
        _pin = pin;
    }

    public void ValidateDestination(string root)
    {
        var expected = Path.Combine(_environment.Software.LocalAppData, SoftwareInstaller.VatisId);
        if (!SoftwareInstaller.SamePath(root, expected))
            throw new InvalidDataException("Fresh vATIS installation supports only its standard current-user folder.");
        RequireAbsent(root);
        foreach (var path in _environment.OtherKnownRoots) RequireAbsent(path);
        if (_environment.HasRegistration())
            throw new InvalidDataException("A vATIS registration already exists. Adopt or remove the existing installation before installing another copy.");
        if (_environment.Software.IsRunning(SoftwareApp.Vatis))
            throw new IOException("Close vATIS in every Windows session before installing it.");
    }

    public void ValidateRelease(SoftwareRelease release)
    {
        var name = $"{SoftwareInstaller.VatisId}-{_pin.Version}-full.nupkg";
        if (release.App != SoftwareApp.Vatis || release.Version != _pin.Version || release.FileName != name ||
            release.DownloadUri.AbsoluteUri != "https://vatis.app/updates/windows/" + name ||
            release.Size != _pin.PackageSize || !release.Sha256.Equals(_pin.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This vATIS release has not been reviewed for installation without starting the client. Use the official installer manually.");
    }

    /// <summary>Cancellation ends at beforeInstall; once Setup starts it is always awaited, never killed.</summary>
    public async Task<SoftwareInstallResult> InstallAsync(SoftwareRelease release, string verifiedNupkgPath,
        string root, IProgress<string>? progress, CancellationToken preApplyOnly, Func<Task>? beforeInstall = null)
    {
        if (RestartRequired) throw new InvalidOperationException("Restart Windows before further vATIS installation.");
        if (!await _gate.WaitAsync(0, preApplyOnly).ConfigureAwait(false))
            throw new InvalidOperationException("A vATIS installation is already in progress.");
        try
        {
            ValidateRelease(release);
            ValidateDestination(root);
            SoftwareInstaller.RejectReparse(verifiedNupkgPath);
            await using var packageLock = new FileStream(verifiedNupkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await _software.VerifyPackageAsync(release, verifiedNupkgPath, preApplyOnly).ConfigureAwait(false);
            progress?.Report("Downloading the reviewed vATIS Setup…");
            var setupPath = await DownloadSetupAsync(preApplyOnly).ConfigureAwait(false);
            SoftwareInstaller.RejectReparse(setupPath);
            await using var setupLock = new FileStream(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await VerifySetupAsync(setupPath, setupLock, release, preApplyOnly).ConfigureAwait(false);
            preApplyOnly.ThrowIfCancellationRequested();
            if (beforeInstall != null) await beforeInstall().ConfigureAwait(false);
            // Recheck after the caller's final release/backup review. No data root is created by us.
            ValidateDestination(root);
            preApplyOnly.ThrowIfCancellationRequested();
            progress?.Report("Installing vATIS in the background without starting the client…");
            var logPath = Path.Combine(Path.GetDirectoryName(setupPath)!, "setup.log");
            // Actual reviewed native helper is 0.0.1251: --silent suppresses first run. It does
            // NOT accept --silent-no-start. Its required --veloapp-install hook exits before Avalonia.
            var code = await _environment.Software.RunAsync(new(setupPath,
                ["--silent", "--installto", root, "--log", logPath], false)).ConfigureAwait(false);
            RestartRequired |= code == 3010;
            if (code != 0)
                throw new IOException($"vATIS Setup returned exit code {code}. Installation may be incomplete; review its installation folder before retrying. Setup log: {logPath}");
            var exe = Path.Combine(root, "current", "vATIS.exe");
            var installed = _software.Inspect(SoftwareApp.Vatis, exe);
            if (!installed.CanUpdate || installed.Version != release.Version || installed.Scope != "CurrentUser" ||
                !SoftwareInstaller.SamePath(installed.RootPath, root) || !_environment.RegistrationMatches(root))
                throw new IOException("vATIS Setup exited, but its expected version, destination and current-user registration could not all be verified. Review the installation manually.");
            if (_environment.Software.IsRunning(SoftwareApp.Vatis))
                throw new IOException("vATIS is running unexpectedly after installation. Launchpad did not stop it; review the installation manually.");
            return new(exe);
        }
        finally { _gate.Release(); }
    }

    private async Task<string> DownloadSetupAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_environment.DownloadTimeout);
        var token = timeout.Token;
        var attempt = Path.Combine(_environment.Software.LocalAppData, "VatscaUpdateChecker", "SoftwareUpdates",
            "FreshInstall", "vATIS", Guid.NewGuid().ToString("N"));
        SoftwareInstaller.RejectReparse(attempt);
        Directory.CreateDirectory(attempt);
        var path = Path.Combine(attempt, "vATIS-Setup.exe");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SetupUrl);
            request.Headers.UserAgent.ParseAdd("SwedenFirLaunchpad/2.0");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            // The reviewed route serves the binary directly. A changed/redirected endpoint requires review.
            if (response.StatusCode != System.Net.HttpStatusCode.OK || response.RequestMessage?.RequestUri?.AbsoluteUri != SetupUrl)
                throw new InvalidDataException("The official vATIS Setup route changed. Use the official download page manually.");
            if (response.Content.Headers.ContentLength is { } length && length != _pin.SetupSize)
                throw new InvalidDataException("vATIS Setup changed since its no-start behavior was reviewed. Use the official installer manually.");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > _pin.SetupSize) throw new InvalidDataException("vATIS Setup exceeds the reviewed download size.");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (total != _pin.SetupSize) throw new InvalidDataException("The vATIS Setup download is incomplete.");
            return path;
        }
        catch (Exception exception)
        {
            // Only our unique partial payload is removed. Other files/logs are never recursively deleted.
            SoftwareInstaller.RejectReparse(path);
            if (File.Exists(path)) File.Delete(path);
            if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("The vATIS Setup download timed out. No installer was started.", exception);
            throw;
        }
    }

    internal async Task VerifySetupAsync(string path, Stream lockedSetup, SoftwareRelease release, CancellationToken cancellationToken)
    {
        ValidateRelease(release);
        if (lockedSetup.Length != _pin.SetupSize || _pin.SetupSize > 64 * 1024 * 1024)
            throw new InvalidDataException("The vATIS Setup size is not the reviewed installer.");
        lockedSetup.Position = 0;
        byte[] bytes = new byte[checked((int)lockedSetup.Length)];
        await lockedSetup.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(_pin.SetupSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The vATIS Setup digest does not match the reviewed installer. Use the official installer manually.");
        _environment.Software.VerifyPublisher(path, "Justin Shannon");
        var identity = _environment.Software.ReadBinary(path);
        if (identity.ProductName != "vATIS" || SoftwareInstaller.NormalizeVersion(identity.ProductVersion) != release.Version)
            throw new InvalidDataException("The signed Setup product/version is not the reviewed vATIS release.");
        VerifyEmbeddedPackage(bytes, release);
    }

    internal static void VerifyEmbeddedPackage(byte[] setup, SoftwareRelease release)
    {
        int marker = setup.AsSpan().IndexOf(BundleMarker);
        if (marker < 16 || setup.AsSpan(marker + BundleMarker.Length).IndexOf(BundleMarker) >= 0)
            throw new InvalidDataException("The vATIS Setup has no unique reviewed Velopack bundle header.");
        long offset = BinaryPrimitives.ReadInt64LittleEndian(setup.AsSpan(marker - 16, 8));
        long size = BinaryPrimitives.ReadInt64LittleEndian(setup.AsSpan(marker - 8, 8));
        if (offset < marker + BundleMarker.Length || size != release.Size || offset > setup.LongLength - size || size <= 0)
            throw new InvalidDataException("The vATIS Setup embedded package range is invalid.");
        string hash = Convert.ToHexString(SHA256.HashData(setup.AsSpan(checked((int)offset), checked((int)size))));
        if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Setup embedded package differs from the reviewed official full package.");
    }

    private static void RequireAbsent(string path)
    {
        SoftwareInstaller.RejectReparse(path);
        try
        {
            _ = File.GetAttributes(path);
            throw new InvalidDataException("A vATIS installation or data folder already exists. Adopt it, or export and remove it before fresh installation; kept data requires manual restoration.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}

internal sealed record VatisSetupPin(string Version, long SetupSize, string SetupSha256, long PackageSize, string PackageSha256)
{
    internal static readonly VatisSetupPin Reviewed = new(VatisFreshInstaller.ReviewedVersion, VatisFreshInstaller.SetupSize,
        VatisFreshInstaller.SetupSha256, VatisFreshInstaller.PackageSize, VatisFreshInstaller.PackageSha256);
}

/// <summary>Tests replace every OS query/runner and use a tiny synthetic bundle with its own exact pin.</summary>
internal sealed class VatisFreshInstallerEnvironment
{
    public SoftwareInstallerEnvironment Software { get; init; } = new();
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public Func<bool> HasRegistration { get; init; } = AnyRegistration;
    public Func<string, bool> RegistrationMatches { get; init; } = MatchesRegistration;
    public IReadOnlyList<string> OtherKnownRoots { get; init; } = KnownRoots();

    private static IReadOnlyList<string> KnownRoots() => new[]
    {
        (Environment.SpecialFolder.LocalApplicationData, "vATIS"),
        (Environment.SpecialFolder.ApplicationData, "vATIS"),
        (Environment.SpecialFolder.ProgramFiles, "vATIS"),
        (Environment.SpecialFolder.ProgramFilesX86, "vATIS")
    }.Select(item => Path.Combine(Environment.GetFolderPath(item.Item1), item.Item2)).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool AnyRegistration()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                if (name.Equals(SoftwareInstaller.VatisId, StringComparison.OrdinalIgnoreCase) || name.Equals("vATIS", StringComparison.OrdinalIgnoreCase)) return true;
                using var key = uninstall.OpenSubKey(name);
                var display = key?.GetValue("DisplayName") as string;
                if (display != null && (display.Equals("vATIS", StringComparison.OrdinalIgnoreCase) || display.StartsWith("vATIS ", StringComparison.OrdinalIgnoreCase))) return true;
            }
        }
        return false;
    }

    private static bool MatchesRegistration(string root)
    {
        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = currentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + SoftwareInstaller.VatisId);
        string Value(string name) => key?.GetValue(name) as string ?? "";
        return Value("DisplayName") == "vATIS" && Value("Publisher") == "Justin Shannon" &&
            SoftwareInstaller.SamePath(Value("InstallLocation"), root) &&
            SoftwareInstaller.SamePath(Value("DisplayIcon"), Path.Combine(root, "current", "vATIS.exe")) &&
            Value("UninstallString").Equals('"' + Path.Combine(root, "Update.exe") + "\" --uninstall", StringComparison.OrdinalIgnoreCase) &&
            Value("QuietUninstallString").Equals('"' + Path.Combine(root, "Update.exe") + "\" --uninstall --silent", StringComparison.OrdinalIgnoreCase);
    }
}
