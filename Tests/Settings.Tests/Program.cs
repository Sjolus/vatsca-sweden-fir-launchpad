using System.Security;
using System.Text.Json;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

// Every persistence call uses an in-memory writer. Never load or write real settings,
// access Credential Manager, inspect application paths or launch a process.
var tests = new (string Name, Action Run)[]
{
    ("successful save writes the current session choices once", () =>
    {
        var current = Choices();
        int writes = 0;
        AppSettings? saved = null;
        bool result = SettingsService.TrySave(current, settings => { writes++; saved = settings.Copy(); });
        Assert(result && writes == 1 && Snapshot(current) == Snapshot(saved!), "Current choices were not saved.");
    }),
    ("access denied keeps all session choices for a later retry", () => Failure(new UnauthorizedAccessException())),
    ("filesystem write failure keeps all session choices for a later retry", () => Failure(new IOException())),
    ("security denial keeps all session choices for a later retry", () => Failure(new SecurityException())),
    ("retry saves subsequent edits together with retained setup and profile choices", () =>
    {
        var current = Choices();
        Assert(!SettingsService.TrySave(current, _ => throw new IOException()), "Failure was not reported.");
        current.TrackAudioExePath = @"C:\Synthetic\New TrackAudio\trackaudio.exe";
        current.CheckOnStartup = false;
        AppSettings? saved = null;
        Assert(SettingsService.TrySave(current, settings => saved = settings.Copy()), "Retry did not succeed.");
        Assert(Snapshot(saved!) == Snapshot(current), "Retry discarded choices retained after failure.");
        Assert(saved!.SetupWizardCompleted && saved.VatsimName == "Åsa Östergård" &&
            saved.LastEuroscopeProfile.EndsWith("ESAA TWR.prf"), "Completed choices were lost.");
    })
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine("PASS " + test.Name);
}
Console.WriteLine($"{tests.Length}/{tests.Length} settings persistence checks passed; no user files or credentials accessed.");

static void Failure(Exception error)
{
    var current = Choices();
    var before = Snapshot(current);
    int attempts = 0;
    bool saved = SettingsService.TrySave(current, settings =>
    {
        Assert(ReferenceEquals(settings, current), "The writer received stale or replacement settings.");
        attempts++;
        throw error;
    });
    Assert(!saved && attempts == 1, "Failure did not return a retryable result.");
    Assert(Snapshot(current) == before, "A failed save changed the authoritative session choices.");
}

static AppSettings Choices() => new()
{
    CheckOnStartup = true, IsDarkMode = true, CompactLayout = false,
    SetupWizardCompleted = true, SetupWizardDismissed = false,
    EuroscopeExePath = @"C:\Synthetic\EuroScope\EuroScope.exe",
    EuroscopeDataPath = @"C:\Synthetic\GNG", TrackAudioExePath = @"C:\Synthetic\TrackAudio\trackaudio.exe",
    VacsExePath = @"C:\Synthetic\VACS\vacs-client.exe", VatisExePath = @"C:\Synthetic\vATIS\vATIS.exe",
    VatEfsPath = @"C:\Synthetic\VatEFS", PatchVatEfs = true,
    VatsimName = "Åsa Östergård", VatsimRating = 1, VatsimCid = "1234567", ObsCallsign = "TEST_OBS",
    LastEuroscopeProfile = @"C:\Synthetic\GNG\ESAA TWR.prf"
};
static string Snapshot(AppSettings settings) => JsonSerializer.Serialize(settings);
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
