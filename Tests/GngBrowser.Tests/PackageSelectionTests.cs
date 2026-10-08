using System.Diagnostics;
using System.Text.Json;
using VatscaUpdateChecker.Services;

internal static class PackageSelectionTests
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); count++; }
        foreach (var address in new[] { "https://files.aero-nav.com/ESAA", "https://FILES.AERO-NAV.COM:443/ESAA/", "https://files.aero-nav.com/ESAA?synthetic=1" })
            Check(GngPackageSelection.IsPackagePage(new Uri(address)), "Known ESAA package page rejected");
        foreach (var address in new[] { "http://files.aero-nav.com/ESAA", "https://files.aero-nav.com:8443/ESAA", "https://files.aero-nav.com/ESAA/more", "https://files.aero-nav.com/esaa", "https://files.aero-nav.com/ESGG", "https://files.aero-nav.com.evil.test/ESAA", "https://auth.example.test/ESAA", "https://synthetic@files.aero-nav.com/ESAA", "about:blank" })
            Check(!GngPackageSelection.IsPackagePage(new Uri(address)), "Unsupported page accepted for script execution");
        Check(!GngPackageSelection.IsPackagePage(null), "Missing page accepted");
        Check(!GngPackageSelection.IsPackagePage(new Uri("ESAA", UriKind.Relative)), "Relative page accepted");
        foreach (var status in new[] { "ready", "clicked", "waiting-for-login", "unsupported-link" })
        {
            var result = GngPackageSelection.ParseResult(JsonSerializer.Serialize(new { status, version = "2610/01 rev.3", identity = "20261001222500-261001-0003" }));
            Check(result.Status == status && result.Version == "2610/01 rev.3" && result.Identity == "20261001222500-261001-0003", "Public package result could not be parsed");
        }
        foreach (var json in new[] { "null", "[]", "{", "{\"status\":\"unknown\"}", "{\"status\":\"clicked\"}", "{\"status\":true}",
            "{\"status\":\"clicked\",\"version\":\"2610/01 rev.3\",\"identity\":\"20260230120000-261001-0003\"}",
            "{\"status\":\"clicked\",\"version\":\"2611/01 rev.3\",\"identity\":\"20261001222500-261001-0003\"}",
            "{\"status\":\"clicked\",\"version\":\"2615/01 rev.3\",\"identity\":\"20261001222500-261501-0003\"}",
            "{\"status\":\"clicked\",\"version\":\"2610/00 rev.3\",\"identity\":\"20261001222500-261000-0003\"}",
            "{\"status\":\"clicked\",\"href\":\"https://example.test/private\"}", new string('x', 513) })
            Check(GngPackageSelection.ParseResult(json) == new GngPackageSelectionResult("ambiguous"), "Invalid or excessive script output accepted");
        foreach (var status in new[] { "wrong-page", "already-attempted", "no-package", "ambiguous", "selection-changed" })
            Check(GngPackageSelection.ParseResult("{\"status\":\"" + status + "\"}").Status == status, "Non-download result lost");
        try { GngPackageSelection.CreateInspectionScript((GngPackageKind)99); throw new InvalidOperationException("Invalid package enum accepted"); }
        catch (ArgumentOutOfRangeException) { count++; }
        try { GngPackageSelection.CreateDownloadScript((GngPackageKind)99, "20261001222500-261001-0003"); throw new InvalidOperationException("Invalid download package enum accepted"); }
        catch (ArgumentOutOfRangeException) { count++; }
        foreach (var version in new[] { "", "2610/01 rev.03", "2615/01 rev.3", "2610/00 rev.3", "2610/01 rev.3'", "2610/01 rev.3\n", "2610/01 rev.10000" })
        {
            try { GngPackageSelection.CreateInspectionScript(GngPackageKind.Full, version); throw new InvalidOperationException("Unvalidated reference version accepted"); }
            catch (ArgumentException) { count++; }
        }
        foreach (var identity in new[] { "", "20261001222500-261001-0003'", "20261001222500-261001-0003\n", "20260230122500-261001-0003", "20261001222500-261501-0003", "20261001222500-261000-0003", "09991001222500-261001-0003", new string('x', 1000) })
        {
            try { GngPackageSelection.CreateDownloadScript(GngPackageKind.Full, identity); throw new InvalidOperationException("Unvalidated click identity accepted"); }
            catch (ArgumentException) { count++; }
        }
        try { GngPackageSelection.CreateDownloadScript(GngPackageKind.Full, "20261001222500-261001-0003", "2610/01 rev.4"); throw new InvalidOperationException("Mismatched reference identity accepted"); }
        catch (ArgumentException) { count++; }

        var identities = new[] { "20261001222500-261001-0003", "20261001222554-261001-0003", "20261001222501-261001-0003", "20261001222500-261002-0001" };

        // Run the exact production scripts against small DOM objects, without an installed
        // browser engine, network, cookies, account state or any third-party npm packages.
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SelectionDomTests.js"));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Node.js is required for the synthetic DOM checks.");
        process.StandardInput.Write(JsonSerializer.Serialize(new
        {
            inspection = new
            {
                full = GngPackageSelection.CreateInspectionScript(GngPackageKind.Full),
                update = GngPackageSelection.CreateInspectionScript(GngPackageKind.UpdateOnly),
                fullReference = GngPackageSelection.CreateInspectionScript(GngPackageKind.Full, "2610/01 rev.3")
            },
            download = new
            {
                full = identities.ToDictionary(identity => identity, identity => GngPackageSelection.CreateDownloadScript(GngPackageKind.Full, identity)),
                update = identities.ToDictionary(identity => identity, identity => GngPackageSelection.CreateDownloadScript(GngPackageKind.UpdateOnly, identity)),
                fullReference = identities.Where(identity => identity.EndsWith("-261001-0003", StringComparison.Ordinal))
                    .ToDictionary(identity => identity, identity => GngPackageSelection.CreateDownloadScript(GngPackageKind.Full, identity, "2610/01 rev.3"))
            }
        }));
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); throw new InvalidOperationException("Synthetic DOM checks timed out."); }
        var output = stdout.GetAwaiter().GetResult();
        var errors = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException("Synthetic package selection failed: " + output + errors);
        using var results = JsonDocument.Parse(output);
        count += results.RootElement.GetProperty("passed").GetInt32();
        Console.WriteLine("PASS: exact package-selection scripts exercised with synthetic DOM objects in Node.js.");
        return count;
    }
}
