using VatscaUpdateChecker.Services;

internal static class Program
{
    private static int _passed;
    private static void Main(string[] args)
    {
        Run("machine context is explicit and does not query a user SID", f =>
        {
            var proof = f.Read();
            Equal("AllUsers", proof.Scope); Equal(f.Data, proof.DataRoot);
            Assert(f.Calls.All(call => call.Context == 4 && call.Sid == null));
            Assert(proof.ComponentPaths.Contains(Path.Combine(f.Data, "alias.txt")));
            Equal(64, proof.Fingerprint.Length);
        });
        Run("unmanaged user context passes only the current SID", f =>
        {
            f.Registration = f.Registration with { Scope = "CurrentUser" }; f.Contexts = [2];
            f.Read(); Assert(f.Calls.All(call => call.Sid == Fixture.Sid && call.Context is 1 or 2));
            Assert(f.ComponentCalls.All(context => context == 2));
        });
        Run("managed user context stays managed", f =>
        {
            f.Registration = f.Registration with { Scope = "CurrentUser" }; f.Contexts = [1];
            f.Read(); Assert(f.ComponentCalls.All(context => context == 1));
        });
        Run("ambiguous user contexts fail closed", f =>
        {
            f.Registration = f.Registration with { Scope = "CurrentUser" }; f.Contexts = [1, 2]; Reject(f);
        });
        Run("missing context fails closed", f => { f.Contexts = []; Reject(f); });
        Run("unknown scope fails closed", f => { f.Registration = f.Registration with { Scope = "unknown" }; Reject(f); });
        Run("untrusted registration is rejected before native lookup", f =>
        {
            f.Registration = f.Registration with { WindowsInstaller = false }; Reject(f); Equal(0, f.Calls.Count);
        });
        Run("wrong cached product is rejected", f => { f.Metadata = f.Metadata with { ProductCode = Guid.NewGuid().ToString("B") }; Reject(f); });
        Run("wrong cached upgrade family is rejected", f => { f.Metadata = f.Metadata with { UpgradeCode = Guid.NewGuid().ToString("B") }; Reject(f); });
        Run("wrong cached publisher is rejected", f => { f.Metadata = f.Metadata with { Manufacturer = "Other" }; Reject(f); });
        Run("wrong cached version is rejected", f => { f.Metadata = f.Metadata with { Version = "3.2.4" }; Reject(f); });
        Run("three-field MSI version agrees with zero fourth field", f => { f.Registration = f.Registration with { Version = "3.2.3.0" }; f.Read(); });
        Run("cache outside Windows Installer directory is rejected", f => { f.Cache = Path.Combine(f.Root, "copied.msi"); File.WriteAllText(f.Cache, "fake"); Reject(f); });
        Run("unsafe executable actions are rejected", f => { f.Metadata = f.Metadata with { HasUnboundedActions = true }; Reject(f); });
        Run("another account's data path is not covered by standard-root backup", f =>
        {
            f.Paths[Fixture.DataId] = new(3, Path.Combine(f.Base, "another user", "EuroScope", "Hungarian Matias.prf")); Reject(f);
        });
        Run("lookalike directory prefix is rejected", f => { f.Paths[Fixture.DataId] = new(3, Path.Combine(f.Data + "-other", "Hungarian Matias.prf")); Reject(f); });
        Run("custom data path outside reviewed roots is rejected", f => { f.Paths[Fixture.DataId] = new(3, Path.Combine(f.Base, "custom", "Hungarian Matias.prf")); Reject(f); });
        Run("legacy Documents path needs manual review", f => { f.Paths[Fixture.DataId] = new(3, Path.Combine(f.Base, "Documents", "EuroScope", "Hungarian Matias.prf")); Reject(f); });
        Run("missing component destination fails closed", f => { f.Paths[Fixture.DataId] = new(2, null); Reject(f); });
        Run("broken component destination fails closed", f => { f.Paths[Fixture.DataId] = new(-7, Path.Combine(f.Data, "Hungarian Matias.prf")); Reject(f); });
        Run("component basename must agree with MSI key file", f => { f.Paths[Fixture.DataId] = new(3, Path.Combine(f.Data, "other.prf")); Reject(f); });
        Run("all non-key files are included in the derived inventory", f =>
        {
            var proof = f.Read(); Assert(proof.ComponentPaths.Contains(Path.Combine(f.Data, "alias.txt")));
            Equal(4, proof.ComponentPaths.Count);
        });
        Run("registry-only component does not become a filesystem path", f =>
        {
            f.Metadata = f.Metadata with { Components = f.Metadata.Components.Append(new EuroScopeMsiComponent(Guid.NewGuid().ToString("B"), 4, null, [])).ToArray() };
            Equal(4, f.Read().ComponentPaths.Count);
        });
        Run("registry-key component owning files needs manual review", f =>
        {
            f.Metadata = f.Metadata with { Components = f.Metadata.Components.Append(new EuroScopeMsiComponent(Guid.NewGuid().ToString("B"), 4, null, ["unknown.txt"])).ToArray() }; Reject(f);
        });
        Run("duplicate component IDs are rejected", f => { f.Metadata = f.Metadata with { Components = f.Metadata.Components.Append(f.Metadata.Components[0]).ToArray() }; Reject(f); });
        Run("component without a registration GUID is rejected", f => { f.Metadata = f.Metadata with { Components = [new("", 0, "EuroScope.exe", ["EuroScope.exe"])] }; Reject(f); });
        Run("MSI file traversal is rejected", f =>
        {
            f.Metadata = f.Metadata with { Components = [f.Metadata.Components[0], new(Fixture.DataId, 0, "Hungarian Matias.prf", ["Hungarian Matias.prf", @"..\outside.txt"])] }; Reject(f);
        });
        Run("alternate data streams are rejected", f => { f.Paths[Fixture.DataId] = new(3, Path.Combine(f.Data, "Hungarian Matias.prf:stream")); Reject(f); });
        Run("UNC component paths are rejected", f => { f.Paths[Fixture.DataId] = new(3, @"\\server\share\Hungarian Matias.prf"); Reject(f); });
        Run("Windows files outside Fonts are rejected", f => { f.Paths[Fixture.FontId] = new(3, Path.Combine(f.Windows, "System32", "EuroScope.ttf")); Reject(f); });
        Run("program-root relocation changes the proof", f =>
        {
            var a = f.Read(); File.AppendAllText(f.Cache, "changed package"); var b = f.Read(); Assert(a.Fingerprint != b.Fingerprint);
        });
        Run("component order does not change the proof", f =>
        {
            var a = f.Read(); f.Metadata = f.Metadata with { Components = f.Metadata.Components.Reverse().ToArray() };
            Equal(a.Fingerprint, f.Read().Fingerprint);
        });
        Run("directory key paths are included", f =>
        {
            string id = Guid.NewGuid().ToString("B");
            f.Metadata = f.Metadata with { Components = f.Metadata.Components.Append(new EuroScopeMsiComponent(id, 0, null, [])).ToArray() };
            f.Paths[id] = new(3, f.Data); Assert(f.Read().ComponentPaths.Contains(f.Data));
        });
        Run("cached package remains locked throughout metadata and path lookup", f =>
        {
            f.BeforeMetadata = () => Throws(() => File.WriteAllText(f.Cache, "race")); f.Read();
        });
        Run("dangling directory links fail closed", f =>
        {
            string link = Path.Combine(f.Data, "dangling");
            SyntheticJunction.Create(link, Path.Combine(f.Base, "missing-target"));
            try { f.Paths[Fixture.DataId] = new(3, Path.Combine(link, "Hungarian Matias.prf")); Reject(f); }
            finally { Directory.Delete(link); }
        });
        if (args.Length == 2 && args[0] == "--inspect-package")
        {
            var metadata = EuroScopeMsiFootprintNative.PackageMetadata(Path.GetFullPath(args[1]));
            Equal(EuroScopePolicy.ProductCode, metadata.ProductCode);
            Equal(EuroScopePolicy.UpgradeCode, metadata.UpgradeCode);
            Equal("3.2.3", metadata.Version); Equal(87, metadata.Components.Count); Assert(!metadata.HasUnboundedActions);
            _passed++; Console.WriteLine("PASS optional read-only parser check of the pinned research MSI");
        }
        else if (args.Length != 0) throw new ArgumentException("Use no arguments, or --inspect-package <pinned research MSI>.");
        Console.WriteLine($"Passed {_passed} EuroScope MSI footprint checks. No installed-product queries, installers, client launches or network requests.");
    }
    private static void Run(string name, Action<Fixture> action) { using var fixture = new Fixture(); action(fixture); _passed++; Console.WriteLine("PASS " + name); }
    private static void Reject(Fixture fixture) => Throws(() => fixture.Read());
    private static void Throws(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
        throw new Exception("Expected a closed validation failure.");
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T a, T b) { if (!EqualityComparer<T>.Default.Equals(a, b)) throw new Exception($"Expected {a}, got {b}."); }

