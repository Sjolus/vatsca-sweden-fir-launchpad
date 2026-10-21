using System.Text.Json;
using VatscaUpdateChecker.Models;

// In-memory only: no WPF, settings service, registry, credential or installer dependency.
var tests = new List<(string Name, Action Test)>
{
    ("new inline guide starts once without configuring or installing anything", () =>
    {
        var settings = new AppSettings();
        var before = settings.Copy();
        True(SetupGuide.Initialize(settings, firstLaunch: true));
        True(settings.SetupGuideActive && !settings.SetupWizardCompleted && !settings.SetupWizardDismissed);
        Equal("Applications", SetupGuide.Section(settings));
        True(!SetupGuide.Initialize(settings, firstLaunch: true));
        Equal(UnrelatedSettings(before), UnrelatedSettings(settings));
    }),
    ("saved inline guide resumes even when a settings file now exists", () =>
    {
        var settings = new AppSettings();
        SetupGuide.Initialize(settings, firstLaunch: true);
        SetupGuide.RememberSection(settings, "Gng");
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        True(!SetupGuide.Initialize(loaded, firstLaunch: false));
        True(loaded.SetupGuideActive);
        Equal("Gng", SetupGuide.Section(loaded));
        Equal("2 · Swedish GNG", SetupGuide.Present(loaded, "Gng")!.Heading);
    }),
    ("existing installations without the inline marker are never enrolled automatically", () =>
    {
        foreach (var settings in new[]
        {
            new AppSettings(), Existing(),
            new AppSettings { SetupWizardStep = 3 },
            new AppSettings { SetupWizardDismissed = true, SetupGuideSection = "Profile" }
        })
        {
            string before = JsonSerializer.Serialize(settings);
            True(!SetupGuide.Initialize(settings, firstLaunch: false));
            True(SetupGuide.Present(settings, "Applications") is null);
            Equal(before, JsonSerializer.Serialize(settings));
        }
    }),
    ("legacy completion and dismissal prevent first-launch re-enrollment", () =>
    {
        foreach (var settings in new[]
        {
            new AppSettings { SetupWizardCompleted = true },
            new AppSettings { SetupWizardDismissed = true }
        })
        {
            string before = JsonSerializer.Serialize(settings);
            True(!SetupGuide.Initialize(settings, firstLaunch: true));
            Equal(before, JsonSerializer.Serialize(settings));
        }
    }),
    ("Apps and optional-section navigation cannot finish or advance the guide", () =>
    {
        var settings = Existing();
        SetupGuide.Start(settings);
        True(SetupGuide.RememberSection(settings, "Gng"));
        foreach (string destination in new[] { "Apps", "Preferences", "Activity", "unknown" })
        {
            string before = JsonSerializer.Serialize(settings);
            True(!SetupGuide.RememberSection(settings, destination));
            Equal(before, JsonSerializer.Serialize(settings));
            var next = SetupGuide.Next(settings, destination);
            True(!next.Changed && settings.SetupGuideActive && !settings.SetupWizardCompleted);
            Equal("Gng", next.Destination);
            Equal(before, JsonSerializer.Serialize(settings));
        }
    }),
    ("guide steps end only on explicit Finish and do not imply setup readiness", () =>
    {
        var settings = new AppSettings();
        var before = settings.Copy();
        SetupGuide.Initialize(settings, firstLaunch: true);
        foreach (var (visible, destination) in new[] { ("Applications", "Gng"), ("Gng", "Profile") })
        {
            var next = SetupGuide.Next(settings, visible);
            True(next.Changed && settings.SetupGuideActive && !settings.SetupWizardCompleted);
            Equal(destination, next.Destination);
        }
        Equal("Finish setup", SetupGuide.Present(settings, "Profile")!.NextLabel);
        var finished = SetupGuide.Next(settings, "Profile");
        True(finished.Changed && !settings.SetupGuideActive && settings.SetupWizardCompleted && !settings.SetupWizardDismissed);
        Equal("Apps", finished.Destination);
        Equal(UnrelatedSettings(before), UnrelatedSettings(settings));
        True(SetupGuide.Present(settings, "Profile") is null);
        True(!SetupGuide.Next(settings, "Profile").Changed);
    }),
    ("Later retains choices and its checkpoint while Help resumes it", () =>
    {
        var settings = Existing();
        settings.PatchVatEfs = true; settings.PatchSaaman = true;
        ApplicationPreferences.SetNotUsed(settings, "vATIS", true);
        var before = settings.Copy();
        SetupGuide.Start(settings);
        SetupGuide.RememberSection(settings, "Profile");
        True(SetupGuide.Later(settings));
        True(!settings.SetupGuideActive && settings.SetupWizardDismissed && !settings.SetupWizardCompleted);
        True(!SetupGuide.Later(settings));
        True(!SetupGuide.Initialize(settings, firstLaunch: false));
        True(SetupGuide.Start(settings));
        Equal("Profile", SetupGuide.Section(settings));
        True(settings.SetupGuideActive && !settings.SetupWizardDismissed);
        Equal(UnrelatedSettings(before), UnrelatedSettings(settings));
    }),
    ("Help restarts a finished guide without altering installed apps or saved identity", () =>
    {
        var settings = Existing();
        settings.SetupGuideSection = "Profile";
        var before = settings.Copy();
        True(SetupGuide.Start(settings));
        Equal("Applications", SetupGuide.Section(settings));
        True(settings.SetupGuideActive && !settings.SetupWizardCompleted);
        Equal(UnrelatedSettings(before), UnrelatedSettings(settings));
        True(!SetupGuide.Start(settings));
    }),
    ("invalid active checkpoints normalize once and direct navigation saves only a real change", () =>
    {
        var settings = new AppSettings { SetupGuideActive = true, SetupGuideSection = "obsolete" };
        True(SetupGuide.Initialize(settings, firstLaunch: false));
        Equal("Applications", settings.SetupGuideSection);
        True(!SetupGuide.Initialize(settings, firstLaunch: false));
        True(!SetupGuide.RememberSection(settings, "Applications"));
        True(SetupGuide.RememberSection(settings, "Profile"));
        True(!SetupGuide.RememberSection(settings, "Profile"));
        Equal("Continue: Controller profile", SetupGuide.Present(settings, "Preferences")!.NextLabel);
        Equal("Profile", SetupGuide.Next(settings, "Activity").Destination);
    }),
    ("progress checkpoints resume without committing tentative appearance choices", () =>
    {
        var current = new AppSettings { IsDarkMode = true, CompactLayout = true };
        var state = new SetupWizardState(current) { Route = SetupWizardRoute.Fresh };
        state.Draft.IsDarkMode = false; state.Draft.CompactLayout = false;
        state.MoveTo(SetupWizardStep.Gng);
        var saved = state.Dismiss();
        Equal(2, saved.SetupWizardStep); Equal("Fresh", saved.SetupWizardRoute);
        True(saved.IsDarkMode && saved.CompactLayout && !saved.SetupWizardCompleted);
        var resumed = new SetupWizardState(saved);
        Equal(SetupWizardStep.Gng, resumed.Step); Equal(SetupWizardRoute.Fresh, resumed.Route);
        Equal(SetupWizardStep.Welcome, new SetupWizardState(new AppSettings { SetupWizardCompleted = true, SetupWizardStep = 4 }).Step);
    }),
    ("invalid persisted progress starts at welcome and unknown moves are rejected", () =>
    {
        var state = new SetupWizardState(new AppSettings { SetupWizardStep = 999, SetupWizardRoute = "unknown" });
        Equal(SetupWizardStep.Welcome, state.Step); Equal(SetupWizardRoute.Existing, state.Route);
        Throws<ArgumentOutOfRangeException>(() => state.MoveTo((SetupWizardStep)999));
    }),
    ("GNG completion checkpoints its folder before profile and preserves tentative preferences", () =>
    {
        var state = new SetupWizardState(new AppSettings { IsDarkMode = true });
        state.MoveTo(SetupWizardStep.Gng); state.Draft.IsDarkMode = false;
        state.AcceptGngFolder(@"C:\Synthetic\GNG", installed: true);
        state.MoveTo(SetupWizardStep.Profile);
        Equal(@"C:\Synthetic\GNG", state.Dismiss().EuroscopeDataPath);
        Equal(SetupWizardStep.Profile, new SetupWizardState(state.Dismiss()).Step);
        True(state.Dismiss().IsDarkMode && !state.Draft.IsDarkMode && state.HasSavedActions);
        Throws<ArgumentException>(() => state.AcceptGngFolder(" ", installed: false));
    }),
    ("direct adoption saves only the selected path and re-enables the chosen application", () =>
    {
        var state = new SetupWizardState(Existing());
        state.SetNotUsed("TrackAudio", true); state.Draft.IsDarkMode = false;
        state.AcceptLocation(SetupWizardApplication.TrackAudio, @"C:\Synthetic\adopted\trackaudio.exe");
        Equal(@"C:\Synthetic\adopted\trackaudio.exe", state.Dismiss().TrackAudioExePath);
        Equal(Existing().VatsimCid, state.Dismiss().VatsimCid);
        True(state.Dismiss().IsDarkMode && !state.IsNotUsed("TrackAudio"));
    }),
    ("not used keeps existing paths and profile choices and survives dismissal", () =>
    {
        var state = new SetupWizardState(Existing());
        state.SetNotUsed("vATIS", true); state.SetNotUsed("VACS", true);
        Equal(Existing().VatisExePath, state.Dismiss().VatisExePath);
        Equal(Existing().VatsimCid, state.Dismiss().VatsimCid);
        True(ApplicationPreferences.IsNotUsed(state.Dismiss(), "vATIS"));
        state.SetNotUsed("vATIS", false);
        True(!state.IsNotUsed("vATIS") && state.IsNotUsed("VACS"));
    }),
    ("opening wizard preserves settings and creates independent copies", () =>
    {
        var current = Existing(); var before = JsonSerializer.Serialize(current);
        var state = new SetupWizardState(current);
        state.Draft.VacsExePath = "pending"; state.Draft.IsDarkMode = false;
        Equal(before, JsonSerializer.Serialize(current));
        Equal(before, JsonSerializer.Serialize(state.Dismiss()));
        True(!state.HasSavedActions && state.Actions.Count == 0 && !state.RestartRequired);
    }),
    ("manual route makes no application or profile changes", () =>
    {
        var current = Existing(); var state = new SetupWizardState(current) { Route = SetupWizardRoute.Manual };
        Equal(JsonSerializer.Serialize(current), JsonSerializer.Serialize(state.Finish()));
        True(!state.HasSavedActions && state.Actions.Count == 0);
    }),
    ("fresh review reopens for a retained path whose executable is missing", () =>
    {
        var current = Existing(); var state = new SetupWizardState(current);
        True(!string.IsNullOrWhiteSpace(state.Draft.TrackAudioExePath));
        True(state.CanReviewFreshSetup(executableExists: false));
        Equal(current.TrackAudioExePath, state.Dismiss().TrackAudioExePath);
        True(!state.HasSavedActions && state.Actions.Count == 0);
    }),
    ("fresh review stays blocked for an existing executable", () =>
    {
        var state = new SetupWizardState(Existing());
        True(!state.CanReviewFreshSetup(executableExists: true));
    }),
    ("restart requirement blocks fresh review for missing and unconfigured executables", () =>
    {
        foreach (var current in new[] { Existing(), new AppSettings() })
        {
            var state = new SetupWizardState(current);
            True(state.CanReviewFreshSetup(executableExists: false));
            state.RequireRestart();
            True(!state.CanReviewFreshSetup(executableExists: false));
            True(!state.CanReviewFreshSetup(executableExists: true));
        }
    }),
    ("Finish saves pending preferences while dismissal discards them", () =>
    {
        var state = new SetupWizardState(Existing());
        state.Draft.IsDarkMode = false; state.Draft.CompactLayout = false; state.Draft.CheckOnStartup = false;
        var finish = state.Finish(); var dismiss = state.Dismiss();
        True(!finish.IsDarkMode && !finish.CompactLayout && !finish.CheckOnStartup);
        True(dismiss.IsDarkMode && dismiss.CompactLayout && dismiss.CheckOnStartup);
        True(finish.SetupWizardCompleted && !finish.SetupWizardDismissed);
    }),
    ("Settings save retains selected paths without saving staged theme or profile fields", () =>
    {
        var state = new SetupWizardState(Existing());
        state.Draft.IsDarkMode = false; state.Draft.CompactLayout = false;
        var edited = state.Draft.Copy();
        edited.VacsExePath = @"C:\Synthetic\chosen\vacs-client.exe";
        edited.EuroscopeDataPath = @"C:\Synthetic\chosen GNG";
        edited.CheckOnStartup = false;
        edited.VatsimName = "unrelated edited identity";
        edited.SetupWizardCompleted = false;
        state.AcceptSettings(edited);
        var saved = state.Dismiss();
        Equal(edited.VacsExePath, saved.VacsExePath); Equal(edited.EuroscopeDataPath, saved.EuroscopeDataPath);
        Equal("Synthetic identity", saved.VatsimName);
        True(saved.IsDarkMode && saved.CompactLayout && saved.SetupWizardCompleted && !saved.CheckOnStartup);
        True(!state.Draft.IsDarkMode && !state.Draft.CompactLayout && state.HasSavedActions);
        Equal(Existing().LastEuroscopeProfile, saved.LastEuroscopeProfile);
    }),
    ("Controller profile save retains identity without leaking unrelated path or preference edits", () =>
    {
        var state = new SetupWizardState(Existing());
        state.Draft.IsDarkMode = false; state.Draft.CheckOnStartup = false;
        var edited = state.Draft.Copy();
        edited.VatsimName = "Åsa Östergård (synthetic)"; edited.VatsimCid = "0000001";
        edited.VatsimRating = 3; edited.ObsCallsign = "ES"; edited.PatchVatEfs = true;
        edited.VatisExePath = "unrelated path";
        state.AcceptControllerProfile(edited);
        var saved = state.Dismiss();
        Equal(edited.VatsimName, saved.VatsimName); Equal(edited.VatsimCid, saved.VatsimCid);
        Equal(3, saved.VatsimRating); Equal("ES", saved.ObsCallsign); True(saved.PatchVatEfs);
        Equal(Existing().VatisExePath, saved.VatisExePath);
        True(saved.IsDarkMode && saved.CheckOnStartup && state.HasSavedActions);
        var summary = string.Join("\n", state.Summary());
        True(!summary.Contains(edited.VatsimName) && !summary.Contains(edited.VatsimCid));
    }),
    ("SAAMAN handoff saves only its reviewed choices and preserves unrelated drafts", () =>
    {
        var current = Existing();
        ApplicationPreferences.SetNotUsed(current, "SAAMAN", true);
        var state = new SetupWizardState(current);
        state.Draft.IsDarkMode = false;
        var edited = state.Draft.Copy();
        edited.SaamanDllPath = @"C:\Synthetic\SAAMAN\SAAMAN.dll";
        edited.SaamanProfilePath = @"C:\Synthetic\GNG\Reviewed APP.prf";
        edited.PatchSaaman = true; edited.SaamanProfileManaged = true;
        edited.VatsimName = "unrelated identity";
        edited.EuroscopeDataPath = @"C:\Unreviewed";
        edited.VatisExePath = @"C:\Unreviewed\vATIS.exe";
        state.AcceptSaaman(edited);
        var saved = state.Dismiss();
        Equal(edited.SaamanDllPath, saved.SaamanDllPath);
        Equal(edited.SaamanProfilePath, saved.SaamanProfilePath);
        True(saved.PatchSaaman && saved.SaamanProfileManaged);
        Equal(current.VatsimName, saved.VatsimName);
        Equal(current.EuroscopeDataPath, saved.EuroscopeDataPath);
        Equal(current.VatisExePath, saved.VatisExePath);
        True(saved.IsDarkMode && !state.Draft.IsDarkMode && state.HasSavedActions);
        True(!ApplicationPreferences.IsNotUsed(saved, "SAAMAN"));
        True(state.Actions.Any(a => a.Contains("SAAMAN")));
    }),
    ("Controller profile handoff retains an explicit SAAMAN disable across dismissal", () =>
    {
        var current = Existing();
        current.SaamanDllPath = @"C:\Synthetic\SAAMAN.dll";
        current.PatchSaaman = current.SaamanProfileManaged = true;
        var state = new SetupWizardState(current);
        var edited = state.Draft.Copy(); edited.PatchSaaman = false;
        state.AcceptControllerProfile(edited);
        var saved = state.Dismiss();
        True(!saved.PatchSaaman && saved.SaamanProfileManaged);
        Equal(current.SaamanDllPath, saved.SaamanDllPath);
    }),
    ("incomplete setup never adopts an unverified path", () =>
    {
        var current = Existing(); var state = new SetupWizardState(current);
        state.RecordIncompleteSetup("EuroScope"); state.RequireRestart();
        Equal(current.EuroscopeExePath, state.Dismiss().EuroscopeExePath);
        True(!state.HasSavedActions && state.RestartRequired);
        True(state.Summary().Any(s => s.Contains("without a confirmed installation")));
    }),
    ("restart is sticky and its notice is not duplicated", () =>
    {
        var state = new SetupWizardState(new AppSettings());
        state.RequireRestart(); state.RequireRestart();
        state.AcceptSettings(new AppSettings());
        True(state.RestartRequired);
        Equal(1, state.Actions.Count(s => s.StartsWith("Windows restart required")));
    }),
    ("dismissal keeps a verified installation even after later incomplete setup", () =>
    {
        var state = new SetupWizardState(new AppSettings());
        state.RecordInstallation(SetupWizardApplication.Vacs, @"C:\Synthetic\installed\vacs-client.exe");
        state.RecordIncompleteSetup("TrackAudio");
        True(state.HasSavedActions);
        Equal(@"C:\Synthetic\installed\vacs-client.exe", state.Dismiss().VacsExePath);
        Equal("", state.Dismiss().TrackAudioExePath);
    }),
    ("invalid installation result cannot alter saved choices", () =>
    {
        var state = new SetupWizardState(Existing());
        Throws<ArgumentException>(() => state.RecordInstallation(SetupWizardApplication.Vacs, " "));
        Throws<ArgumentOutOfRangeException>(() => state.RecordInstallation((SetupWizardApplication)999, "path"));
        Throws<ArgumentException>(() => state.RecordInstallation(SetupWizardApplication.VatEfs, "efs.exe"));
        Throws<ArgumentException>(() => state.RecordInstallation(SetupWizardApplication.VatEfs, @"C:\Synthetic\VatEFS\Wrong.exe"));
        Equal(JsonSerializer.Serialize(Existing()), JsonSerializer.Serialize(state.Dismiss()));
        True(!state.HasSavedActions && state.Actions.Count == 0);
    }),
    ("returned snapshots cannot mutate future saved results", () =>
    {
        var state = new SetupWizardState(Existing());
        state.Dismiss().VacsExePath = "changed result";
        state.Finish().VatsimName = "changed result";
        Equal(Existing().VacsExePath, state.Dismiss().VacsExePath);
        Equal(Existing().VatsimName, state.Finish().VatsimName);
    }),
    ("summary distinguishes unconfigured choices from installed software", () =>
    {
        var state = new SetupWizardState(new AppSettings { VacsExePath = @"C:\Synthetic\chosen\vacs-client.exe" });
        var summary = string.Join("\n", state.Summary());
        True(summary.Contains("Your chosen program files and data folders") && summary.Contains("not chosen — optional"));
        True(summary.Contains("VACS: chosen location — "));
        True(summary.Contains("Downloading and installing a package need separate actions") && summary.Contains("No installation"));
    }),
    ("no chosen applications produces no next-step guides", () =>
    {
        var state = new SetupWizardState(new AppSettings { EuroscopeExePath = " ", EuroscopeDataPath = "\t" });
        Equal(0, state.NextSteps().Count);
        True(!state.HasSavedActions && state.Actions.Count == 0);
    }),
    ("EuroScope offers the GNG guide before any data folder is chosen", () =>
    {
        var state = new SetupWizardState(new AppSettings { EuroscopeExePath = @"C:\Synthetic\EuroScope.exe" });
        True(state.NextSteps().SequenceEqual(new[] { SetupWizardGuide.EuroScopeGng }));
        Equal("", state.Draft.EuroscopeDataPath);
    }),
    ("GNG data selection offers one guide even with EuroScope also chosen", () =>
    {
        var state = new SetupWizardState(new AppSettings { EuroscopeDataPath = @"D:\Synthetic\GNG" });
        True(state.NextSteps().SequenceEqual(new[] { SetupWizardGuide.EuroScopeGng }));
        state.Draft.EuroscopeExePath = @"C:\Synthetic\EuroScope.exe";
        True(state.NextSteps().SequenceEqual(new[] { SetupWizardGuide.EuroScopeGng }));
    }),
    ("voice and ATIS guides follow their chosen paths without implying other applications", () =>
    {
        var state = new SetupWizardState(new AppSettings
        {
            TrackAudioExePath = @"C:\Synthetic\trackaudio.exe", VatisExePath = @"C:\Synthetic\vATIS.exe",
            VacsExePath = @"C:\Synthetic\vacs-client.exe", VatEfsPath = @"C:\Synthetic\VatEFS"
        });
        var before = JsonSerializer.Serialize(state.Draft);
        True(state.NextSteps().SequenceEqual(new[] { SetupWizardGuide.TrackAudio, SetupWizardGuide.Vatis }));
        Equal(before, JsonSerializer.Serialize(state.Draft));
        True(!state.HasSavedActions && state.Actions.Count == 0);
    }),
    ("guides refresh after reviewed path choices and verified installation", () =>
    {
        var state = new SetupWizardState(new AppSettings());
        state.RecordInstallation(SetupWizardApplication.TrackAudio, @"C:\Synthetic\trackaudio.exe");
        True(state.NextSteps().SequenceEqual(new[] { SetupWizardGuide.TrackAudio }));
        var selected = state.Draft.Copy(); selected.TrackAudioExePath = ""; selected.VatisExePath = @"C:\Synthetic\vATIS.exe";
        state.AcceptSettings(selected);
        True(state.NextSteps().SequenceEqual(new[] { SetupWizardGuide.Vatis }));
    }),
    ("guide destinations are fixed official HTTPS pages and unknown IDs are rejected", () =>
    {
        var expected = new Dictionary<SetupWizardGuide, string>
        {
            [SetupWizardGuide.EuroScopeGng] = "euroscope-and-gng-package-installation",
            [SetupWizardGuide.TrackAudio] = "observers-guide",
            [SetupWizardGuide.Vatis] = "vatis"
        };
        foreach (var (guide, page) in expected)
        {
            var uri = new Uri(SetupWizardState.GuideUrl(guide), UriKind.Absolute);
            Equal("https", uri.Scheme); Equal("wiki.vatsim-scandinavia.org", uri.Host);
            Equal("/books/general/page/" + page, uri.AbsolutePath);
            True(string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo));
        }
        Throws<ArgumentOutOfRangeException>(() => SetupWizardState.GuideUrl((SetupWizardGuide)999));
    }),
};

