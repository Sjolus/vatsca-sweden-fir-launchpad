using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

int passed = 0;
await Test("selects highest stable and skips drafts and semantic prereleases", async () =>
{
    using var fixture = new Fixture();
    fixture.Catalog = "[" + string.Join(",", fixture.ReleaseJson("1.3.37"), fixture.ReleaseJson("2.0.0", draft: true),
        fixture.ReleaseJson("3.0.0", prerelease: true), fixture.ReleaseJson("4.0.0-beta.1"), fixture.ReleaseJson("1.3.36")) + "]";
    Equal("1.3.37", (await fixture.Releases.GetLatestAsync())!.Version);
    Equal(0, fixture.AssetRequests);
});
await Test("rejects newest release without a verified asset instead of falling back", async () =>
{
    using var fixture = new Fixture();
    fixture.Catalog = "[" + fixture.ReleaseJson("1.3.36") + "," + fixture.ReleaseJson("1.3.37").Replace("\"sha256:", "\"unsupported:") + "]";
    await ThrowsAsync<InvalidDataException>(() => fixture.Releases.GetLatestAsync());
});
await Test("rejects duplicate stable versions and incomplete publication status", async () =>
{
    using var fixture = new Fixture();
    fixture.Catalog = "[" + fixture.ReleaseJson("1.3.37") + "," + fixture.ReleaseJson("1.3.37") + "]";
    await ThrowsAsync<InvalidDataException>(() => fixture.Releases.GetLatestAsync());
    fixture.Catalog = "[" + fixture.ReleaseJson("1.3.37").Replace("\"draft\":false,", "") + "]";
    await ThrowsAsync<InvalidDataException>(() => fixture.Releases.GetLatestAsync());
});
await Test("rejects off-origin assets and redirects", async () =>
{
    using var fixture = new Fixture();
    fixture.Catalog = "[" + fixture.ReleaseJson("1.3.37").Replace("https://github.com/", "https://evil.example/") + "]";
    await ThrowsAsync<InvalidDataException>(() => fixture.Releases.GetLatestAsync());
    fixture.Catalog = "[" + fixture.ReleaseJson("1.3.37") + "]";
    var release = (await fixture.Releases.GetLatestAsync())!;
    fixture.Redirect = "https://example.com/SAAMAN.dll";
    await ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Installer.Preview(fixture.Settings, release), fixture.Settings));
    False(File.Exists(fixture.Target));
});
await Test("updates one DLL and restores verified previous bytes without personal data changes", async () =>
{
    using var fixture = new Fixture();
    fixture.Existing("1.3.36");
    var release = (await fixture.Releases.GetLatestAsync())!;
    var plan = fixture.Installer.Preview(fixture.Settings, release);
    var installed = await fixture.Installer.InstallAsync(plan, fixture.Settings);
    Equal("1.3.37", fixture.Inspect(fixture.Target).Version);
    True(File.Exists(installed.ReceiptPath));
    foreach (string name in new[] { "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "SHA256SUMS.txt", "previous.dll" })
        True(File.Exists(Path.Combine(Path.GetDirectoryName(installed.ReceiptPath)!, name)));
    var recovery = fixture.Installer.FindRecovery(fixture.Target)!;
    fixture.Installer.Restore(recovery, fixture.Settings);
    Equal("1.3.36", fixture.Inspect(fixture.Target).Version);
    Equal("Personal data remains", File.ReadAllText(fixture.Personal));
    True(fixture.Installer.FindRecovery(fixture.Target) is null);
});
await Test("fresh install has no destructive restore operation", async () =>
{
    using var fixture = new Fixture();
    var release = (await fixture.Releases.GetLatestAsync())!;
    var result = await fixture.Installer.InstallAsync(fixture.Installer.Preview(fixture.Settings, release), fixture.Settings);
    True(fixture.Installer.FindRecovery(fixture.Target) is null);
    var receipt = JsonSerializer.Deserialize<SaamanRecovery>(File.ReadAllText(result.ReceiptPath))!;
    Throws<InvalidOperationException>(() => fixture.Installer.Restore(receipt, fixture.Settings));
    Equal("1.3.37", fixture.Inspect(fixture.Target).Version);
});
await Test("changed DLL and changed recovery backup cannot be overwritten", async () =>
{
    using var fixture = new Fixture();
    fixture.Existing("1.3.36");
    var release = (await fixture.Releases.GetLatestAsync())!;
    await fixture.Installer.InstallAsync(fixture.Installer.Preview(fixture.Settings, release), fixture.Settings);
    var recovery = fixture.Installer.FindRecovery(fixture.Target)!;
    fixture.Existing("1.3.38");
    Throws<IOException>(() => fixture.Installer.Restore(recovery, fixture.Settings));
    Equal("1.3.38", fixture.Inspect(fixture.Target).Version);
    fixture.Existing("1.3.37");
    File.WriteAllText(recovery.BackupPath, "edited");
    Throws<IOException>(() => fixture.Installer.Restore(recovery, fixture.Settings));
    Equal("1.3.37", fixture.Inspect(fixture.Target).Version);
});
await Test("stale review, downgrade, and configured-path overlaps are rejected", async () =>
{
    using var fixture = new Fixture();
    fixture.Existing("1.3.36");
    var release = (await fixture.Releases.GetLatestAsync())!;
    var plan = fixture.Installer.Preview(fixture.Settings, release);
    fixture.Existing("1.3.35");
    await ThrowsAsync<IOException>(() => fixture.Installer.InstallAsync(plan, fixture.Settings));
    Equal("1.3.35", fixture.Inspect(fixture.Target).Version);
    fixture.Existing("1.3.38");
    Throws<InvalidOperationException>(() => fixture.Installer.Preview(fixture.Settings, release));
    fixture.Settings.VatEfsPath = Path.GetDirectoryName(fixture.Target)!;
    Throws<InvalidOperationException>(() => fixture.Installer.Preview(fixture.Settings, release));
});
await Test("checksum, DLL version, process guard, and cancellation failures preserve installed file", async () =>
{
    using var fixture = new Fixture();
    fixture.Existing("1.3.36");
    var release = (await fixture.Releases.GetLatestAsync())!;
    var plan = fixture.Installer.Preview(fixture.Settings, release);
    fixture.CorruptDownload = true;
    await ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(plan, fixture.Settings));
    Equal("1.3.36", fixture.Inspect(fixture.Target).Version);
    fixture.CorruptDownload = false;
    fixture.Closed = false;
    await ThrowsAsync<InvalidOperationException>(() => fixture.Installer.InstallAsync(plan, fixture.Settings));
    Equal("1.3.36", fixture.Inspect(fixture.Target).Version);
    fixture.Closed = true;
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => fixture.Installer.InstallAsync(plan, fixture.Settings, cancellationToken: cancellation.Token));
    Equal("1.3.36", fixture.Inspect(fixture.Target).Version);
});
await Test("DLL registration and checksum manifest must agree with reviewed release metadata", async () =>
{
    using var fixture = new Fixture();
    fixture.Existing("1.3.36");
    fixture.Payloads["SAAMAN.dll"] = Encoding.UTF8.GetBytes("SAAMAN 1.3.38");
    fixture.Catalog = "[" + fixture.ReleaseJson("1.3.37") + "]";
    var release = (await fixture.Releases.GetLatestAsync())!;
    await ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Installer.Preview(fixture.Settings, release), fixture.Settings));
    Equal("1.3.36", fixture.Inspect(fixture.Target).Version);
    fixture.Payloads["SAAMAN.dll"] = Encoding.UTF8.GetBytes("SAAMAN 1.3.37");
    fixture.Payloads["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(new string('0', 64) + "  SAAMAN.dll");
    fixture.Catalog = "[" + fixture.ReleaseJson("1.3.37") + "]";
    release = (await fixture.Releases.GetLatestAsync())!;
    await ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Installer.Preview(fixture.Settings, release), fixture.Settings));
    Equal("1.3.36", fixture.Inspect(fixture.Target).Version);
});
await Test("active maintenance lease blocks install before downloading", async () =>
{
    using var fixture = new Fixture();
    var release = (await fixture.Releases.GetLatestAsync())!;
    var plan = fixture.Installer.Preview(fixture.Settings, release);
    True(MaintenanceLock.TryAcquire(out var held));
    using (held)
        await ThrowsAsync<InvalidOperationException>(() => fixture.Installer.InstallAsync(plan, fixture.Settings));
    Equal(0, fixture.AssetRequests);
    False(File.Exists(fixture.Target));
});
await Test("pending transaction retains original and recovery permits a later retry", async () =>
{
    using var fixture = new Fixture();
    fixture.Existing("1.3.36");
    var release = (await fixture.Releases.GetLatestAsync())!;
    var plan = fixture.Installer.Preview(fixture.Settings, release);
    fixture.FailGuardAt = 3; // After the durable receipt and verified backup, before deleting original.
    await ThrowsAsync<InvalidOperationException>(() => fixture.Installer.InstallAsync(plan, fixture.Settings));
    Equal("1.3.36", fixture.Inspect(fixture.Target).Version);
    Throws<InvalidOperationException>(() => fixture.Installer.Preview(fixture.Settings, release));
    fixture.FailGuardAt = 0;
    int requests = fixture.AssetRequests;
    await ThrowsAsync<InvalidOperationException>(() => fixture.Installer.InstallAsync(plan, fixture.Settings));
    Equal(requests, fixture.AssetRequests);
    fixture.Installer.Restore(fixture.Installer.FindRecovery(fixture.Target)!, fixture.Settings);
    await fixture.Installer.InstallAsync(fixture.Installer.Preview(fixture.Settings, release), fixture.Settings);
    Equal("1.3.37", fixture.Inspect(fixture.Target).Version);
});
await Test("real static inspector rejects a text DLL without executing it", () =>
{
    using var fixture = new Fixture();
    fixture.Existing("1.3.36");
    Throws<BadImageFormatException>(() => SaamanInstallService.InspectDll(fixture.Target));
    return Task.CompletedTask;
});
await Test("renamed DLL adoption is rejected with the required file name", () =>
{
    using var fixture = new Fixture();
    fixture.Existing("1.3.36");
    string renamed = Path.Combine(Path.GetDirectoryName(fixture.Target)!, "MyArrivalManager.dll");
    File.Copy(fixture.Target, renamed);
    try { fixture.Installer.Adopt(renamed, fixture.Settings); throw new Exception("Expected renamed DLL rejection."); }
    catch (InvalidOperationException ex) { True(ex.Message.Contains("SAAMAN.dll", StringComparison.Ordinal)); }
    Equal("", fixture.Settings.SaamanDllPath);
    True(File.Exists(renamed));
    return Task.CompletedTask;
});
Console.WriteLine($"PASS: {passed} SAAMAN release, install and recovery scenarios.");

