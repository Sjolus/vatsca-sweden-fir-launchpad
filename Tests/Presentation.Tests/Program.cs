using System.Text.Json;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

// Pure settings, bounds, browser-placement and identity checks. Never enumerate or act on real processes/windows.
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

DisplayArea[] displays = [new(@"\\.\DISPLAY1", primary, true), new(@"\\.\DISPLAY2", new(1920, 0, 2560, 1400), false)];
var right = new ExternalWindowPlacement(new(2000, 50, 1200, 900), false, @"\\.\DISPLAY2");
bool Opens(ExternalWindowTarget? target, WindowBounds bounds, bool maximize) => target is not null && target.Bounds == bounds && target.Maximize == maximize;
Check(Opens(ExternalWindowLayout.Resolve(right, displays), new(2000, 50, 1200, 900), false), "browser window reopens at its saved position on a secondary display");
Check(ExternalWindowLayout.Resolve(right, [displays[0]]) is null, "browser window from a disconnected display gets no launch placement");
Check(ExternalWindowLayout.Resolve(new(null, true, @"\\.\DISPLAY2"), [displays[0]]) is null, "maximized window on a disconnected display gets no launch placement");
Check(Opens(ExternalWindowLayout.Resolve(right with { Maximized = true, Monitor = @"\\.\DISPLAY1" }, displays), new(0, 0, 1920, 1040), true),
    "maximized window ignores normal bounds remembered on another display");
var renamed = ExternalWindowLayout.Resolve(right with { Monitor = "renamed" }, displays);
Check(Opens(renamed, new(2000, 50, 1200, 900), false) && renamed!.Display.Name == @"\\.\DISPLAY2", "a renamed display still keeps a window it contains");
var overflowing = ExternalWindowLayout.Resolve(new(new(3800, 900, 1200, 900), false, @"\\.\DISPLAY2"), displays);
Check(Opens(overflowing, new(3280, 500, 1200, 900), false) && overflowing!.Display.Name == @"\\.\DISPLAY2" && overflowing.EdgeBounds == new WindowBounds(3280, 500, 1200, 900),
    "a window overflowing its display's work area is moved fully onto it without resizing");
var maximizedInPlace = ExternalWindowLayout.Resolve(right with { Maximized = true }, displays);
Check(Opens(maximizedInPlace, new(2000, 50, 1200, 900), true) && maximizedInPlace!.Display.Name == @"\\.\DISPLAY2" && maximizedInPlace.EdgeBounds == new WindowBounds(2000, 50, 1200, 900),
    "maximized window keeps normal bounds remembered on the same display so Edge maximizes there");
var maximized = ExternalWindowLayout.Resolve(new(null, true, @"\\.\DISPLAY2"), displays);
Check(Opens(maximized, new(1920, 0, 2560, 1400), true) && maximized!.Display.Name == @"\\.\DISPLAY2", "maximized browser window returns to its display");
Check(ExternalWindowLayout.Resolve(right, displays)!.EdgeBounds == new WindowBounds(2000, 50, 1200, 900), "Edge launch bounds equal physical pixels at 100% scaling");
DisplayArea[] scaled = [new("A", new(0, 0, 1920, 1040), true, new(0, 0, 1920, 1080), 1.25), new("B", new(1920, 0, 1920, 1040), false, new(1920, 0, 1920, 1080), 1.25)];
Check(ExternalWindowLayout.ToChromiumDip(new(2000, 50, 1250, 900), scaled[1], scaled) == new WindowBounds(1600, 40, 1000, 720), "uniform scaling divides launch bounds exactly");
DisplayArea[] mixed = [new("A", new(0, 0, 1920, 1040), true, new(0, 0, 1920, 1080), 1), new("B", new(1920, 0, 2560, 1400), false, new(1920, 0, 2560, 1440), 1.5),
    new("C", new(-2560, 0, 2560, 1400), false, new(-2560, 0, 2560, 1440), 2)];