foreach (var app in Enum.GetValues<SetupWizardApplication>())
{
    tests.Add(($"verified {app} path survives dismissal without pending preferences", () =>
    {
        var current = Existing(); var state = new SetupWizardState(current);
        state.Draft.IsDarkMode = false; state.Draft.CheckOnStartup = false;
        string path = @"C:\Synthetic\verified\" + (app == SetupWizardApplication.VatEfs ? "efs" : app.ToString()) + ".exe";
        state.RecordInstallation(app, path, restartRequired: true, backupFolder: @"D:\Synthetic\recovery");
        var saved = state.Dismiss();
        Equal(app == SetupWizardApplication.VatEfs ? Path.GetDirectoryName(path)! : path, app switch
        {
            SetupWizardApplication.EuroScope => saved.EuroscopeExePath,
            SetupWizardApplication.Vacs => saved.VacsExePath,
            SetupWizardApplication.Vatis => saved.VatisExePath,
            SetupWizardApplication.VatEfs => saved.VatEfsPath,
            _ => saved.TrackAudioExePath
        });
        True(saved.IsDarkMode && saved.CheckOnStartup && state.RestartRequired && state.HasSavedActions);
        Equal(current.EuroscopeDataPath, saved.EuroscopeDataPath);
        True(state.Actions.Any(s => s.Contains(@"D:\Synthetic\recovery")));
    }));
}