async Task Test(string name, Func<Task> test)
{
    await test(); passed++; Console.WriteLine("PASS " + name);
}
static void True(bool condition) { if (!condition) throw new Exception("Expected true."); }
static void False(bool condition) => True(!condition);
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class Fixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Launchpad-Saaman-Tests-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _client;
    internal string Target { get; }
    internal string Personal { get; }
    internal AppSettings Settings { get; }
    internal SaamanReleaseService Releases { get; }
    internal SaamanInstallService Installer { get; }
    internal Dictionary<string, byte[]> Payloads { get; } = new(StringComparer.Ordinal);
    internal string Catalog { get; set; }
    internal string? Redirect { get; set; }
    internal bool CorruptDownload { get; set; }
    internal bool Closed { get; set; } = true;
    internal int FailGuardAt { get; set; }
    private int _guardCalls;
    internal int AssetRequests { get; private set; }
    internal Fixture()
    {
        string data = Path.Combine(_root, "GNG");
        Directory.CreateDirectory(data);
        Target = Path.Combine(data, "ESAA", "Plugins", "SAAMAN", "SAAMAN.dll");
        Personal = Path.Combine(data, "personal.txt");
        File.WriteAllText(Personal, "Personal data remains");
        Settings = new AppSettings { EuroscopeDataPath = data };
        Payloads["SAAMAN.dll"] = Encoding.UTF8.GetBytes("SAAMAN 1.3.37");
        Payloads["LICENSE.txt"] = Encoding.UTF8.GetBytes("Synthetic license");
        Payloads["THIRD-PARTY-NOTICES.txt"] = Encoding.UTF8.GetBytes("Synthetic notices");
        Payloads["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(string.Join("\n", Payloads.Select(pair => Hash(pair.Value) + "  " + pair.Key)));
        Catalog = "[" + ReleaseJson("1.3.37") + "]";
        _client = new HttpClient(new Handler(this));
        Releases = new SaamanReleaseService(_client);
        Installer = new SaamanInstallService(Releases, Path.Combine(_root, "LaunchpadRecovery"), () =>
        {
            if (!Closed || ++_guardCalls == FailGuardAt) throw new InvalidOperationException("Synthetic EuroScope guard blocked.");
        }, Inspect);
    }
    internal SaamanDllInfo Inspect(string path)
    {
        var snapshot = SaamanFileSafety.Snapshot(path);
        string value = File.ReadAllText(path);
        if (!value.StartsWith("SAAMAN ") || !SoftwareVersion.TryParse(value[7..], out var version))
            throw new InvalidDataException("Synthetic SAAMAN identity absent.");
        return new(SaamanFileSafety.Canonical(path), version.Value, snapshot.Size, snapshot.Hash);
    }
    internal void Existing(string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllBytes(Target, Encoding.UTF8.GetBytes("SAAMAN " + version));
    }
    internal string ReleaseJson(string version, bool draft = false, bool prerelease = false) => JsonSerializer.Serialize(new
    {
        tag_name = "v" + version, draft, prerelease, published_at = "2026-10-19T12:00:00Z",
        assets = Payloads.Select(pair => new
        {
            name = pair.Key, state = "uploaded", size = pair.Value.Length, digest = "sha256:" + Hash(pair.Value),
            browser_download_url = $"https://github.com/Kaskobiii/SAAMAN/releases/download/v{version}/{pair.Key}"
        }).ToArray()
    });
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public void Dispose()
    {
        _client.Dispose();
        string full = Path.GetFullPath(_root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "Launchpad-Saaman-Tests-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid synthetic cleanup path.");
        Directory.Delete(full, recursive: true);
    }
    private sealed class Handler(Fixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.Host == "api.github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fixture.Catalog), RequestMessage = request });
            fixture.AssetRequests++;
            if (fixture.Redirect is not null)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
                response.Headers.Location = new Uri(fixture.Redirect);
                return Task.FromResult(response);
            }
            string name = Path.GetFileName(request.RequestUri.AbsolutePath);
            byte[] payload = fixture.Payloads[name];
            if (fixture.CorruptDownload && name == "SAAMAN.dll") payload = Enumerable.Repeat((byte)0, payload.Length).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload), RequestMessage = request });
        }
    }
}
