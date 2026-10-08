using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

public static class SourceTests
{
    public static int Passed { get; private set; }
    private const string Hash = "ab973c5ebaad926fbf0da185da2cc8abb6f3e2852d36dde7d6fe9a4c3b1fb7c3";

    public static async Task RunAsync()
    {
        Passed = 0;
        await Run("SemVer precedence preserves beta numbers and ignores build metadata", () =>
        {
            string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1"];
            for (int i = 1; i < ordered.Length; i++) Compare(ordered[i - 1], ordered[i], -1);
            Compare("4.1.0-beta.19", "4.1.0-beta.2", 1);
            Compare("1.2.3+first.001", "1.2.3+second", 0);
            Compare("999999999999999999999.0.0", "999999999999999999998.0.0", 1);
            Compare("1.0.0-999999999999999999999", "1.0.0-999999999999999999998", 1);
            Compare("1.0.0-BETA", "1.0.0-beta", -1);
            return Task.CompletedTask;
        });
        await Run("malformed versions are never normalized into newer releases", () =>
        {
            string[] invalid = ["", "1", "1.2", "1.2.3.4", "v1.2.3", " 1.2.3", "1.2.3\n", "01.2.3", "1.02.3", "1.2.03", "1.2.3-beta.01", "1.2.3-", "1.2.3+", "1.2.3-beta..2", "1.2.3-β", "1.2.3+build/path"];
            foreach (string version in invalid)
                Assert(!SoftwareVersion.TryCompare(version, "1.2.3", out _), $"Accepted invalid version {version}");
            return Task.CompletedTask;
        });
        await Run("VACS selects only the stable client Windows x64 setup", async () =>
        {
            var actual = await Latest(SoftwareApp.Vacs, GitHub(
                Release(SoftwareApp.Vacs, "99.0.0", tag: "vacs-server-v99.0.0"),
                Release(SoftwareApp.Vacs, "3.0.0-rc.2", prerelease: true),
                Release(SoftwareApp.Vacs, "2.8.0"), Release(SoftwareApp.Vacs, "2.7.0")));
            Assert(actual is { Version: "2.8.0", FileName: "vacs_2.8.0_x64-setup.exe" }, "Wrong VACS release");
            Assert(actual!.Sha256 == Hash.ToUpperInvariant() && actual.Size == 11164891, "Metadata was lost");
        });
        await Run("TrackAudio excludes falsely stable beta tags and actual prereleases/drafts", async () =>
        {
            var actual = await Latest(SoftwareApp.TrackAudio, GitHub(
                Release(SoftwareApp.TrackAudio, "2.0.0-beta.9"),
                Release(SoftwareApp.TrackAudio, "1.9.0", prerelease: true),
                Release(SoftwareApp.TrackAudio, "1.8.0", draft: true),
                Release(SoftwareApp.TrackAudio, "1.4.0"), Release(SoftwareApp.TrackAudio, "1.3.3")));
            Assert(actual is { Version: "1.4.0", FileName: "trackaudio-1.4.0-x64-setup.exe" }, "Wrong TrackAudio release");
        });
        await Run("a newer installed beta cannot be downgraded by a stable-only catalog", async () =>
        {
            var actual = await Latest(SoftwareApp.TrackAudio, GitHub(Release(SoftwareApp.TrackAudio, "1.4.0")), "1.5.0-beta.1");
            Assert(actual is not null, "Missing official stable release");
            Compare(actual!.Version, "1.5.0-beta.1", -1); // The coordinator refuses this comparison.
        });
        await Run("VatEFS fresh installs and upgrades select the newest published prerelease MSI", async () =>
        {
            string catalog = GitHub(Release(SoftwareApp.VatEfs, "0.0.14"), Release(SoftwareApp.VatEfs, "0.0.15", prerelease: true),
                Release(SoftwareApp.VatEfs, "0.0.16", draft: true));
            var actual = await Latest(SoftwareApp.VatEfs, catalog, "0.0.12");
            Assert(actual is { Version: "0.0.15", FileName: "vatefs-0.0.15.msi", IsPrerelease: true }, "VatEFS prerelease MSI was not selected");
            using var client = Client((request, _) =>
            {
                Assert(request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/minsulander/vatefs/releases?per_page=100&page=1", "Wrong VatEFS catalog");
                return Task.FromResult(Response(catalog, request));
            });
            Assert(await new SoftwareReleaseSource(client).GetLatestFreshAsync(SoftwareApp.VatEfs, false) == actual, "Fresh/update catalogs differ");
        });
        await Run("VatEFS skips unpublished releases and rejects newer unsupported MSI versions", async () =>
        {
            var pending = Release(SoftwareApp.VatEfs, "0.0.16", prerelease: true); pending["published_at"] = null;
            var actual = await Latest(SoftwareApp.VatEfs, GitHub(pending, Release(SoftwareApp.VatEfs, "0.0.15")));
            Assert(actual is { Version: "0.0.15", IsPrerelease: false }, "An unpublished MSI version was used");
            foreach (var unsupported in new[] { "0.0.17-beta.1", "300.0.0", "0.0.15+build" })
                await Reject(GitHub(Release(SoftwareApp.VatEfs, unsupported, prerelease: true), Release(SoftwareApp.VatEfs, "0.0.14")), SoftwareApp.VatEfs);
            var invalid = Release(SoftwareApp.VatEfs, "0.0.15"); invalid["published_at"] = "invalid";
            await Reject(GitHub(invalid), SoftwareApp.VatEfs);
        });
        await Run("VatEFS rejects incomplete or ambiguous release assets", async () =>
        {
            var missingHash = Release(SoftwareApp.VatEfs, "0.0.15"); Asset(missingHash).Remove("digest");
            await Reject(GitHub(missingHash), SoftwareApp.VatEfs);
            var wrongAsset = Release(SoftwareApp.VatEfs, "0.0.15"); Asset(wrongAsset)["name"] = "VatEFS.dll";
            await Reject(GitHub(wrongAsset), SoftwareApp.VatEfs);
            var duplicateAsset = Release(SoftwareApp.VatEfs, "0.0.15"); duplicateAsset["assets"] = new[] { Asset(duplicateAsset), Asset(duplicateAsset) };
            await Reject(GitHub(duplicateAsset), SoftwareApp.VatEfs);
            await Reject(GitHub(Release(SoftwareApp.VatEfs, "0.0.15"), Release(SoftwareApp.VatEfs, "0.0.15")), SoftwareApp.VatEfs);
        });
        await Run("VatEFS download uses only the exact official versioned MSI and approved redirects", async () =>
        {
            var release = (await Latest(SoftwareApp.VatEfs, GitHub(Release(SoftwareApp.VatEfs, "0.0.15", prerelease: true))))!;
            Assert(SoftwareReleaseSource.IsAllowedDownloadUri(release, new("https://github.com/minsulander/vatefs/releases/download/v0.0.15/vatefs-0.0.15.msi")), "Official MSI rejected");
            foreach (var uri in new[] { "https://github.com/minsulander/vatefs/releases/download/v0.0.14/vatefs-0.0.15.msi", "https://github.com/other/vatefs/releases/download/v0.0.15/vatefs-0.0.15.msi", "https://example.invalid/vatefs-0.0.15.msi" })
                Assert(!SoftwareReleaseSource.IsAllowedDownloadUri(release, new(uri), true), "Unrelated asset was accepted");
            Assert(!SoftwareReleaseSource.IsAllowedDownloadUri(release, new("https://release-assets.githubusercontent.com/synthetic")) &&
                SoftwareReleaseSource.IsAllowedDownloadUri(release, new("https://release-assets.githubusercontent.com/synthetic"), true), "CDN allowed before official redirect");
        });
        await Run("latest incomplete GitHub asset is rejected without falling back", async () =>
        {
            var latest = Release(SoftwareApp.Vacs, "2.8.0");
            Asset(latest).Remove("digest");
            await Reject(GitHub(latest, Release(SoftwareApp.Vacs, "2.7.0")), SoftwareApp.Vacs);
        });
        await Run("missing or malformed publication status fails closed", async () =>
        {
            foreach (string field in new[] { "draft", "prerelease" })
            {
                var release = Release(SoftwareApp.Vacs, "2.8.0");
                release.Remove(field);
                await Reject(GitHub(release), SoftwareApp.Vacs);
                release[field] = "false";
                await Reject(GitHub(release), SoftwareApp.Vacs);
            }
        });
        await Run("missing, wrong-architecture, duplicate or unfinished setup is rejected", async () =>
        {
            foreach (string name in new[] { "vacs_2.8.0_arm64-setup.exe", "vacs_2.8.0_x64-setup.exe.sig", "vacs_2.7.0_x64-setup.exe" })
            {
                var release = Release(SoftwareApp.Vacs, "2.8.0");
                Asset(release)["name"] = name;
                await Reject(GitHub(release), SoftwareApp.Vacs);
            }
            var duplicate = Release(SoftwareApp.Vacs, "2.8.0");
            duplicate["assets"] = new[] { Asset(duplicate), Asset(duplicate) };
            await Reject(GitHub(duplicate), SoftwareApp.Vacs);
            var unfinished = Release(SoftwareApp.Vacs, "2.8.0");
            Asset(unfinished)["state"] = "new";
            await Reject(GitHub(unfinished), SoftwareApp.Vacs);
        });
        await Run("invalid SHA256 and package sizes are rejected", async () =>
        {
            foreach (object? value in new object?[] { null, "", "sha1:" + Hash, "sha256:" + new string('g', 64), "sha256:" + Hash[..63] })
            {
                var release = Release(SoftwareApp.Vacs, "2.8.0");
                Asset(release)["digest"] = value;
                await Reject(GitHub(release), SoftwareApp.Vacs);
            }
            foreach (object? value in new object?[] { null, 0, -1, "123", 1073741825L, 1.5 })
            {
                var release = Release(SoftwareApp.Vacs, "2.8.0");
                Asset(release)["size"] = value;
                await Reject(GitHub(release), SoftwareApp.Vacs);
            }
        });
        await Run("initial package URI must be the exact official release asset", async () =>
        {
            const string path = "/vacs-project/vacs/releases/download/vacs-client-v2.8.0/vacs_2.8.0_x64-setup.exe";
            string[] invalid = ["http://github.com" + path, "https://github.com.evil.test" + path,
                "https://user@github.com" + path, "https://github.com:444" + path, "https://evil.test" + path,
                "https://github.com" + path + "?download=1", "https://github.com" + path + "#asset",
                "https://github.com" + path.Replace("vacs-project", "another-owner"),
                "https://github.com" + path.Replace("vacs-client-v2.8.0", "vacs-client-v2.7.0"),
                "https://github.com" + path.Replace("/vacs/releases", "/vacs%2freleases"),
                "https://release-assets.githubusercontent.com/asset", "/relative/setup.exe"];
            foreach (string uri in invalid)
            {
                var release = Release(SoftwareApp.Vacs, "2.8.0");
                Asset(release)["browser_download_url"] = uri;
                await Reject(GitHub(release), SoftwareApp.Vacs);
            }
        });
        await Run("GitHub CDN destinations are permitted only after the official URL", async () =>
        {
            var release = (await Latest(SoftwareApp.Vacs, GitHub(Release(SoftwareApp.Vacs, "2.8.0"))))!;
            foreach (string host in new[] { "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com" })
            {
                var uri = new Uri($"https://{host}/asset?signature=value");
                Assert(!SoftwareReleaseSource.IsAllowedDownloadUri(release, uri), "CDN accepted as initial source");
                Assert(SoftwareReleaseSource.IsAllowedDownloadUri(release, uri, true), "Approved CDN rejected");
            }
            Assert(!SoftwareReleaseSource.IsAllowedDownloadUri(release, new Uri("https://githubusercontent.com/asset"), true), "Unexpected host accepted");
            Assert(!SoftwareReleaseSource.IsAllowedDownloadUri(release, new Uri("http://objects.githubusercontent.com/asset"), true), "HTTP accepted");
        });
        await Run("vATIS uses Windows full packages and numeric beta ordering", async () =>
        {
            var unrelated = VatisAsset("99.0.0"); unrelated["PackageId"] = "different.application";
            var delta = VatisAsset("4.1.0-beta.20"); delta["Type"] = "Delta";
            var release = await Latest(SoftwareApp.Vatis, Vatis(VatisAsset("4.1.0-beta.2"), delta, unrelated, VatisAsset("4.1.0-beta.19")), "4.1.0-beta.18");
            Assert(release is { Version: "4.1.0-beta.19", FileName: "org.vatsim.vatis-4.1.0-beta.19-full.nupkg" }, "Wrong vATIS package");
            Assert(release!.DownloadUri.AbsoluteUri == "https://vatis.app/updates/windows/org.vatsim.vatis-4.1.0-beta.19-full.nupkg", "Wrong vATIS origin");
            Assert(!SoftwareReleaseSource.IsAllowedDownloadUri(release, new Uri("https://objects.githubusercontent.com/asset"), true), "vATIS must stay on vendor host");
        });
        await Run("stable vATIS installations do not opt into beta", async () =>
        {
            Assert(await Latest(SoftwareApp.Vatis, Vatis(VatisAsset("4.1.0-beta.19")), "4.0.0") is null, "Stable opted into beta");
            var release = await Latest(SoftwareApp.Vatis, Vatis(VatisAsset("4.1.0-beta.19"), VatisAsset("4.0.1")), "4.0.0");
            Assert(release?.Version == "4.0.1", "Stable release ignored");
        });
        await Run("vATIS beta can graduate to stable without switching to alpha or rc", async () =>
        {
            var release = await Latest(SoftwareApp.Vatis, Vatis(VatisAsset("4.1.0-beta.19"), VatisAsset("4.1.0"), VatisAsset("5.0.0-alpha.1"), VatisAsset("5.0.0-rc.1")), "4.1.0-beta.18");
            Assert(release?.Version == "4.1.0", "Unexpected beta channel change");
            Assert(await Latest(SoftwareApp.Vatis, Vatis(VatisAsset("4.1.0-beta.19")), "4.1.0-alpha.1") is null, "Unknown channel opted into beta");
            await Throws<InvalidDataException>(() => Latest(SoftwareApp.Vatis, Vatis(VatisAsset("4.1.0")), "unknown"));
        });
        await Run("vATIS filename, hash, version and duplicate full package validation", async () =>
        {
            foreach (string name in new[] { "../escape.nupkg", "https://evil.test/package.nupkg", "org.vatsim.vatis-4.1.0-beta.19-delta.nupkg", "org.vatsim.vatis-4.0.0-full.nupkg" })
            {
                var asset = VatisAsset("4.1.0-beta.19"); asset["FileName"] = name;
                await Reject(Vatis(asset), SoftwareApp.Vatis);
            }
            var missingHash = VatisAsset("4.1.0-beta.19"); missingHash.Remove("SHA256");
            await Reject(Vatis(missingHash), SoftwareApp.Vatis);
            var invalidVersion = VatisAsset("4.1.0-beta.019");
            await Reject(Vatis(invalidVersion), SoftwareApp.Vatis);
            await Reject(Vatis(VatisAsset("4.1.0-beta.19"), VatisAsset("4.1.0-beta.19")), SoftwareApp.Vatis);
        });
        await Run("empty official feeds yield no supported release", async () =>
        {
            Assert(await Latest(SoftwareApp.Vacs, "[]") is null, "Empty GitHub result not null");
            Assert(await Latest(SoftwareApp.Vatis, "{\"Assets\":[]}", "4.1.0-beta.18") is null, "Empty vATIS result not null");
        });
        await Run("malformed and oversized catalogs are rejected", async () =>
        {
            await Reject("not json", SoftwareApp.Vacs);
            await Reject("{}", SoftwareApp.Vacs);
            await Reject("[]", SoftwareApp.Vatis);
            await Reject(new string(' ', 8 * 1024 * 1024 + 1), SoftwareApp.Vacs);
        });
        await Run("metadata redirects are rejected", async () =>
        {
            using var client = Client((request, _) => Task.FromResult(Response("[]", new HttpRequestMessage(HttpMethod.Get, "https://evil.test/releases"))));
            await Throws<InvalidDataException>(() => new SoftwareReleaseSource(client).GetLatestAsync(SoftwareApp.Vacs, "2.7.0", default));
        });
        await Run("metadata failures propagate and requests identify the application", async () =>
        {
            using var client = Client((request, _) =>
            {
                Assert(request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/vacs-project/vacs/releases?per_page=100&page=1", "Wrong API endpoint");
                Assert(request.Headers.UserAgent.ToString().Contains("SwedenFirLaunchpad"), "No User-Agent");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request });
            });
            await Throws<HttpRequestException>(() => new SoftwareReleaseSource(client).GetLatestAsync(SoftwareApp.Vacs, "2.7.0", default));
        });
        await Run("GitHub pagination finds client releases beyond unrelated first page", async () =>
        {
            int calls = 0;
            using var client = Client((request, _) =>
            {
                calls++;
                string json = calls == 1 ? GitHub(Enumerable.Range(0, 100).Select(_ => Release(SoftwareApp.Vacs, "99.0.0", tag: "vacs-server-v99.0.0")).ToArray()) : GitHub(Release(SoftwareApp.Vacs, "2.8.0"));
                return Task.FromResult(Response(json, request));
            });
            var release = await new SoftwareReleaseSource(client).GetLatestAsync(SoftwareApp.Vacs, "2.7.0", default);
            Assert(calls == 2 && release?.Version == "2.8.0", "Pagination failed");
        });
        await Run("metadata has an independent timeout and caller cancellation", async () =>
        {
            using var client = Client(async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new Exception("Unreachable"); });
            client.Timeout = TimeSpan.FromMinutes(20);
            var source = new SoftwareReleaseSource(client, TimeSpan.FromMilliseconds(30));
            await Throws<TimeoutException>(() => source.GetLatestAsync(SoftwareApp.Vacs, "2.7.0", default));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Throws<OperationCanceledException>(() => source.GetLatestAsync(SoftwareApp.Vacs, "2.7.0", cancellation.Token));
        });
    }

    private static Dictionary<string, object?> Release(SoftwareApp app, string version, bool draft = false, bool prerelease = false, string? tag = null)
    {
        string releaseTag = tag ?? (app == SoftwareApp.Vacs ? "vacs-client-v" : app == SoftwareApp.VatEfs ? "v" : "") + version;
        string name = app == SoftwareApp.Vacs ? $"vacs_{version}_x64-setup.exe" : app == SoftwareApp.VatEfs ? $"vatefs-{version}.msi" : $"trackaudio-{version}-x64-setup.exe";
        string repo = app == SoftwareApp.Vacs ? "vacs-project/vacs" : app == SoftwareApp.VatEfs ? "minsulander/vatefs" : "pierr3/TrackAudio";
        return new() { ["tag_name"] = releaseTag, ["draft"] = draft, ["prerelease"] = prerelease, ["published_at"] = "2026-10-07T20:54:00Z",
            ["assets"] = new[] { new Dictionary<string, object?> { ["name"] = name, ["state"] = "uploaded", ["size"] = 11164891,
                ["digest"] = "sha256:" + Hash, ["browser_download_url"] = $"https://github.com/{repo}/releases/download/{releaseTag}/{name}" } } };
    }
    private static Dictionary<string, object?> Asset(Dictionary<string, object?> release) => ((Dictionary<string, object?>[])release["assets"]!)[0];
    private static Dictionary<string, object?> VatisAsset(string version) => new()
    {
        ["PackageId"] = "org.vatsim.vatis", ["Version"] = version, ["Type"] = "Full", ["FileName"] = $"org.vatsim.vatis-{version}-full.nupkg",
        ["SHA256"] = "29903D495EA7CCD8C64DA60104EB84E2E23B547EC09E11A0037A2856E3BAD248", ["Size"] = 33176430
    };
    private static string GitHub(params Dictionary<string, object?>[] releases) => JsonSerializer.Serialize(releases);
    private static string Vatis(params Dictionary<string, object?>[] assets) => JsonSerializer.Serialize(new { Assets = assets });
    private static async Task<SoftwareRelease?> Latest(SoftwareApp app, string json, string installed = "4.1.0-beta.18")
    {
        using var client = Client((request, _) => Task.FromResult(Response(json, request)));
        return await new SoftwareReleaseSource(client).GetLatestAsync(app, installed, default);
    }
    private static Task Reject(string json, SoftwareApp app) => Throws<InvalidDataException>(() => Latest(app, json));
    private static HttpResponseMessage Response(string json, HttpRequestMessage request) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json"), RequestMessage = request };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) => new(new Handler(action));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static void Compare(string left, string right, int expected) =>
        Assert(SoftwareVersion.TryCompare(left, right, out int comparison) && Math.Sign(comparison) == expected, $"Wrong ordering: {left} vs {right}");
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Run(string name, Func<Task> test)
    {
        await test(); Passed++; Console.WriteLine("PASS source: " + name);
    }
}
