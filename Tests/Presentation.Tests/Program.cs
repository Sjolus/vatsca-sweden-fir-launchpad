using System.Text.Json;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

// Pure settings, bounds and identity checks. Never enumerate or act on real processes/windows.
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
var settings = JsonSerializer.Deserialize<AppSettings>("{\"EuroscopeExePath\":\"C:\\\\Synthetic\\\\EuroScope.exe\"}")!;
Check(settings.TextScale == 1 && !settings.AlwaysOnTop && settings.WindowWidth == null, "old settings retain standard presentation defaults");
string originalPath = settings.EuroscopeExePath;
ApplicationPreferences.SetNotUsed(settings, "EuroScope", true);
ApplicationPreferences.SetNotUsed(settings, "vATIS", true);
ApplicationPreferences.SetNotUsed(settings, "EuroScope", true);
Check(settings.NotUsedApplications == "EuroScope|vATIS", "optional choices are distinct and stable");
Check(settings.EuroscopeExePath == originalPath, "not-used choice preserves saved application paths");
ApplicationPreferences.SetNotUsed(settings, "Sweden FIR Launchpad", true);
Check(!ApplicationPreferences.IsNotUsed(settings, "Sweden FIR Launchpad"), "Launchpad cannot be excluded from updates");
ApplicationPreferences.SetNotUsed(settings, "EuroScope", false);
Check(settings.NotUsedApplications == "vATIS" && settings.EuroscopeExePath == originalPath, "re-enabling application restores inclusion without losing its path");
settings.NotUsedApplications += "|future-tool|";
ApplicationPreferences.SetNotUsed(settings, "TrackAudio", true);
Check(settings.NotUsedApplications == "TrackAudio|vATIS", "unknown choices cannot mark unrelated applications unused");
settings.TextScale = 1.25; settings.AlwaysOnTop = true; settings.DetailPanelHeight = 220;
settings.WindowLeft = -1800; settings.WindowTop = 100; settings.WindowWidth = 880; settings.WindowHeight = 550;
settings.WindowMonitor = "Synthetic display"; settings.SetupWizardStep = 2;
var copy = settings.Copy(); copy.NotUsedApplications = "";
Check(settings.NotUsedApplications != copy.NotUsedApplications && copy.TextScale == 1.25 && copy.WindowLeft == -1800 && copy.SetupWizardStep == 2, "settings copy preserves independent presentation and resume choices");
var roundtrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
Check(roundtrip.WindowMonitor == settings.WindowMonitor && roundtrip.DetailPanelHeight == 220 && roundtrip.TextScale == 1.25, "presentation survives settings serialization");
Check(ApplicationPreferences.EffectiveTextScale(double.NaN) == 1 && ApplicationPreferences.EffectiveTextScale(99) == 1.5 && ApplicationPreferences.EffectiveTextScale(.2) == 1, "invalid text sizes are bounded");

var primary = new WindowBounds(0, 0, 1920, 1040);
Check(WindowBounds.Fit(new(-1900, 80, 880, 550), primary, 760, 480) == new WindowBounds(0, 80, 880, 550), "removed monitor cannot leave window off screen");
Check(WindowBounds.Fit(new(-1800, 80, 880, 550), new(-1920, 0, 1920, 1040), 760, 480).Left == -1800, "negative-coordinate monitor is retained");
Check(WindowBounds.Fit(new(1800, 1000, 99999, 99999), primary, 760, 480) == primary, "oversized saved window fits current work area");
Check(WindowBounds.Fit(new(0, 0, 880, 550), new(0, 0, 640, 360), 760, 480) == new WindowBounds(0, 0, 640, 360), "small work area wins over normal minimum size");
Check(WindowBounds.Fit(new(double.NaN, 0, 880, 550), primary, 760, 480) == new WindowBounds(0, 0, 760, 480), "invalid persisted bounds use a visible default");

var process = new ApplicationProcessIdentity(123, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), @"C:\Synthetic\EuroScope.exe");
Check(ApplicationProcessControl.SameIdentity(process, process), "identical process lifetime is accepted");
Check(!ApplicationProcessControl.SameIdentity(process, process with { Id = 124 }), "another PID is rejected");
Check(!ApplicationProcessControl.SameIdentity(process, process with { StartedUtc = process.StartedUtc.AddSeconds(1) }), "PID reuse is rejected");
Check(!ApplicationProcessControl.SameIdentity(process, process with { ExecutablePath = @"C:\Other\EuroScope.exe" }), "another installation is rejected even when the executable name matches");
Check(!ApplicationProcessControl.SameIdentity(process with { ExecutablePath = "" }, process with { ExecutablePath = "" }), "unknown executable identity is rejected");
Check(ApplicationProcessControl.SameIdentity(process, process with { ExecutablePath = process.ExecutablePath.ToUpperInvariant() }), "Windows path case does not invent a different installation");
Console.WriteLine($"{checks} presentation and process identity checks passed; no user settings, windows or processes accessed.");