Check(ExternalWindowLayout.ToChromiumDip(new(2070, 150, 1500, 900), mixed[1], mixed) == new WindowBounds(2020, 100, 1000, 600), "higher-scaled display right of primary keeps its DIP origin");
Check(ExternalWindowLayout.ToChromiumDip(new(-2360, 100, 1000, 800), mixed[2], mixed) == new WindowBounds(-1180, 50, 500, 400), "higher-scaled display left of primary is laid out against the primary edge");
Check(ExternalWindowLayout.Resolve(new(new(double.NaN, 0, 1, 1), false, ""), displays) is null, "invalid saved bounds leave the browser default");
Check(ExternalWindowLayout.Resolve(right, []) is null, "no connected displays leave the browser default");
Check(ExternalWindowLayout.EdgeArguments(maximizedInPlace).SequenceEqual(["--window-position=2000,50", "--window-size=1200,900"]),
    "a maximized window launches at its normal bounds on its display without --start-maximized, which placement switches override");
Check(ExternalWindowLayout.EdgeArguments(maximized).SequenceEqual(["--window-position=1920,0", "--window-size=2560,1400"]),
    "a maximized window without normal bounds launches across its saved display's work area");
Check(ExternalWindowLayout.EdgeArguments(ExternalWindowLayout.Resolve(right, displays)).SequenceEqual(["--window-position=2000,50", "--window-size=1200,900"]),
    "a normal window launches with its position and size");
Check(ExternalWindowLayout.EdgeArguments(null).Count == 0, "no target passes no placement arguments");
var culture = System.Globalization.CultureInfo.CurrentCulture;
try
{
    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
    var negative = new ExternalWindowTarget(new(-1913, 0, 974, 1047), false, displays[0], new(-1913, -7, 974, 1047));
    Check(ExternalWindowLayout.EdgeArguments(negative).SequenceEqual(["--window-position=-1913,-7", "--window-size=974,1047"]),
        "negative launch positions use an ASCII minus under Swedish culture");
}
finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
var leftSnap = new ExternalWindowPlacement(new(0, 0, 960, 1040), false, @"\\.\DISPLAY1");
Check(Opens(ExternalWindowLayout.Resolve(leftSnap, displays), new(0, 0, 960, 1040), false), "a snapped visible frame fills its half of the work area without clipping");
Check(WindowInsets.Between(new(100, 100, 800, 600), new(107, 100, 786, 593)) == new WindowInsets(7, 0, 7, 7), "invisible resize borders are measured from the outer and visible frames");
Check(WindowInsets.Between(new(10, 20, 800, 600), new(10, 20, 800, 600)).Expand(new(10, 20, 800, 600)) == new WindowBounds(10, 20, 800, 600), "borderless frames launch unchanged");
var bordered = leftSnap with { Frame = new(7, 0, 7, 7) };
Check(ExternalWindowLayout.Resolve(bordered, displays)!.EdgeBounds == new WindowBounds(-7, 0, 974, 1047) &&
    ExternalWindowLayout.Resolve(bordered, displays)!.Bounds == new WindowBounds(0, 0, 960, 1040), "Edge launch size includes saved invisible borders while verification uses the visible frame");
Check(ExternalWindowLayout.Resolve(leftSnap with { Frame = new(double.NaN, -1, 900, 0) }, displays)!.EdgeBounds == new WindowBounds(0, 0, 960, 1040), "implausible saved borders are ignored");
var framed = new AppSettings();
Check(BrowserWindowSettings.Set(framed, "VATIRIS", bordered) && BrowserWindowSettings.Get(framed, "VATIRIS") == bordered, "saved borders round-trip through settings");
BrowserWindowSettings.Set(framed, "VATIRIS", new(null, true, @"\\.\DISPLAY1"));
Check(BrowserWindowSettings.Get(framed, "VATIRIS") == bordered with { Maximized = true }, "maximizing keeps the normal bounds and borders observed with them");
var browser = new AppSettings();
Check(BrowserWindowSettings.Get(browser, "VATIRIS") is null && !BrowserWindowSettings.Set(browser, "VatEFS", right), "only VATIRIS remembers a browser placement");
Check(BrowserWindowSettings.Set(browser, "VATIRIS", right) && !BrowserWindowSettings.Set(browser, "VATIRIS", right), "unchanged browser placement is not rewritten");
Check(BrowserWindowSettings.Set(browser, "VATIRIS", new(null, true, @"\\.\DISPLAY2")) && BrowserWindowSettings.Get(browser, "VATIRIS") == right with { Maximized = true },
    "maximizing keeps the earlier normal bounds");
var browserRoundtrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(browser))!;
Check(BrowserWindowSettings.Get(browserRoundtrip, "VATIRIS") == right with { Maximized = true } &&
    BrowserWindowSettings.Get(browser.Copy(), "VATIRIS") == right with { Maximized = true }, "browser placement survives serialization and copies");
Check(browser.WindowLeft is null && browser.WindowMonitor == "", "browser placement is independent of Launchpad's own window");
var restored = new ExternalWindowPlacement(new(2100, 60, 1000, 800), false, @"\\.\DISPLAY2");
Check(BrowserWindowSettings.Set(browser, "VATIRIS", restored) && BrowserWindowSettings.Get(browser, "VATIRIS") == restored &&
    browser.VatirisWindowLeft == 2100 && browser.VatirisWindowWidth == 1000 && !browser.VatirisWindowMaximized, "un-maximizing to new normal bounds replaces the remembered bounds");
Check(BrowserWindowSettings.Set(browser, "VATIRIS", new(new(double.NaN, 0, 1, 1), false, @"\\.\DISPLAY1")) &&
    BrowserWindowSettings.Get(browser, "VATIRIS") == restored with { Monitor = @"\\.\DISPLAY1" }, "an observation without valid bounds keeps the remembered bounds");
var implausible = new AppSettings { VatirisWindowLeft = 0, VatirisWindowTop = 0, VatirisWindowWidth = 960, VatirisWindowHeight = 1040,
    VatirisWindowMonitor = @"\\.\DISPLAY1", VatirisFrameLeft = 900, VatirisFrameTop = 0, VatirisFrameRight = 7, VatirisFrameBottom = 7 };
Check(BrowserWindowSettings.Get(implausible, "VATIRIS") == leftSnap, "implausible saved borders are dropped when settings are read");

var tracker = new BrowserPlacementTracker();
var tracked = new AppSettings();
Check(!tracker.Observe(tracked, "VATIRIS", windowOpen: true, right, canSave: true) && BrowserWindowSettings.Get(tracked, "VATIRIS") == right,
    "an open window's placement is recorded in settings without saving");
Check(!tracker.Observe(tracked, "VATIRIS", windowOpen: true, null, canSave: true) && BrowserWindowSettings.Get(tracked, "VATIRIS") == right,
    "a hidden or minimized observation keeps the recorded placement");
Check(!tracker.Observe(tracked, "VATIRIS", windowOpen: false, null, canSave: false), "a closed window's placement waits while saving is not allowed");
Check(tracker.Observe(tracked, "VATIRIS", windowOpen: false, null, canSave: true), "a changed placement is saved once the window has closed and saving is allowed");
Check(!tracker.Observe(tracked, "VATIRIS", windowOpen: false, null, canSave: true), "a saved placement is not saved again on later ticks");
Check(!tracker.Observe(tracked, "VATIRIS", windowOpen: true, right, canSave: true) && !tracker.Observe(tracked, "VATIRIS", windowOpen: false, null, canSave: true),
    "reopening at an unchanged placement does not save settings");
var otherApp = new AppSettings();
Check(!tracker.Observe(otherApp, "VatEFS", windowOpen: true, right, canSave: true) && !tracker.Observe(otherApp, "VatEFS", windowOpen: false, null, canSave: true) &&
    otherApp.VatirisWindowLeft is null, "VatEFS observations are neither recorded nor saved");

var process = new ApplicationProcessIdentity(123, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), @"C:\Synthetic\EuroScope.exe");
Check(ApplicationProcessControl.SameIdentity(process, process), "identical process lifetime is accepted");
Check(!ApplicationProcessControl.SameIdentity(process, process with { Id = 124 }), "another PID is rejected");
Check(!ApplicationProcessControl.SameIdentity(process, process with { StartedUtc = process.StartedUtc.AddSeconds(1) }), "PID reuse is rejected");
Check(!ApplicationProcessControl.SameIdentity(process, process with { ExecutablePath = @"C:\Other\EuroScope.exe" }), "another installation is rejected even when the executable name matches");
Check(!ApplicationProcessControl.SameIdentity(process with { ExecutablePath = "" }, process with { ExecutablePath = "" }), "unknown executable identity is rejected");
Check(ApplicationProcessControl.SameIdentity(process, process with { ExecutablePath = process.ExecutablePath.ToUpperInvariant() }), "Windows path case does not invent a different installation");
Console.WriteLine($"{checks} presentation and process identity checks passed; no user settings, windows or processes accessed.");
