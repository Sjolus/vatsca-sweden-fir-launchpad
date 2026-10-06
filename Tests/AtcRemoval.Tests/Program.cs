using System.Text.Json;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

// Every removable path is generated inside a unique temporary fixture. No real apps or data are used.
var tests = new (string Name, Func<Task> Body)[]
{
    ("preview records exact bytes and empty directories without changing data", () =>
    {
        using var f = new Fixture(); var file = f.File("app/data/settings.txt", "FAKE-CREDENTIAL"); f.Dir("app/empty");
        var plan = RemovalFileService.Preview([f.PathOf("app")]);
        Equal(1, plan.Files.Count); Equal(3, plan.Directories.Count); Equal(15L, plan.TotalBytes);
        True(plan.Files.Single().FullPath == file && plan.Files.Single().Sha256.Length == 64);
        Equal("FAKE-CREDENTIAL", File.ReadAllText(file)); RemovalFileService.Verify(plan); return Task.CompletedTask;
    }),
    ("file roots can be backed up and removed without their siblings", async () =>
    {
        using var f = new Fixture(); var file = f.File("plugin/VatEFS.dll", "fake-dll"); var other = f.File("plugin/other.dll", "keep");
        var plan = RemovalFileService.Preview([file]); Equal("VatEFS.dll", plan.Files.Single().RelativePath);
        var backup = await RemovalFileService.BackupAsync(plan, f.Backups);
        Equal("fake-dll", File.ReadAllText(System.IO.Path.Combine(backup, "Files", "0000", "VatEFS.dll")));
        True((await RemovalFileService.DeleteAsync(plan)).Succeeded); True(!File.Exists(file) && File.Exists(other));
    }),
    ("backup includes complete selected inventory and restore instructions", async () =>
    {
        using var f = new Fixture(); f.File("app/settings.json", "FAKE-SECRET"); f.Dir("app/empty/nested"); f.File("single.txt", "root-file");
        var plan = RemovalFileService.Preview([f.PathOf("app"), f.PathOf("single.txt")]);
        var backup = await RemovalFileService.BackupAsync(plan, f.Backups);
        True(File.Exists(System.IO.Path.Combine(backup, "RESTORE.txt")));
        var json = File.ReadAllText(System.IO.Path.Combine(backup, "removal-backup.json"));
        True(!json.Contains("FAKE-SECRET")); using var manifest = JsonDocument.Parse(json);
        Equal(2, manifest.RootElement.GetProperty("Files").GetArrayLength());
        True(Directory.Exists(System.IO.Path.Combine(backup, "Files", "0000", "empty", "nested")));
        RemovalFileService.VerifyBackup(plan, backup);
        RemovalFileService.Verify(plan); True(File.Exists(f.PathOf("app/settings.json")));
    }),
    ("recovery verification rejects changed bytes metadata and inventory", async () =>
    {
        foreach (var mode in new[] { "bytes", "manifest", "instructions", "newfile", "directory" })
        {
            using var f = new Fixture(); f.File("app/file", "original"); f.Dir("app/empty");
            var plan = RemovalFileService.Preview([f.PathOf("app")]); var backup = await RemovalFileService.BackupAsync(plan, f.Backups);
            switch (mode)
            {
                case "bytes": File.WriteAllText(System.IO.Path.Combine(backup, "Files", "0000", "file"), "changed!"); break;
                case "manifest": File.WriteAllText(System.IO.Path.Combine(backup, "removal-backup.json"), "{}"); break;
                case "instructions": File.AppendAllText(System.IO.Path.Combine(backup, "RESTORE.txt"), "untrusted text"); break;
                case "newfile": File.WriteAllText(System.IO.Path.Combine(backup, "unreviewed"), "unexpected"); break;
                case "directory": Directory.Delete(System.IO.Path.Combine(backup, "Files", "0000", "empty")); break;
            }
            RejectSync(() => RemovalFileService.VerifyBackup(plan, backup)); True(File.Exists(f.PathOf("app/file")));
        }
    }),
    ("verified delete removes only previewed tree including empty folders", async () =>
    {
        using var f = new Fixture(); f.File("app/a", "a"); f.File("app/sub/b", "b"); f.Dir("app/empty"); f.File("other/keep", "safe");
        var result = await RemovalFileService.DeleteAsync(RemovalFileService.Preview([f.PathOf("app")]));
        True(result.Succeeded); Equal(2, result.DeletedFiles); Equal(3, result.DeletedDirectories);
        True(!Directory.Exists(f.PathOf("app")) && File.Exists(f.PathOf("other/keep")));
    }),
    ("changed contents prevent any deletion", async () =>
    {
        using var f = new Fixture(); var first = f.File("app/a", "first"); var last = f.File("app/z", "same-size");
        var plan = RemovalFileService.Preview([f.PathOf("app")]); File.WriteAllText(last, "different");
        await Reject(() => RemovalFileService.DeleteAsync(plan)); True(File.Exists(first) && File.Exists(last));
    }),
    ("same-byte replacement fails identity check", async () =>
    {
        using var f = new Fixture(); var file = f.File("app/file", "original"); var plan = RemovalFileService.Preview([f.PathOf("app")]);
        File.Move(file, f.PathOf("moved-original")); File.WriteAllText(file, "original");
        await Reject(() => RemovalFileService.DeleteAsync(plan)); True(File.Exists(file));
    }),
    ("added file or empty directory invalidates the entire plan", async () =>
    {
        foreach (bool directory in new[] { false, true })
        {
            using var f = new Fixture(); var file = f.File("app/a", "original"); var plan = RemovalFileService.Preview([f.PathOf("app")]);
            if (directory) f.Dir("app/new"); else f.File("app/new", "unreviewed");
            await Reject(() => RemovalFileService.DeleteAsync(plan)); True(File.Exists(file));
        }
    }),
    ("missing entries invalidate strict deletion", async () =>
    {
        using var f = new Fixture(); var a = f.File("app/a", "a"); var b = f.File("app/b", "b");
        var plan = RemovalFileService.Preview([f.PathOf("app")]); File.Delete(b);
        await Reject(() => RemovalFileService.DeleteAsync(plan)); True(File.Exists(a));
    }),
    ("remaining deletion permits only missing reviewed entries", async () =>
    {
        using var f = new Fixture(); var a = f.File("app/a", "a"); var b = f.File("app/sub/b", "b");
        var plan = RemovalFileService.Preview([f.PathOf("app")]); File.Delete(b); Directory.Delete(f.PathOf("app/sub"));
        RemovalFileService.VerifyRemaining(plan);
        var result = await RemovalFileService.DeleteRemainingAsync(plan);
        True(result.Succeeded); Equal(1, result.DeletedFiles); True(!Directory.Exists(f.PathOf("app")));
    }),
    ("remaining deletion rejects new or changed survivors", async () =>
    {
        foreach (bool add in new[] { false, true })
        {
            using var f = new Fixture(); var file = f.File("app/a", "original"); var plan = RemovalFileService.Preview([f.PathOf("app")]);
            if (add) f.File("app/new", "unreviewed"); else File.WriteAllText(file, "changed");
            await Reject(() => RemovalFileService.DeleteRemainingAsync(plan)); True(File.Exists(file));
        }
    }),
    ("remaining deletion rejects replaced root identity", async () =>
    {
        using var f = new Fixture(); f.File("app/a", "a"); var plan = RemovalFileService.Preview([f.PathOf("app")]);
        Directory.Move(f.PathOf("app"), f.PathOf("moved")); f.Dir("app");
        await Reject(() => RemovalFileService.DeleteRemainingAsync(plan)); True(Directory.Exists(f.PathOf("app")));
    }),
    ("missing roots and empty plans are safe noops", async () =>
    {
        using var f = new Fixture(); var file = f.File("one", "a"); var plan = RemovalFileService.Preview([file]); File.Delete(file);
        True((await RemovalFileService.DeleteRemainingAsync(plan)).Succeeded);
        var empty = RemovalFileService.Preview([]); RemovalFileService.Verify(empty);
        var result = await RemovalFileService.DeleteAsync(empty); True(result.Succeeded); Equal(0, result.DeletedFiles);
    }),
    ("duplicate and overlapping roots are rejected", () =>
    {
        using var f = new Fixture(); var file = f.File("app/sub/file", "a");
        RejectSync(() => RemovalFileService.Preview([f.PathOf("app"), f.PathOf("app/sub")]));
        RejectSync(() => RemovalFileService.Preview([f.PathOf("app"), file]));
        RejectSync(() => RemovalFileService.Preview([file, file.ToUpperInvariant()])); return Task.CompletedTask;
    }),
    ("protected broad roots are rejected without enumerating their files", () =>
    {
        foreach (var root in new[] { System.IO.Path.GetPathRoot(Environment.SystemDirectory)!, Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            RejectSync(() => RemovalFileService.Preview([root]));
        return Task.CompletedTask;
    }),
    ("relative device UNC traversal and ADS paths are rejected", () =>
    {
        using var f = new Fixture(); var file = f.File("file", "a");
        foreach (var path in new[] { "relative", @"\\server\share\folder", @"\\?\C:\Windows", file + ":hidden", f.PathOf("a/../file"), file + ".", file + " ", f.PathOf("NUL.txt"), f.PathOf("COM1"), f.PathOf("LPT¹") })
            RejectSync(() => RemovalFileService.Preview([path]));
        return Task.CompletedTask;
    }),
    ("named alternate streams are rejected instead of omitted", () =>
    {
        using var f = new Fixture(); var file = f.File("file", "a"); File.WriteAllText(file + ":synthetic", "hidden-data");
        RejectSync(() => RemovalFileService.Preview([file])); return Task.CompletedTask;
    }),
    ("backup inside removal root is rejected", async () =>
    {
        using var f = new Fixture(); f.File("app/a", "a"); f.Dir("app/backup"); var plan = RemovalFileService.Preview([f.PathOf("app")]);
        await Reject(() => RemovalFileService.BackupAsync(plan, f.PathOf("app/backup"))); True(File.Exists(f.PathOf("app/a")));
    }),
    ("stale backup plan copies nothing", async () =>
    {
        using var f = new Fixture(); var file = f.File("app/a", "a"); var plan = RemovalFileService.Preview([f.PathOf("app")]); File.WriteAllText(file, "changed");
        await Reject(() => RemovalFileService.BackupAsync(plan, f.Backups)); Equal(0, Directory.GetFileSystemEntries(f.Backups).Length);
    }),
    ("cancelled backup does not remove data and has no complete manifest", async () =>
    {
        using var f = new Fixture(); var file = f.File("app/a", "a"); var plan = RemovalFileService.Preview([f.PathOf("app")]);
        using var cancellation = new CancellationTokenSource();
        await Reject(() => RemovalFileService.BackupAsync(plan, f.Backups, new CallbackProgress(_ => cancellation.Cancel()), cancellation.Token));
        True(File.Exists(file)); var folder = Directory.GetDirectories(f.Backups).Single();
        True(File.Exists(System.IO.Path.Combine(folder, "INCOMPLETE.txt"))); True(!File.Exists(System.IO.Path.Combine(folder, "removal-backup.json")));
    }),
    ("file writes and directory replacement are blocked during apply", async () =>
    {
        using var f = new Fixture(); var file = f.File("app/a", "a"); var plan = RemovalFileService.Preview([f.PathOf("app")]);
        bool checkedLocks = false, writeBlocked = false, renameBlocked = false;
        var result = await RemovalFileService.DeleteAsync(plan, new CallbackProgress(_ =>
        {
            if (checkedLocks) return; checkedLocks = true;
            try { File.WriteAllText(file, "tamper"); } catch (IOException) { writeBlocked = true; }
            try { Directory.Move(f.PathOf("app"), f.PathOf("renamed")); } catch (IOException) { renameBlocked = true; }
        }));
        True(checkedLocks && writeBlocked && renameBlocked && result.Succeeded);
    }),
    ("late unreviewed file survives and prevents folder deletion", async () =>
    {
        using var f = new Fixture(); f.File("app/a", "a"); var plan = RemovalFileService.Preview([f.PathOf("app")]); bool added = false;
        var result = await RemovalFileService.DeleteAsync(plan, new CallbackProgress(_ => { if (!added) { added = true; f.File("app/late", "unreviewed"); } }));
        True(!result.Succeeded); Equal(1, result.DeletedFiles); Equal(1, result.Errors.Count); True(File.Exists(f.PathOf("app/late")));
    }),
    ("readonly selected files are removed by their verified handle", async () =>
    {
        using var f = new Fixture(); var file = f.File("app/read-only", "a"); File.SetAttributes(file, FileAttributes.ReadOnly);
        var result = await RemovalFileService.DeleteAsync(RemovalFileService.Preview([f.PathOf("app")]));
        True(result.Succeeded); True(!File.Exists(file));
    }),
    ("late named streams are blocked or preserved with an error", async () =>
    {
        using var f = new Fixture(); var file = f.File("file", "a"); var plan = RemovalFileService.Preview([file]); bool added = false;
        var result = await RemovalFileService.DeleteAsync(plan, new CallbackProgress(_ =>
        {
            try { File.WriteAllText(file + ":late", "unreviewed"); added = true; } catch (IOException) { }
        }));
        if (added) { True(!result.Succeeded); True(File.Exists(file)); }
        else { True(result.Succeeded); True(!File.Exists(file)); }
    }),
    ("open writable files prevent all deletion", async () =>
    {
        using var f = new Fixture(); var a = f.File("app/a", "a"); var z = f.File("app/z", "z"); var plan = RemovalFileService.Preview([f.PathOf("app")]);
        using var writer = new FileStream(z, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        await Reject(() => RemovalFileService.DeleteAsync(plan)); True(File.Exists(a));
    }),
    ("directory traversal depth is bounded", () =>
    {
        using var f = new Fixture(); var path = "app"; for (int i = 0; i <= RemovalFileService.MaximumDepth; i++) path += "/x";
        f.Dir(path); RejectSync(() => RemovalFileService.Preview([f.PathOf("app")])); return Task.CompletedTask;
    }),
    ("junction roots and ancestors never redirect removal", async () =>
    {
        using var f = new Fixture(); var target = f.File("target/sub/keep", "outside-selected-root");
        SyntheticNative.Junction(f.PathOf("alias"), f.PathOf("target"));
        RejectSync(() => RemovalFileService.Preview([f.PathOf("alias")]));
        RejectSync(() => RemovalFileService.Preview([f.PathOf("alias/sub/keep")]));
        f.File("app/keep", "selected"); SyntheticNative.Junction(f.PathOf("app/link"), f.PathOf("target"));
        RejectSync(() => RemovalFileService.Preview([f.PathOf("app")]));
        True(File.Exists(target));
        var plan = RemovalFileService.Preview([f.PathOf("target/sub/keep")]);
        await Reject(() => RemovalFileService.BackupAsync(plan, f.PathOf("alias"))); True(File.Exists(target));
    }),
    ("directory replaced by junction after preview stops all deletion", async () =>
    {
        using var f = new Fixture(); var first = f.File("app/a", "keep-first"); f.File("app/sub/b", "b");
        var outside = f.File("outside/keep", "do-not-touch"); var plan = RemovalFileService.Preview([f.PathOf("app")]);
        Directory.Move(f.PathOf("app/sub"), f.PathOf("moved")); SyntheticNative.Junction(f.PathOf("app/sub"), f.PathOf("outside"));
        await Reject(() => RemovalFileService.DeleteAsync(plan)); await Reject(() => RemovalFileService.DeleteRemainingAsync(plan));
        True(File.Exists(first)); Equal("do-not-touch", File.ReadAllText(outside));
    }),
    ("dangling junctions are rejected even when Exists reports false", async () =>
    {
        using var f = new Fixture(); f.Dir("app"); f.Dir("target");
        var plan = RemovalFileService.Preview([f.PathOf("app")]); Directory.Delete(f.PathOf("app"));
        SyntheticNative.Junction(f.PathOf("app"), f.PathOf("target")); Directory.Delete(f.PathOf("target"));
        RejectSync(() => RemovalFileService.Preview([f.PathOf("app")]));
        await Reject(() => RemovalFileService.DeleteRemainingAsync(plan));
    }),
    ("logical size is bounded before hashing sparse fixture", () =>
    {
        using var f = new Fixture(); var file = f.PathOf("oversized.bin");
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            SyntheticNative.MakeSparse(stream.SafeFileHandle);
            stream.SetLength(RemovalFileService.MaximumBytes + 1);
        }
        RejectSync(() => RemovalFileService.Preview([file])); return Task.CompletedTask;
    }),
    ("empty directory converted to a junction during apply is rejected", async () =>
    {
        using var f = new Fixture(); f.Dir("app"); f.File("outside/keep", "safe");
        var plan = RemovalFileService.Preview([f.PathOf("app")]); bool converted = false;
        var result = await RemovalFileService.DeleteAsync(plan, new CallbackProgress(_ =>
        {
            SyntheticNative.Junction(f.PathOf("app"), f.PathOf("outside")); converted = true;
        }));
        True(converted && !result.Succeeded); Equal("safe", File.ReadAllText(f.PathOf("outside/keep")));
    })
};
int failed = 0;
foreach (var (name, body) in tests)
{
    try { await body(); Console.WriteLine("PASS files: " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL files: " + name + ": " + ex); }
}
Console.WriteLine($"File engine: {tests.Length - failed}/{tests.Length} passed. Synthetic temporary files only.");
try { await CatalogTests.RunAsync(); } catch (Exception ex) { failed++; Console.WriteLine("FAIL catalog/vendor: " + ex); }
failed += await CoordinatorTests.Run();
return failed == 0 ? 0 : 1;

static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
static void RejectSync(Action action)
{
    try { action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return; }
    throw new Exception("Expected file operation rejection");
}
static async Task Reject(Func<Task> action)
{
    try { await action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException) { return; }
    throw new Exception("Expected file operation rejection");
}
sealed class CallbackProgress(Action<string> report) : IProgress<string> { public void Report(string message) => report(message); }
sealed class Fixture : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LaunchpadRemovalSynthetic-" + Guid.NewGuid().ToString("N"));
    public string Backups => PathOf("recovery");
    public Fixture() { Directory.CreateDirectory(Backups); }
    public string PathOf(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public string File(string relative, string contents)
    { var path = PathOf(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, contents); return path; }
    public string Dir(string relative) { var path = PathOf(relative); Directory.CreateDirectory(path); return path; }
    public void Dispose()
    {
        var full = System.IO.Path.GetFullPath(Root);
        if (System.IO.Path.GetDirectoryName(full) != System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath())
            || !System.IO.Path.GetFileName(full).StartsWith("LaunchpadRemovalSynthetic-", StringComparison.Ordinal)) throw new Exception("Unsafe fixture cleanup path");
        CleanDirectory(full);
    }
    private static void CleanDirectory(string path)
    {
        // Junction test fixtures must be unlinked, never traversed even during test cleanup.
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = System.IO.File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry, recursive: false);
                else CleanDirectory(entry);
            }
            else { System.IO.File.SetAttributes(entry, FileAttributes.Normal); System.IO.File.Delete(entry); }
        }
        Directory.Delete(path, recursive: false);
    }
}
