using VatscaUpdateChecker.Services;

int passed = 0, failed = 0;
Check("fresh destination creates exactly the empty EuroScope child", f =>
{
    string actual = GngSetupDestination.PrepareDefault(f.Roaming);
    Equal(f.Destination, actual);
    True(Directory.Exists(actual));
    Equal(0, Directory.EnumerateFileSystemEntries(actual).Count());
    Equal(1, Directory.EnumerateFileSystemEntries(f.Roaming).Count());
});
Check("existing package files are retained and repeated preparation is harmless", f =>
{
    Directory.CreateDirectory(f.Destination);
    string file = Path.Combine(f.Destination, "synthetic.prf");
    File.WriteAllText(file, "untouched synthetic content");
    Equal(f.Destination, GngSetupDestination.PrepareDefault(f.Roaming));
    Equal(f.Destination, GngSetupDestination.PrepareDefault(f.Roaming));
    Equal("untouched synthetic content", File.ReadAllText(file));
    Equal(1, Directory.EnumerateFileSystemEntries(f.Destination).Count());
});
Check("a file occupying the destination is never replaced", f =>
{
    File.WriteAllText(f.Destination, "keep this file");
    Reject(() => GngSetupDestination.PrepareDefault(f.Roaming));
    Equal("keep this file", File.ReadAllText(f.Destination));
});
Check("a junction at the destination is refused", f =>
{
    SyntheticJunction.Create(f.Destination, f.Outside);
    Reject(() => GngSetupDestination.PrepareDefault(f.Roaming));
    Equal(0, Directory.EnumerateFileSystemEntries(f.Outside).Count());
});
Check("a redirected roaming directory is refused", f =>
{
    string linked = Path.Combine(f.Root, "linked-roaming");
    SyntheticJunction.Create(linked, f.Outside);
    Reject(() => GngSetupDestination.PrepareDefault(linked));
    False(Directory.Exists(Path.Combine(f.Outside, "EuroScope")));
});
Check("a linked ancestor is refused before creating descendants", f =>
{
    string linked = Path.Combine(f.Root, "linked-parent");
    string roaming = Path.Combine(f.Outside, "roaming");
    Directory.CreateDirectory(roaming);
    SyntheticJunction.Create(linked, f.Outside);
    Reject(() => GngSetupDestination.PrepareDefault(Path.Combine(linked, "roaming")));
    Equal(0, Directory.EnumerateFileSystemEntries(roaming).Count());
});
Check("missing roaming ancestry is not invented", f =>
{
    string missing = Path.Combine(f.Root, "missing", "roaming");
    Reject(() => GngSetupDestination.PrepareDefault(missing));
    False(Directory.Exists(Path.GetDirectoryName(missing)));
});
Check("unsafe roots are rejected without creating a destination", f =>
{
    foreach (string invalid in new[] { "", " ", "relative", @"\\example.invalid\share", Path.GetPathRoot(f.Root)!,
        f.Roaming + "\\..\\Other", f.Roaming + "\n", f.Roaming + ".", f.Roaming + " " })
        Reject(() => GngSetupDestination.PrepareDefault(invalid));
    False(Directory.Exists(f.Destination));
});
Console.WriteLine($"GNG setup destination: {passed} passed; {failed} failed (synthetic directories only).");
return failed == 0 ? 0 : 1;

void Check(string name, Action<Fixture> test)
{
    try
    {
        using var f = new Fixture();
        test(f); passed++; Console.WriteLine("PASS " + name);
    }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void False(bool value) => True(!value);
static void Equal<T>(T expected, T actual)
{ if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; observed {actual}."); }
static void Reject(Action action)
{
    try { action(); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Setup → Swedish GNG", StringComparison.Ordinal)) { return; }
    throw new InvalidOperationException("An unsafe destination was accepted.");
}

sealed class Fixture : IDisposable
{
    // Reuse the junction helper's constrained synthetic-root convention.
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Launchpad-client-configuration-tests-gng-" + Guid.NewGuid().ToString("N"));
    public string Roaming => Path.Combine(Root, "roaming");
    public string Destination => Path.Combine(Roaming, "EuroScope");
    public string Outside => Path.Combine(Root, "outside");
    public Fixture() { Directory.CreateDirectory(Roaming); Directory.CreateDirectory(Outside); }
    public void Dispose()
    {
        string relative = Path.GetRelativePath(Path.GetTempPath(), Path.GetFullPath(Root));
        if (!relative.StartsWith("Launchpad-client-configuration-tests-gng-", StringComparison.Ordinal) || relative.Contains(Path.DirectorySeparatorChar))
            throw new IOException("Fixture cleanup escaped its temporary root.");
        DeleteOwned(Root);
    }
    private static void DeleteOwned(string directory)
    {
        foreach (string child in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.Directory) == 0) File.Delete(child);
            else if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(child);
            else DeleteOwned(child);
        }
        Directory.Delete(directory);
    }
}