int passed = 0;
foreach (var (name, test) in tests)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); Environment.ExitCode = 1; }
}
Console.WriteLine($"Setup wizard: {passed}/{tests.Count} passed (in-memory settings only).");

static AppSettings Existing() => new()
{
    IsDarkMode = true, CompactLayout = true, CheckOnStartup = true,
    SetupWizardCompleted = true, SetupWizardDismissed = false,
    EuroscopeExePath = @"C:\Synthetic\EuroScope\EuroScope.exe", EuroscopeDataPath = @"D:\Synthetic\GNG",
    VacsExePath = @"C:\Synthetic\VACS\vacs-client.exe", VatisExePath = @"C:\Synthetic\vATIS\vATIS.exe",
    TrackAudioExePath = @"C:\Synthetic\TrackAudio\trackaudio.exe", VatEfsPath = @"C:\Synthetic\VatEFS",
    VatsimName = "Synthetic identity", VatsimCid = "0000000", VatsimRating = 2, ObsCallsign = "TEST",
    LastEuroscopeProfile = @"D:\Synthetic\GNG\ESAA TEST.prf"
};
static string UnrelatedSettings(AppSettings settings)
{
    var copy = settings.Copy();
    copy.SetupGuideActive = false; copy.SetupGuideSection = "Applications";
    copy.SetupWizardCompleted = copy.SetupWizardDismissed = false;
    copy.SetupWizardStep = 0;
    return JsonSerializer.Serialize(copy);
}
static void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Values did not match."); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name + ".");
}
