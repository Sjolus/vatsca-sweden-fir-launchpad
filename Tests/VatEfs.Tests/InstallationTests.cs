using VatscaUpdateChecker.Services;

internal static class InstallationTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("VatEFS registered package version matches the configured plugin folder", () =>
        {
            var fixture = new Fixture();
            var result = fixture.Read();
            Require(result.PluginFound && result.Version == "0.0.15", "Registered package version missing.");
            Require(result.Detail.Contains("Windows Installer"), "Version source was not described.");
        });
        test("VatEFS folder matching ignores case and trailing directory separators", () =>
        {
            var fixture = new Fixture();
            var result = fixture.Read(fixture.Root.ToUpperInvariant() + Path.DirectorySeparatorChar);
            Require(result.Version == "0.0.15", "Equivalent folder spelling was rejected.");
        });
        test("VatEFS blank or missing plugin paths do not enumerate products", () =>
        {
            var fixture = new Fixture();
            fixture.Registrations = () => throw new Exception("Should not inspect registration.");
            Require(!fixture.Read("").PluginFound, "Blank path was treated as installed.");
            fixture.Files.Remove(Path.Combine(fixture.Root, "VatEFS.dll"));
            Require(!fixture.Read().PluginFound, "Missing plugin was treated as installed.");
        });
        test("VatEFS plugin-only copies retain an unknown package version", () =>
        {
            var fixture = new Fixture();
            fixture.Files.Remove(Path.Combine(fixture.Root, "efs.exe"));
            fixture.Registrations = () => throw new Exception("Should not inspect registration.");
            RequireUnknown(fixture.Read());
        });
        test("VatEFS never borrows a version from another registered folder", () =>
        {
            var fixture = new Fixture();
            fixture.Products = [fixture.Products[0] with { ReadmeComponentPath = Path.Combine(fixture.Root + "-other", "README.txt") }];
            RequireUnknown(fixture.Read());
        });
        test("VatEFS unregistered and ambiguous registered copies remain unknown", () =>
        {
            var fixture = new Fixture();
            var product = fixture.Products[0];
            fixture.Products = [];
            RequireUnknown(fixture.Read());
            fixture.Products = [product, product with { ProductCode = "{DDDDDDDD-1111-2222-3333-444444444444}", Version = "0.0.14" }];
            RequireUnknown(fixture.Read());
            fixture.Products = [product, product with { ProductCode = "{DDDDDDDD-1111-2222-3333-444444444444}", Version = "unreported" }];
            RequireUnknown(fixture.Read());
        });
        test("VatEFS requires the expected MSI identity and README component", () =>
        {
            foreach (string field in new[] { "product", "publisher", "code", "keypath", "relative", "missing" })
            {
                var fixture = new Fixture();
                var product = fixture.Products[0];
                fixture.Products = [field switch
                {
                    "product" => product with { Name = "Different application" },
                    "publisher" => product with { Publisher = "Different publisher" },
                    "code" => product with { ProductCode = "not-a-product-code" },
                    "keypath" => product with { ReadmeComponentPath = Path.Combine(fixture.Root, "different.txt") },
                    "relative" => product with { ReadmeComponentPath = "README.txt" },
                    _ => product with { ReadmeComponentPath = null }
                }];
                RequireUnknown(fixture.Read());
            }
        });
        test("VatEFS missing registered README does not establish an installed version", () =>
        {
            var fixture = new Fixture();
            fixture.Files.Remove(Path.Combine(fixture.Root, "README.txt"));
            RequireUnknown(fixture.Read());
        });
        test("VatEFS rejects malformed or non-MSI registered versions", () =>
        {
            foreach (string version in new[] { "", "v0.0.15", "0.0", "0.0.15.0", "0.0.15-beta", "0.0.15+build", "00.0.15", "256.0.15", "0.0.65536", new string('1', 1024) })
            {
                var fixture = new Fixture();
                fixture.Products = [fixture.Products[0] with { Version = version }];
                RequireUnknown(fixture.Read());
            }
        });
        test("VatEFS registered-version lookup rejects redirected files and ancestors", () =>
        {
            foreach (string file in new[] { "VatEFS.dll", "efs.exe", "README.txt" })
            {
                var fixture = new Fixture();
                fixture.Unredirected = path => !string.Equals(path, Path.Combine(fixture.Root, file), StringComparison.OrdinalIgnoreCase);
                RequireUnknown(fixture.Read());
            }
            var redirectedRoot = new Fixture { Unredirected = _ => false };
            RequireUnknown(redirectedRoot.Read());
        });
        test("VatEFS registration failures preserve plugin detection without exposing error contents", () =>
        {
            var fixture = new Fixture();
            fixture.Registrations = () => throw new IOException("synthetic-sensitive-value");
            var result = fixture.Read();
            RequireUnknown(result);
            Require(!result.Detail.Contains("synthetic-sensitive-value"), "Raw failure text reached the UI.");
        });
        test("VatEFS limits the number of registrations considered", () =>
        {
            var fixture = new Fixture();
            fixture.Products = Enumerable.Repeat(fixture.Products[0], 33).ToArray();
            RequireUnknown(fixture.Read());
        });
        test("VatEFS configured relative and device paths are not treated as installed", () =>
        {
            var fixture = new Fixture();
            fixture.Registrations = () => throw new Exception("Should not inspect registration.");
            Require(!fixture.Read("relative-folder").PluginFound, "Relative path was accepted.");
            Require(!fixture.Read(@"\\?\C:\VatEFS").PluginFound, "Device path was accepted.");
        });
        test("VatEFS checks only filenames inside the configured folder", () =>
        {
            var fixture = new Fixture();
            var queried = new List<string>();
            var result = VatEfsInstallationProbe.Read(fixture.Root, () => fixture.Products,
                path => { queried.Add(path); return fixture.Files.Contains(path); }, _ => true);
            Require(result.Version == "0.0.15", "Fixture was not recognized.");
            Require(queried.All(path => fixture.Files.Contains(path)), "Probe searched outside its configured filenames.");
            Require(queried.Count == 3, "Probe read unnecessary files.");
        });
    }

    private static void RequireUnknown(VatEfsInstallation result) =>
        Require(result.PluginFound && result.Version == null && !string.IsNullOrEmpty(result.Detail), "Expected a detected plugin with unknown package version.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Fixture
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "VatEfs-probe-synthetic");
        internal HashSet<string> Files { get; }
        internal IReadOnlyList<VatEfsMsiRegistration> Products { get; set; }
        internal Func<IReadOnlyList<VatEfsMsiRegistration>> Registrations { get; set; }
        internal Func<string, bool> Unredirected { get; set; } = _ => true;

        internal Fixture()
        {
            Files = new(new[] { "VatEFS.dll", "efs.exe", "README.txt" }.Select(name => Path.Combine(Root, name)), StringComparer.OrdinalIgnoreCase);
            Products = [new("{AAAAAAAA-1111-2222-3333-444444444444}", "VatEFS", "Martin Insulander", "0.0.15", Path.Combine(Root, "README.txt"))];
            Registrations = () => Products;
        }

        internal VatEfsInstallation Read(string? path = null) =>
            VatEfsInstallationProbe.Read(path ?? Root, Registrations, Files.Contains, Unredirected);
    }
}
