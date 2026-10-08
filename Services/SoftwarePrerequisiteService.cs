using System.IO;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public sealed record SoftwarePrerequisiteResult(bool RestartRequired);

/// <summary>Explicit first-install prerequisites. Checking never downloads or installs anything.</summary>
public static class SoftwarePrerequisiteService
{
    public const string WebViewDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    public const string VisualCppDownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    internal static readonly Version WebViewBootstrapperBaseline = new(1, 3, 275, 13);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static string? GetProblem(SoftwareApp app) => GetProblem(app, new SoftwarePrerequisiteEnvironment());

    internal static string? GetProblem(SoftwareApp app, SoftwarePrerequisiteEnvironment environment)
    {
        ValidateApp(app);
        if (app == SoftwareApp.VatEfs) return EuroScopePrerequisiteService.GetProblem(X86Environment(environment));
        if (app == SoftwareApp.Vatis) return null;
        try
        {
            if (IsInstalled(app, environment)) return null;
            return app == SoftwareApp.Vacs
                ? "Machine-wide Microsoft Edge WebView2 Runtime is required for the VACS all-users installation. Use the separate Install runtime action."
                : $"Microsoft Visual C++ x64 runtime {EuroScopePrerequisiteService.MinimumVersion} or newer is required. Use the separate Install runtime action.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return "The Microsoft prerequisite could not be checked. Use Microsoft's official installer, then check again."; }
    }

    public static Task<SoftwarePrerequisiteResult> InstallAsync(SoftwareApp app, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, Action? onRestartRequired = null) =>
        InstallAsync(app, new SoftwarePrerequisiteEnvironment(), Http, progress, cancellationToken, onRestartRequired);

    internal static async Task<SoftwarePrerequisiteResult> InstallAsync(SoftwareApp app, SoftwarePrerequisiteEnvironment environment,
        HttpClient http, IProgress<string>? progress = null, CancellationToken cancellationToken = default, Action? onRestartRequired = null)
    {
        ValidateApp(app);
        if (app == SoftwareApp.VatEfs)
        {
            var result = await EuroScopePrerequisiteService.InstallAsync(X86Environment(environment), http,
                progress, cancellationToken, onRestartRequired).ConfigureAwait(false);
            return new(result.RestartRequired);
        }
        if (environment.DownloadTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(environment.DownloadTimeout));
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? folder = null;
        string? package = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (app == SoftwareApp.Vatis || IsInstalled(app, environment))
            { progress?.Report("No additional Microsoft prerequisite installation is needed."); return new(false); }
            var name = app == SoftwareApp.Vacs ? "Microsoft Edge WebView2 Runtime" : "Microsoft Visual C++ x64 runtime";
            var storage = Path.Combine(environment.LocalAppData, "VatscaUpdateChecker", "SoftwareUpdates", "Prerequisites");
            SoftwareInstaller.RejectReparse(storage);
            Directory.CreateDirectory(storage);
            folder = Path.Combine(storage, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            SoftwareInstaller.RejectReparse(folder);
            package = Path.Combine(folder, app == SoftwareApp.Vacs ? "MicrosoftEdgeWebview2Setup.exe" : "vc_redist.x64.exe");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(environment.DownloadTimeout);
            try
            {
                await EuroScopePrerequisiteService.DownloadMicrosoftPackageAsync(http, package,
                    new Uri(app == SoftwareApp.Vacs ? WebViewDownloadUrl : VisualCppDownloadUrl),
                    address => ValidateAddress(app, address), name, app == SoftwareApp.Vacs ? 32L * 1024 * 1024 : 64L * 1024 * 1024,
                    progress, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new IOException("The Microsoft prerequisite download timed out. Retry or use Microsoft's official installer."); }

            progress?.Report($"Verifying Microsoft's {name} installer…");
            SoftwareInstaller.RejectReparse(package);
            using var immutablePackage = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
            VerifyPackage(app, package, environment);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInstalled(app, environment)) return new(false);

            progress?.Report($"Installing {name} for all users. Accept the Windows elevation prompt and wait for completion.");
            // Both prerequisites are machine-wide. Never pass cancellation to, kill, or retry a started installer.
            var arguments = app == SoftwareApp.Vacs ? new[] { "/silent", "/install" } : new[] { "/install", "/quiet", "/norestart" };
            int exit = await environment.RunAsync(new(package, arguments, true)).ConfigureAwait(false);
            // Report the native request even if registration verification subsequently fails.
            if (exit == 3010) onRestartRequired?.Invoke();
            if (exit == 1223) throw new IOException("Microsoft prerequisite installation was cancelled at the Windows elevation prompt.");
            if (exit is not (0 or 3010)) throw new IOException($"Microsoft prerequisite installation failed (exit code {exit}). Use Microsoft's official installer if retrying does not resolve it.");
            // Edge Update can finish registration just after its bootstrapper exits; wait without launching a runtime.
            bool installed = IsInstalled(app, environment);
            for (int attempt = 0; !installed && app == SoftwareApp.Vacs && attempt < 15; attempt++)
            {
                await environment.DelayAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                installed = IsInstalled(app, environment);
            }
            if (!installed) throw new IOException("The installer finished, but the machine-wide Microsoft prerequisite could not be confirmed. Restart Windows if requested, then check again.");
            progress?.Report(exit == 3010 ? $"{name} installed. Restart Windows before installing the application." : $"{name} installed.");
            return new(exit == 3010);
        }
        finally
        {
            if (package != null && folder != null)
                try
                {
                    SoftwareInstaller.RejectReparse(folder);
                    SoftwareInstaller.RejectReparse(package);
                    if (File.Exists(package)) File.Delete(package);
                    Directory.Delete(folder, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException or ArgumentException) { }
            Gate.Release();
        }
    }

    private static bool IsInstalled(SoftwareApp app, SoftwarePrerequisiteEnvironment environment)
    {
        var version = environment.ReadInstalledVersion(app);
        return app == SoftwareApp.Vacs ? version != null && version > new Version(0, 0, 0, 0) : EuroScopePrerequisiteService.MeetsBaseline(version);
    }

    internal static void VerifyPackage(SoftwareApp app, string path, SoftwarePrerequisiteEnvironment environment)
    {
        ValidateApp(app);
        if (app == SoftwareApp.Vatis) throw new InvalidOperationException("vATIS has no Microsoft prerequisite package in this workflow.");
        if (app == SoftwareApp.VatEfs)
        {
            EuroScopePrerequisiteService.VerifyPackage(path, X86Environment(environment));
            return;
        }
        var binary = environment.ReadBinary(path);
        // Microsoft's x64 Burn installer and architecture-selecting WebView bootstrapper are both x86 PE files.
        bool valid = binary.Machine == Machine.I386 && binary.CompanyName == "Microsoft Corporation" &&
            Version.TryParse(binary.ProductVersion, out var version) && (app == SoftwareApp.Vacs
                ? binary.OriginalFilename.Equals("MicrosoftEdgeUpdateSetup.exe", StringComparison.OrdinalIgnoreCase) &&
                  binary.ProductName == "Microsoft Edge Update" && version.Major == 1 && version >= WebViewBootstrapperBaseline
                : binary.OriginalFilename.Equals("VC_redist.x64.exe", StringComparison.OrdinalIgnoreCase) &&
                  Regex.IsMatch(binary.ProductName, @"^Microsoft Visual C\+\+ (?:2015-2022|v14) Redistributable \(x64\)(?: - [0-9.]+)?$", RegexOptions.CultureInvariant) &&
                  EuroScopePrerequisiteService.MeetsBaseline(version));
        if (!valid) throw new InvalidDataException("The download is not the supported Microsoft prerequisite installer.");
        environment.VerifyPublisher(path, "Microsoft Corporation");
    }

    internal static void ValidateAddress(SoftwareApp app, Uri address)
    {
        if (app is SoftwareApp.TrackAudio or SoftwareApp.VatEfs) { EuroScopePrerequisiteService.ValidateAddress(address); return; }
        if (app != SoftwareApp.Vacs || !address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps || address.Port != 443 || address.UserInfo.Length != 0 ||
            !new[] { "go.microsoft.com", "msedge.sf.dl.delivery.mp.microsoft.com", "msedge.sf.tlu.dl.delivery.mp.microsoft.com", "download.microsoft.com" }.Contains(address.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The Microsoft prerequisite download used an unsupported source. Use Microsoft's official download page.");
    }

    private static void ValidateApp(SoftwareApp app)
    {
        if (app is not (SoftwareApp.Vacs or SoftwareApp.TrackAudio or SoftwareApp.Vatis or SoftwareApp.VatEfs)) throw new ArgumentOutOfRangeException(nameof(app));
    }

    // VatEFS's EuroScope plugin uses the same x86 runtime as EuroScope itself.
    private static EuroScopePrerequisiteEnvironment X86Environment(SoftwarePrerequisiteEnvironment environment) => new()
    {
        LocalAppData = environment.LocalAppData,
        DownloadTimeout = environment.DownloadTimeout,
        ReadInstalledVersion = () => environment.ReadInstalledVersion(SoftwareApp.VatEfs),
        ReadBinary = environment.ReadBinary,
        VerifyPublisher = environment.VerifyPublisher,
        RunAsync = environment.RunAsync
    };
}

internal sealed class SoftwarePrerequisiteEnvironment
{
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public Func<SoftwareApp, Version?> ReadInstalledVersion { get; init; } = ReadMachineVersion;
    public Func<string, EuroScopeRuntimeBinary> ReadBinary { get; init; } = EuroScopePrerequisiteEnvironment.ReadPackageIdentity;
    public Action<string, string> VerifyPublisher { get; init; } = SoftwareInstallerNative.VerifyPublisher;
    public Func<SoftwareInstallCommand, Task<int>> RunAsync { get; init; } = SoftwareInstallerNative.RunAsync;
    public Func<TimeSpan, Task> DelayAsync { get; init; } = duration => Task.Delay(duration);

    private static Version? ReadMachineVersion(SoftwareApp app)
    {
        if (app == SoftwareApp.TrackAudio) return EuroScopePrerequisiteEnvironment.ReadRuntimeVersion("x64");
        if (app == SoftwareApp.VatEfs) return EuroScopePrerequisiteEnvironment.ReadRuntimeVersion("x86");
        if (app != SoftwareApp.Vacs) return null;
        // VACS fresh install uses AllUsers. HKCU alone is insufficient when UAC uses another account.
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", writable: false);
        return Version.TryParse(key?.GetValue("pv") as string, out var version) ? version : null;
    }
}
