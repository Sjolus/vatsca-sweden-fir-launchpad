using SaamanSetupFlow.Tests;
using VatscaUpdateChecker.Models;

int passed = 0, failed = 0;
await Check("Turning on a missing plugin installs before activation", async () =>
{
    using var f = new Fixture();
    ApplicationPreferences.SetNotUsed(f.Window.Settings, "SAAMAN", true);
    await f.Window.Enable(true);
    Equal(1, f.Count("install")); Equal(1, f.Count("apply-profile"));
    True(f.Calls.IndexOf("installed") < f.Calls.IndexOf("apply-profile"));
    True(File.Exists(f.Dll)); True(f.Window.Settings.PatchSaaman); True(f.Window.Settings.SaamanProfileManaged);
    Equal(f.Dll, f.Window.Settings.SaamanDllPath);
    False(ApplicationPreferences.IsNotUsed(f.Window.Settings, "SAAMAN"));
    Equal("SAAMAN:" + f.Dll, File.ReadAllText(f.Profile));
    Equal(ReportingOutcome.Completed, f.End(ReportingOperation.Install).Outcome);
    Equal(ReportingOutcome.Completed, f.End(ReportingOperation.ConfigurePluginProfile).Outcome);
    True(f.Activities.Any(a => a.Operation == ActivityOperation.Install));
    True(f.Activities.Any(a => a.Operation == ActivityOperation.Configure));
    Balanced(f);
});
await Check("Turning on an existing verified DLL makes no release request", async () =>
{
    using var f = new Fixture(installed: true) { ForbidNetwork = true };
    await f.Window.Enable(true);
    Equal(0, f.Count("release")); Equal(0, f.Count("install")); Equal(2, f.AdoptCount);
    True(f.Window.Settings.PatchSaaman); Equal(1, f.Applied.Count); Balanced(f);
});
await Check("Activation failure after installation retains the DLL and reports partial", async () =>
{
    using var f = new Fixture { FailProfile = true };
    await f.Window.Enable(true);
    True(File.Exists(f.Dll)); Equal(f.Dll, f.Window.Settings.SaamanDllPath);
    False(f.Window.Settings.PatchSaaman); False(f.Window.Settings.SaamanProfileManaged);
    Equal("original profile", File.ReadAllText(f.Profile));
    Equal(ReportingOutcome.Completed, f.End(ReportingOperation.Install).Outcome);
    Equal(ReportingOutcome.Partial, f.End(ReportingOperation.ConfigurePluginProfile).Outcome);
    True(f.End(ReportingOperation.ConfigurePluginProfile).Details?.RecoveryAvailable == true);
    Equal(1, f.FailureCount); Equal(1, f.Saved.Count); Balanced(f);
});
await Check("Failed settings persistence keeps applied choices; retry only saves", async () =>
{
    using var f = new Fixture(installed: true);
    f.SaveResults.Enqueue(false);
    await f.Window.Enable(true);
    True(f.Window.Settings.PatchSaaman); True(f.Window.NeedsSave);
    Equal(ReportingOutcome.Partial, f.End(ReportingOperation.ConfigurePluginProfile).Outcome);
    Equal(ReportingReason.SaveFailed, f.End(ReportingOperation.ConfigurePluginProfile).Reason);
    True(f.Statuses.Last().Message.Contains("Retry save", StringComparison.Ordinal));
    int install = f.Count("install"), profile = f.Count("apply-profile"), adopt = f.AdoptCount;
    int runtime = f.Count("runtime-check"), releases = f.Count("release");
    await f.Window.RetrySave();
    False(f.Window.NeedsSave); True(f.Window.Settings.PatchSaaman);
    Equal(install, f.Count("install")); Equal(profile, f.Count("apply-profile")); Equal(adopt, f.AdoptCount);
    Equal(runtime, f.Count("runtime-check")); Equal(releases, f.Count("release"));
    Equal(ReportingOutcome.Completed, f.End(ReportingOperation.Save).Outcome); Balanced(f);
});
await Check("Disabling restores the profile without DLL, network or runtime", async () =>
{
    using var f = new Fixture(enabled: true) { ForbidNetwork = true, ForbidRuntime = true, ForbidAdopt = true };
    f.Window.Settings.SaamanDllPath = f.Dll; // The previously selected copy has been removed.
    await f.Window.Enable(false);
    Equal(0, f.Count("runtime-check")); Equal(0, f.Count("release")); Equal(0, f.Count("install")); Equal(0, f.AdoptCount);
    False(f.Window.Settings.PatchSaaman); True(f.Window.Settings.SaamanProfileManaged);
    True(ApplicationPreferences.IsNotUsed(f.Window.Settings, "SAAMAN"));
    Equal("MAESTRO restored", File.ReadAllText(f.Profile)); False(f.Applied.Single().Policy.Enabled);
    Equal(ReportingOutcome.Completed, f.End(ReportingOperation.ConfigurePluginProfile).Outcome); Balanced(f);
});
await Check("Selecting a moved enabled DLL repairs the profile entirely offline", async () =>
{
    using var f = new Fixture(enabled: true) { ForbidNetwork = true, ForbidRuntime = true };
    f.Window.Settings.SaamanDllPath = f.Dll; // Old path is absent.
    f.SelectedDll = f.WriteDll(Path.Combine(f.Root, "relocated", "SAAMAN.dll"));
    await f.Window.Choose();
    Equal(f.SelectedDll, f.Window.Settings.SaamanDllPath); True(f.Window.Settings.PatchSaaman);
    Equal(f.SelectedDll, f.Applied.Single().Policy.DllPath);
    Equal(1, f.Count("plan-relocate")); Equal(0, f.Count("release")); Equal(0, f.Count("install")); Equal(0, f.Count("runtime-check"));
    Equal(ReportingOutcome.Completed, f.End(ReportingOperation.ConfigurePluginProfile).Outcome); Balanced(f);
});
await Check("Choosing a DLL while off saves it without activating the profile", async () =>
{
    using var f = new Fixture { ForbidNetwork = true, ForbidRuntime = true };
    f.SelectedDll = f.WriteDll(Path.Combine(f.Root, "selected", "SAAMAN.dll"));
    await f.Window.Choose();
    Equal(f.SelectedDll, f.Window.Settings.SaamanDllPath); False(f.Window.Settings.PatchSaaman);
    Equal(2, f.AdoptCount); Equal(0, f.Count("apply-profile")); Equal("original profile", File.ReadAllText(f.Profile));
    Equal(ReportingOutcome.Completed, f.End(ReportingOperation.Adopt).Outcome); Balanced(f);
});
await Check("Cancelling selection performs no operation", async () =>
{
    using var f = new Fixture();
    await f.Window.Choose();
    Equal(0, f.Starts.Count); Equal(0, f.Saved.Count); Equal(0, f.AdoptCount); Equal(0, f.Count("release"));
});
await Check("Cancellation after release lookup prevents installation and activation", async () =>
{
    using var f = new Fixture();
    f.OnLatest = f.Window.Cancel;
    await f.Window.Enable(true);
    Equal(0, f.Count("install")); Equal(0, f.Count("apply-profile")); Equal(0, f.Saved.Count);
    False(File.Exists(f.Dll)); False(f.Window.Settings.PatchSaaman);
    Equal(ReportingOutcome.Cancelled, f.End(ReportingOperation.Install).Outcome);
    True(f.Statuses.Last().Cancelled); False(f.Statuses.Last().Busy); False(f.Statuses.Last().CanCancel);
    Equal(ReportingOutcome.Cancelled, f.End(ReportingOperation.ConfigurePluginProfile).Outcome); Balanced(f);
});
await Check("Cancellation while downloading prevents file writes", async () =>
{
    using var f = new Fixture();
    f.OnDownload = f.Window.Cancel;
    await f.Window.Enable(true);
    Equal(1, f.Count("install")); Equal(0, f.Count("installed")); Equal(0, f.Count("apply-profile"));
    False(File.Exists(f.Dll)); Equal(0, f.Saved.Count);
    Equal(ReportingOutcome.Cancelled, f.End(ReportingOperation.ConfigurePluginProfile).Outcome); Balanced(f);
});
await Check("Cancellation arriving after DLL commit retains installation and reports partial", async () =>
{
    using var f = new Fixture();
    // The background commit has finished, but its posted Applying progress has not reached the UI yet.
    f.AfterInstall = f.Window.Cancel;
    await f.Window.Enable(true);
    True(File.Exists(f.Dll)); Equal(f.Dll, f.Window.Settings.SaamanDllPath);
    False(f.Window.Settings.PatchSaaman); Equal(0, f.Count("apply-profile"));
    Equal(ReportingOutcome.Completed, f.End(ReportingOperation.Install).Outcome);
    Equal(ReportingOutcome.Partial, f.End(ReportingOperation.ConfigurePluginProfile).Outcome);
    True(f.End(ReportingOperation.ConfigurePluginProfile).Details?.RecoveryAvailable == true); Balanced(f);
});
await Check("Missing WebView2 blocks enable without running its installer", async () =>
{
    using var f = new Fixture { RuntimeAvailable = false };
    await f.Window.Enable(true);
    True(f.Window.NeedsRuntime); False(f.Window.Settings.PatchSaaman);
    Equal(0, f.Count("runtime-install")); Equal(0, f.Count("release")); Equal(0, f.Count("apply-profile")); Equal(0, f.Saved.Count);
    Equal(ReportingOutcome.Blocked, f.End(ReportingOperation.ConfigurePluginProfile).Outcome);
    Equal(ReportingReason.MissingConfiguration, f.End(ReportingOperation.ConfigurePluginProfile).Reason); Balanced(f);
});
await Check("The explicit runtime action installs only the runtime and preserves restart", async () =>
{
    using var f = new Fixture { RuntimeAvailable = false, RuntimeRestart = true };
    await f.Window.InstallRuntime();
    Equal(1, f.Count("runtime-install")); False(f.Window.NeedsRuntime); True(f.Window.RestartRequired);
    Equal(0, f.Count("release")); Equal(0, f.Count("install")); Equal(0, f.Count("apply-profile")); Equal(0, f.Saved.Count);
    False(f.Window.Settings.PatchSaaman);
    True(f.Activities.Single().RestartRequired); Balanced(f);
});
await Check("A running EuroScope blocks enable before runtime or network checks", async () =>
{
    using var f = new Fixture { Running = true };
    await f.Window.Enable(true);
    Equal(0, f.Count("runtime-check")); Equal(0, f.Count("release")); Equal(0, f.Saved.Count);
    False(File.Exists(f.Dll)); Equal(0, f.Count("apply-profile")); Balanced(f);
});
await Check("EuroScope starting after download blocks activation and keeps the installed DLL", async () =>
{
    using var f = new Fixture();
    f.AfterInstall = () => f.Running = true;
    await f.Window.Enable(true);
    True(File.Exists(f.Dll)); False(f.Window.Settings.PatchSaaman); Equal(0, f.Count("apply-profile"));
    Equal("original profile", File.ReadAllText(f.Profile));
    Equal(ReportingOutcome.Partial, f.End(ReportingOperation.ConfigurePluginProfile).Outcome); Balanced(f);
});
await Check("A missing target profile prevents downloading a plugin", async () =>
{
    using var f = new Fixture();
    File.Delete(f.Profile);
    await f.Window.Enable(true);
    Equal(0, f.Count("release")); Equal(0, f.Count("runtime-check")); Equal(0, f.Saved.Count);
    False(File.Exists(f.Dll)); Balanced(f);
});
await Check("A pending GNG operation prevents changes", async () =>
{
    using var f = new Fixture(installed: true) { PendingGng = true };
    await f.Window.Enable(true);
    Equal(0, f.Count("release")); Equal(0, f.AdoptCount); Equal(0, f.Count("apply-profile")); Equal(0, f.Saved.Count); Balanced(f);
});
await Check("Busy maintenance lease prevents disabled-profile writes", async () =>
{
    using var f = new Fixture(enabled: true) { LeaseAvailable = false, ForbidNetwork = true, ForbidRuntime = true };
    await f.Window.Enable(false);
    True(f.Window.Settings.PatchSaaman); Equal(0, f.Count("apply-profile")); Equal(0, f.Saved.Count);
    Equal("original profile", File.ReadAllText(f.Profile)); Balanced(f);
});
await Check("Profile plan DLL mismatch rejects activation", async () =>
{
    using var f = new Fixture(installed: true) { BadPlanHash = true };
    await f.Window.Enable(true);
    Equal(0, f.Count("apply-profile")); Equal(0, f.Saved.Count); False(f.Window.Settings.PatchSaaman); Balanced(f);
});
await Check("Relocation DLL mismatch preserves the old selected path", async () =>
{
    using var f = new Fixture(enabled: true) { BadPlanHash = true };
    f.Window.Settings.SaamanDllPath = f.Dll;
    f.SelectedDll = f.WriteDll(Path.Combine(f.Root, "selected", "SAAMAN.dll"));
    await f.Window.Choose();
    Equal(f.Dll, f.Window.Settings.SaamanDllPath); Equal(0, f.Count("apply-profile")); Equal(0, f.Saved.Count); Balanced(f);
});
await Check("A DLL changing between adoption checks is not saved", async () =>
{
    using var f = new Fixture { ChangedSecondIdentity = true };
    f.SelectedDll = f.WriteDll(Path.Combine(f.Root, "selected", "SAAMAN.dll"));
    await f.Window.Choose();
    Equal("", f.Window.Settings.SaamanDllPath); Equal(0, f.Saved.Count); Equal(0, f.Count("apply-profile")); Balanced(f);
});
await Check("An identical current version does not reinstall or alter enabled state", async () =>
{
    using var f = new Fixture(installed: true, enabled: true);
    await f.Window.Update();
    Equal(1, f.Count("release")); Equal(0, f.Count("install")); Equal(0, f.Count("apply-profile"));
    True(f.Window.Settings.PatchSaaman); True(f.Window.ReleaseChecked);
    Equal(ReportingReason.AlreadyConfigured, f.End(ReportingOperation.Update).Reason); Balanced(f);
});
await Check("Identical bytes with a different version still require installation", async () =>
{
    using var f = new Fixture(installed: true) { InstalledVersion = "1.2.2" };
    await f.Window.Update();
    True(f.Statuses.Any(s => s.Busy && s.CanCancel));
    False(f.Statuses.Last().Busy); False(f.Statuses.Last().CanCancel); False(f.Statuses.Last().Warning);
    Equal(1, f.Count("install")); False(f.Window.Settings.PatchSaaman); Equal(0, f.Count("apply-profile")); Balanced(f);
});
await Check("Saving SAAMAN choices does not overwrite unrelated edits made during download", async () =>
{
    using var f = new Fixture();
    f.AfterInstall = () =>
    {
        f.Window.Settings.TrackAudioExePath = "new synthetic path";
        f.Window.Settings.TextScale = 1.5;
        f.Window.Settings.VatsimName = "Updated Synthetic Controller";
        f.Window.Settings.PatchVatEfs = true;
    };
    await f.Window.Enable(true);
    Equal("new synthetic path", f.Window.Settings.TrackAudioExePath); Equal(1.5, f.Window.Settings.TextScale);
    Equal("Updated Synthetic Controller", f.Window.Settings.VatsimName); True(f.Window.Settings.PatchVatEfs);
    True(f.Saved.All(s => s.TrackAudioExePath == "new synthetic path" && s.PatchVatEfs)); Balanced(f);
});
await Check("Cancellation is disabled during profile application", async () =>
{
    using var f = new Fixture(installed: true);
    f.BeforeProfile = () => { False(f.Window.CanCancel); f.Window.Cancel(); };
    await f.Window.Enable(true);
    True(f.Window.Settings.PatchSaaman); Equal(ReportingOutcome.Completed, f.End(ReportingOperation.ConfigurePluginProfile).Outcome); Balanced(f);
});

Console.WriteLine($"{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

async Task Check(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failed++; Console.WriteLine("FAIL " + name + ": " + exception.Message); }
}
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void False(bool value) => True(!value);
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; observed {actual}.");
}
static void Balanced(Fixture f)
{
    False(f.Window.Busy); False(f.Window.CanCancel); False(f.LeaseHeld);
    Equal(f.Starts.Count, f.Completions.Count);
    foreach (var operation in f.Starts.Distinct())
        Equal(f.Starts.Count(op => op == operation), f.Completions.Count(c => c.Operation == operation));
    Equal(f.Count("timer-stop"), f.Count("timer-start"));
}
