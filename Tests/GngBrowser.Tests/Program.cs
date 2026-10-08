using VatscaUpdateChecker.Services;

const string full = "ESAA-Full-Package_20261001120000-261001-0003.zip";
const string update = "ESAA-Update-Only_20261001120000-261001-0003.zip";
int passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    passed++;
}
bool Download(string address, string name) => GngBrowserPolicy.TryGetDownloadFilename(address, name, out _);

Check(GngBrowserPolicy.IsAllowedNavigation(GngBrowserPolicy.AeroNavHome), "AeroNav HTTPS navigation");
Check(GngBrowserPolicy.IsAllowedNavigation("https://auth.example.test/authorize?code=synthetic"), "HTTPS sign-in providers");
Check(GngBrowserPolicy.IsAllowedNavigation("about:blank"), "Blank provider popup");
Check(!GngBrowserPolicy.IsAllowedNavigation("about:srcdoc"), "Inline frame allowance does not expand top-level or popup navigation");
foreach (var address in new[] { "about:blank", "about:srcdoc", "ABOUT:SRCDOC", "https://challenges.cloudflare.com/turnstile/synthetic" })
    Check(GngBrowserPolicy.IsAllowedFrameNavigation(address), "Verification frame can load: " + address);
foreach (var address in new[] { "http://files.aero-nav.com/ESAA", "file:///C:/test.txt", "javascript:alert(1)", "data:text/html,test", "vatsim:connect", "https://name:password@example.test", "not an address" })
{
    Check(!GngBrowserPolicy.IsAllowedNavigation(address), "Blocked navigation scheme or credentials: " + address);
    Check(!GngBrowserPolicy.IsAllowedFrameNavigation(address), "Inline frame support retains blocked schemes and credentials: " + address);
}
foreach (var address in new[] { "about:config", "about:srcdoc.evil.test", "about:srcdoc?url=file:///C:/test.txt", "about:srcdoc\n", "blob:https://auth.example.test/synthetic" })
    Check(!GngBrowserPolicy.IsAllowedFrameNavigation(address), "Unsupported internal frame address: " + address);

Check(GngBrowserPolicy.VisibleOrigin("https://auth.example.test/callback?code=synthetic#token=synthetic") == "https://auth.example.test", "Origin omits sign-in path, query and fragment");
Check(GngBrowserPolicy.VisibleOrigin("https://name:synthetic@auth.example.test:8443/callback") == "https://auth.example.test:8443", "Origin omits userinfo and shows nonstandard port");
Check(GngBrowserPolicy.VisibleOrigin("https://bücher.example.test/callback") == "https://xn--bcher-kva.example.test", "Origin displays IDN host consistently");
Check(GngBrowserPolicy.VisibleOrigin("about:blank") == "Sign-in window", "Blank window does not invent a trusted origin");

Check(GngBrowserPolicy.TryGetDownloadFilename("https://files.aero-nav.com/download?id=synthetic", @"C:\Downloads\" + full, out var filename) && filename == full,
    "Accepted download uses only the original basename");
Check(Download("https://aero-nav.com/download", update), "Root AeroNav host and Update Only package");
Check(Download("https://FILES.AERO-NAV.COM:443/download", full.ToUpperInvariant()), "Case-insensitive DNS and package name");
Check(Download("https://files.aero-nav.com/download", full.Replace(".zip", " (2).zip")), "Browser duplicate filename suffix");
foreach (var address in new[] { "http://files.aero-nav.com/download", "https://files.aero-nav.com:8443/download", "https://files.aero-nav.com.evil.test/download", "https://evil-aero-nav.com/download", "https://files.aero-nav.com@evil.test/download", "https://synthetic@files.aero-nav.com/download", "https://cdn.example.test/download", "blob:https://files.aero-nav.com/id", "file:///C:/package.zip" })
    Check(!Download(address, full), "Rejected download origin: " + address);
foreach (var name in new[] { "ESAA.zip", full + ".exe", full + "\n", full.Replace("20261001120000", "20261301120000"), full.Replace("20261001120000", "20260230120000"), full.Replace("Full-Package", "Other-Package"), full.Replace("261001", "٢٦١٠٠١"), "" })
    Check(!Download("https://files.aero-nav.com/download", name), "Rejected package filename");

Check(GngBrowserPolicy.IsCompletePackage(@"C:\cache\" + full), "Full package classification");
Check(!GngBrowserPolicy.IsCompletePackage(update), "Update Only does not supply complete-package provenance");
Check(!GngBrowserPolicy.IsCompletePackage(full.Replace("20261001120000", "20269999120000")), "Malformed complete-package timestamp");
Check(!GngBrowserPolicy.IsCompletePackage(null), "Missing cached package");
Check(GngBrowserPolicy.MaximumDownloadBytes == 2L * 1024 * 1024 * 1024, "Bounded browser downloads");
passed += PackageSelectionTests.Run();
passed += DownloadRequestTests.Run();
passed += NavigationStateTests.Run();
Console.WriteLine($"PASS: {passed} GNG browser-policy checks; no browser, network or downloads used.");