    private sealed class Fixture : IDisposable
    {
        public const string Sid = "S-1-5-21-123-456-789-1001";
        public const string MainId = "{11111111-1111-1111-1111-111111111111}";
        public const string DataId = "{22222222-2222-2222-2222-222222222222}";
        public const string FontId = "{33333333-3333-3333-3333-333333333333}";
        public string Base { get; } = Path.Combine(Path.GetTempPath(), "Launchpad-footprint-tests", Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Base, "ProgramFiles", "EuroScope");
        public string Roaming => Path.Combine(Base, "caller", "Roaming");
        public string Data => Path.Combine(Roaming, "EuroScope");
        public string Windows => Path.Combine(Base, "Windows");
        public string Cache;
        public int[] Contexts = [4];
        public AtcMsiRegistration Registration;
        public EuroScopeMsiMetadata Metadata;
        public Dictionary<string, EuroScopeMsiComponentPath> Paths = [];
        public List<(string? Sid, int Context)> Calls = [];
        public List<int> ComponentCalls = [];
        public Action? BeforeMetadata;
        public Fixture()
        {
            Cache = Path.Combine(Windows, "Installer", "cached.msi");
            foreach (string path in new[] { Root, Data, Path.Combine(Windows, "Fonts"), Path.GetDirectoryName(Cache)! }) Directory.CreateDirectory(path);
            File.WriteAllText(Cache, "synthetic cached package");
            Registration = new(EuroScopePolicy.ProductCode, "EuroScope", "Gergely Csernák", "3.2.3", Root, "", true, "AllUsers");
            Metadata = new(Registration.ProductCode, EuroScopePolicy.UpgradeCode, "EuroScope", "Gergely Csernák", "3.2.3",
                [new(MainId, 0, "EuroScope.exe", ["EuroScope.exe"]), new(DataId, 0, "Hungarian Matias.prf", ["Hungarian Matias.prf", "alias.txt"]), new(FontId, 0, "EuroScope.ttf", ["EuroScope.ttf"])], false);
            Paths[MainId] = new(3, Path.Combine(Root, "EuroScope.exe"));
            Paths[DataId] = new(3, Path.Combine(Data, "Hungarian Matias.prf"));
            Paths[FontId] = new(3, Path.Combine(Windows, "Fonts", "EuroScope.ttf"));
        }
        public EuroScopeMsiFootprint Read() => EuroScopeMsiFootprint.Read(Registration, Root, Roaming, Windows, new()
        {
            CurrentUserSid = () => Sid,
            ProductProperty = (product, sid, context, property) => { Equal(Registration.ProductCode, product); Equal("LocalPackage", property); Calls.Add((sid, context)); return Contexts.Contains(context) ? Cache : null; },
            PackageMetadata = _ => { BeforeMetadata?.Invoke(); return Metadata; },
            ComponentPath = (product, component, sid, context) => { Equal(Registration.ProductCode, product); Calls.Add((sid, context)); ComponentCalls.Add(context); return Paths[component]; }
        });
        public void Dispose()
        {
            string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Launchpad-footprint-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Base).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture path.");
            Directory.Delete(Base, true);
        }
    }
}
