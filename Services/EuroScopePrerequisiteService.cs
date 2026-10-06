using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VatscaUpdateChecker.Services;

public sealed record EuroScopePrerequisiteResult(bool RestartRequired);

/// <summary>Reads the x86 VC runtime prerequisite; setup runs only through an explicit InstallAsync call.</summary>
public static class EuroScopePrerequisiteService
{
    public const string DownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x86.exe";
    // Reviewed version delivered by the VATSCA-linked Microsoft URL. Newer v14 runtimes are accepted.
    // This is our supported baseline, not a claim about every plugin's minimum build-tools version.
    internal static readonly Version MinimumVersion = new(14, 44, 35211, 0);
    internal const long MaximumDownloadBytes = 64L * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static bool IsInstalled() => GetProblem() == null;
    public static string? GetProblem() => GetProblem(new EuroScopePrerequisiteEnvironment());

    internal static string? GetProblem(EuroScopePrerequisiteEnvironment environment)
    {
        try
        {
            return MeetsBaseline(environment.ReadInstalledVersion()) ? null :
                $"Microsoft Visual C++ x86 runtime {MinimumVersion} or newer is required. Use the separate Install runtime action or Microsoft's official installer.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return "The Microsoft Visual C++ x86 runtime could not be checked. Use Microsoft's official installer, then check again."; }
    }

    public static Task<EuroScopePrerequisiteResult> InstallAsync(IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, Action? onRestartRequired = null) =>
        InstallAsync(new EuroScopePrerequisiteEnvironment(), Http, progress, cancellationToken, onRestartRequired);

    internal static async Task<EuroScopePrerequisiteResult> InstallAsync(EuroScopePrerequisiteEnvironment environment,
        HttpClient http, IProgress<string>? progress = null, CancellationToken cancellationToken = default, Action? onRestartRequired = null)
    {
        if (environment.DownloadTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(environment.DownloadTimeout));
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? folder = null;
        string? package = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MeetsBaseline(environment.ReadInstalledVersion()))
            { progress?.Report("The supported Microsoft Visual C++ x86 runtime is already installed."); return new(false); }

            var storage = Path.Combine(environment.LocalAppData, "VatscaUpdateChecker", "EuroScope", "Prerequisites");
            SoftwareInstaller.RejectReparse(storage);
            Directory.CreateDirectory(storage);
            folder = Path.Combine(storage, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            SoftwareInstaller.RejectReparse(folder);
            package = Path.Combine(folder, "vc_redist.x86.exe");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(environment.DownloadTimeout);
            try { await DownloadAsync(http, package, progress, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new IOException("The Microsoft runtime download timed out. Retry or use Microsoft's official installer."); }

            progress?.Report("Verifying Microsoft's x86 runtime installer…");
            SoftwareInstaller.RejectReparse(package);
            // Keep the downloaded file immutable through signature checks and process completion.
            using var immutablePackage = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
            VerifyPackage(package, environment);
            cancellationToken.ThrowIfCancellationRequested();
            if (MeetsBaseline(environment.ReadInstalledVersion())) return new(false);

            progress?.Report("Installing Microsoft Visual C++ x86 runtime. Accept the Windows elevation prompt and wait for completion.");
            // Shared machine prerequisite: explicit elevation is intentional. No cancellation/kill once started.
            int exit = await environment.RunAsync(new(package, new[] { "/install", "/quiet", "/norestart" }, true)).ConfigureAwait(false);
            // Report the native request even if registration verification subsequently fails.
            if (exit == 3010) onRestartRequired?.Invoke();
            if (exit == 1223) throw new IOException("Microsoft runtime installation was cancelled at the Windows elevation prompt.");
            if (exit is not (0 or 3010)) throw new IOException($"Microsoft runtime installation failed (exit code {exit}). Use Microsoft's official installer if retrying does not resolve it.");
            if (!MeetsBaseline(environment.ReadInstalledVersion()))
                throw new IOException("The installer finished, but the supported Microsoft Visual C++ x86 runtime could not be confirmed. Restart Windows if requested, then check again.");
            progress?.Report(exit == 3010 ? "Microsoft runtime installed. Restart Windows before using EuroScope." : "Microsoft Visual C++ x86 runtime installed.");
            return new(exit == 3010);
        }
        finally
        {
            // Only our exact temporary package and now-empty unique folder. Never remove shared runtimes.
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

    internal static void VerifyPackage(string path, EuroScopePrerequisiteEnvironment environment)
    {
        var binary = environment.ReadBinary(path);
        if (binary.Machine != Machine.I386 || !binary.OriginalFilename.Equals("VC_redist.x86.exe", StringComparison.OrdinalIgnoreCase) ||
            !binary.CompanyName.Equals("Microsoft Corporation", StringComparison.Ordinal) ||
            !Regex.IsMatch(binary.ProductName, @"^Microsoft Visual C\+\+ (?:2015-2022|v14) Redistributable \(x86\)(?: - [0-9.]+)?$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(binary.ProductVersion, out var version) || !MeetsBaseline(version))
            throw new InvalidDataException("The download is not the supported Microsoft Visual C++ x86 runtime installer.");
        environment.VerifyPublisher(path, "Microsoft Corporation");
    }

    internal static bool MeetsBaseline(Version? version) => version is { Major: 14 } && version >= MinimumVersion;

    internal static async Task DownloadAsync(HttpClient http, string path, IProgress<string>? progress, CancellationToken token)
        => await DownloadMicrosoftPackageAsync(http, path, new Uri(DownloadUrl), ValidateAddress,
            "Microsoft Visual C++ x86 runtime", MaximumDownloadBytes, progress, token).ConfigureAwait(false);

    // Shared transport policy for the separately confirmed Microsoft prerequisite actions.
    internal static async Task DownloadMicrosoftPackageAsync(HttpClient http, string path, Uri address,
        Action<Uri> validateAddress, string name, long maximumBytes, IProgress<string>? progress, CancellationToken token)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            validateAddress(address);
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("VatscaUpdateChecker/2.0");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirects == 5 || response.Headers.Location == null) throw new IOException("The Microsoft runtime download redirected too many times or had no destination.");
                address = new Uri(address, response.Headers.Location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            // The supplied client must not silently follow a redirect past our per-hop policy.
            if (response.RequestMessage?.RequestUri is { } finalAddress && finalAddress != address)
                throw new IOException("Runtime downloads require a client with automatic redirects disabled.");
            var expected = response.Content.Headers.ContentLength;
            if (expected is <= 0 || expected > maximumBytes) throw new InvalidDataException("The Microsoft runtime download has an invalid size.");
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            int lastPercent = -1;
            progress?.Report($"Downloading the {name}…");
            while (true)
            {
                int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > maximumBytes || expected.HasValue && total > expected.Value)
                    throw new InvalidDataException("The Microsoft runtime download exceeded its expected size.");
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                if (expected is > 0)
                {
                    int percent = (int)(total * 100 / expected.Value);
                    if (percent != lastPercent) { progress?.Report($"Downloading the {name}… {percent}%"); lastPercent = percent; }
                }
            }
            if (total == 0 || expected.HasValue && total != expected.Value) throw new InvalidDataException("The Microsoft runtime download was incomplete.");
            return;
        }
    }

    internal static void ValidateAddress(Uri address)
    {
        if (!address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps || address.Port != 443 || address.UserInfo.Length != 0 ||
            !new[] { "aka.ms", "download.visualstudio.microsoft.com", "download.microsoft.com" }.Contains(address.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The Microsoft runtime download used an unsupported source. Use Microsoft's official download page.");
    }
}

internal sealed record EuroScopeRuntimeBinary(Machine Machine, string ProductName, string ProductVersion,
    string CompanyName, string OriginalFilename);

/// <summary>Tests replace registry, PE/trust inspection and process execution; construction has no side effects.</summary>
internal sealed class EuroScopePrerequisiteEnvironment
{
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public Func<Version?> ReadInstalledVersion { get; init; } = ReadRuntimeVersion;
    public Func<string, EuroScopeRuntimeBinary> ReadBinary { get; init; } = ReadPackageIdentity;
    public Action<string, string> VerifyPublisher { get; init; } = SoftwareInstallerNative.VerifyPublisher;
    public Func<SoftwareInstallCommand, Task<int>> RunAsync { get; init; } = SoftwareInstallerNative.RunAsync;

    private static Version? ReadRuntimeVersion() => ReadRuntimeVersion("x86");

    internal static Version? ReadRuntimeVersion(string architecture)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, architecture == "x86" ? RegistryView.Registry32 : RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + architecture, writable: false);
        if (key?.GetValue("Installed") is not int installed || installed != 1) return null;
        var text = (key.GetValue("Version") as string)?.TrimStart('v', 'V');
        if (Version.TryParse(text, out var version)) return version;
        if (key.GetValue("Major") is int major && key.GetValue("Minor") is int minor && key.GetValue("Bld") is int build && key.GetValue("Rbld") is int revision &&
            major >= 0 && minor >= 0 && build >= 0 && revision >= 0)
            return new(major, minor, build, revision);
        return null;
    }

    internal static EuroScopeRuntimeBinary ReadPackageIdentity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pe = new PEReader(stream);
        var version = FileVersionInfo.GetVersionInfo(path);
        return new(pe.PEHeaders.CoffHeader.Machine, version.ProductName ?? "", version.ProductVersion ?? "",
            version.CompanyName ?? "", version.OriginalFilename ?? "");
    }
}
