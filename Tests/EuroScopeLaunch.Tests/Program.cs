using System.Diagnostics;
using VatscaUpdateChecker.Services;

// Synthetic paths only. Inspect ProcessStartInfo; never call Process.Start or read user settings.
var root = Path.Combine(Path.GetTempPath(), "Launchpad-EuroScope-launch-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var data = Folder("GNG Åland");
    var other = Folder("Other profile folder");
    var fallback = Folder("Synthetic Roaming EuroScope");
    var exe = Path.Combine(root, "Program files", "EuroScope.exe");
    var profile = Path.Combine(data, "ESAA TWR Ö.prf");
    var otherProfile = Path.Combine(other, "ESAA APP.prf");
    File.WriteAllText(profile, "synthetic");
    File.WriteAllText(otherProfile, "synthetic");
    var initialCurrentDirectory = Environment.CurrentDirectory;
    var tests = new (string Name, Action Run)[]
    {
        ("configured folder and selected profile preserve spaces and Swedish characters", () =>
            Check(EuroScopeLaunchService.CreateStartInfo(exe, data, profile, fallback), data, profile)),
        ("no-profile launch uses configured folder without arguments", () =>
            Check(EuroScopeLaunchService.CreateStartInfo(exe, data, null, fallback), data, null)),
        ("configured folder takes precedence over selected profile parent", () =>
            Check(EuroScopeLaunchService.CreateStartInfo(exe, data, otherProfile, fallback), data, otherProfile)),
        ("blank configuration with profile uses its parent", () =>
            Check(EuroScopeLaunchService.CreateStartInfo(exe, " ", otherProfile, fallback), other, otherProfile)),
        ("blank configuration without profile uses per-user fallback", () =>
            Check(EuroScopeLaunchService.CreateStartInfo(exe, null, null, fallback), fallback, null)),
        ("missing configured folder does not silently use profile or fallback", () =>
            Reject(() => EuroScopeLaunchService.CreateStartInfo(exe, Path.Combine(root, "Missing"), profile, fallback), "EuroScope folder for Swedish GNG data")),
        ("relative configured folder is rejected instead of depending on Launchpad cwd", () =>
            Reject(() => EuroScopeLaunchService.CreateStartInfo(exe, "relative-data", profile, fallback), "EuroScope folder for Swedish GNG data")),
        ("missing default folder requires choosing a data folder", () =>
            Reject(() => EuroScopeLaunchService.CreateStartInfo(exe, null, null, Path.Combine(root, "Missing default")), "EuroScope folder for Swedish GNG data")),
        ("missing selected profile blocks launch with recovery guidance", () =>
            Reject(() => EuroScopeLaunchService.CreateStartInfo(exe, data, Path.Combine(data, "Missing.prf"), fallback), "Choose another profile")),
        ("relative selected profile is rejected", () =>
            Reject(() => EuroScopeLaunchService.CreateStartInfo(exe, data, "ESAA.prf", fallback), "profile again")),
        ("relative executable is rejected", () =>
            Reject(() => EuroScopeLaunchService.CreateStartInfo("EuroScope.exe", data, null, fallback), "executable path"))
    };
    foreach (var test in tests)
    {
        test.Run();
        if (Environment.CurrentDirectory != initialCurrentDirectory) throw new Exception("Global working directory changed.");
        Console.WriteLine("PASS " + test.Name);
    }
    Console.WriteLine($"{tests.Length}/{tests.Length} EuroScope launch checks passed; no processes started.");

    string Folder(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
    void Check(ProcessStartInfo start, string expectedDirectory, string? expectedProfile)
    {
        if (start.FileName != exe || start.WorkingDirectory != expectedDirectory || start.UseShellExecute ||
            start.Arguments != "" || start.ArgumentList.Count != (expectedProfile == null ? 0 : 1) ||
            expectedProfile != null && start.ArgumentList[0] != expectedProfile)
            throw new Exception("Launch instructions do not match the reviewed paths.");
    }
    static void Reject(Action action, string message)
    {
        try { action(); }
        catch (IOException ex) when (ex.Message.Contains(message, StringComparison.Ordinal)) { return; }
        throw new Exception("Unsafe launch was not rejected with actionable guidance.");
    }
}
finally
{
    var full = Path.GetFullPath(root);
    if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(full).StartsWith("Launchpad-EuroScope-launch-tests-", StringComparison.Ordinal))
        throw new Exception("Refusing cleanup outside the synthetic temporary root.");
    Directory.Delete(full, recursive: true);
}
