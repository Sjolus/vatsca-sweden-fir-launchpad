using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class DllScannerTests
{
    internal static int Run()
    {
        int passed = 0;
        Check("DLL discovery includes unfamiliar names and exposes no local names or resource identity strings", () =>
        {
            using var f = new Fixture(); var (data, plugins) = Folders(f);
            byte[] first = InventoryPeFixture.Create("1.2.3.4", productName: "Synthetic private controller identity");
            byte[] second = [1, 2, 3, 4];
            File.WriteAllBytes(Path.Combine(plugins, "private-controller-name.dll"), first);
            File.WriteAllBytes(Path.Combine(plugins, "unfamiliar-tool.DLL"), second);
            File.WriteAllText(Path.Combine(plugins, "profile.prf"), "synthetic secret, never read");
            var inventory = ReportingInventoryCollector.CollectDllInventory(data, null);
            Assert(inventory.Status == ReportingDllInventoryStatus.Complete && inventory.Entries.Count == 2);
            Assert(inventory.Entries.All(item => item.Product == ReportingDllProduct.Unknown && item.Copies == 1));
            Assert(inventory.Entries.Single(item => item.Sha256 == Hash(first)).Version == "1.2.3.4");
            var unknown = inventory.Entries.Single(item => item.Sha256 == Hash(second));
            Assert(unknown.Version is null && unknown.VersionSource == ReportingDllVersionSource.Unknown);
            string json = Encoding.UTF8.GetString(ReportingWire.Bytes(inventory));
            Assert(ReportingWire.ValidDllInventory(inventory));
            Assert(!json.Contains(data) && !json.Contains("private-controller") && !json.Contains("Synthetic") && !json.Contains("secret") && !json.Contains(".dll"));
        });
        Check("identical DLLs group by fingerprint and role without counting an overlapping configured path twice", () =>
        {
            using var f = new Fixture(); var (data, plugins) = Folders(f);
            byte[] bytes = [7, 8, 9];
            foreach (string name in new[] { "first.dll", "second.dll", "VatEFS.dll" }) File.WriteAllBytes(Path.Combine(plugins, name), bytes);
            string staged = Path.Combine(plugins, "Updated Plugin", "nested"); Directory.CreateDirectory(staged);
            File.WriteAllBytes(Path.Combine(staged, "first.dll"), bytes);
            File.WriteAllBytes(Path.Combine(data, "EuroScope.exe"), [0]);
            var inventory = ReportingInventoryCollector.CollectDllInventory(data, data, plugins);
            Assert(inventory.Status == ReportingDllInventoryStatus.Complete && inventory.Entries.Count == 2);
            Assert(inventory.Entries.Single(item => item.Role == ReportingDllRole.Current).Copies == 3);
            Assert(inventory.Entries.Single(item => item.Role == ReportingDllRole.Staged).Copies == 1);
            Assert(inventory.Entries.All(item => item.Sha256 == Hash(bytes)));
        });
        Check("configured VatEFS inventory reads its exact DLL without walking backend dependencies", () =>
        {
            using var f = new Fixture(); string folder = Path.Combine(f.Root, "vatefs"); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "VatEFS.dll"), [1]);
            File.WriteAllBytes(Path.Combine(folder, "backend-dependency.dll"), [2]);
            var inventory = ReportingInventoryCollector.CollectDllInventory(null, null, folder);
            Assert(inventory.Status == ReportingDllInventoryStatus.Complete && inventory.Entries.Single().Sha256 == Hash([1]));
            File.Delete(Path.Combine(folder, "VatEFS.dll"));
            Assert(ReportingInventoryCollector.CollectDllInventory(null, null, folder) is { Status: ReportingDllInventoryStatus.Complete, Entries.Count: 0 });
            Assert(ReportingInventoryCollector.CollectDllInventory(null, null, Path.Combine(f.Root, "missing")) is
                { Status: ReportingDllInventoryStatus.Unavailable, Entries.Count: 0, UnreadableFiles: 0 });
            Assert(ReportingInventoryCollector.CollectDllInventory(null, null) is
                { Status: ReportingDllInventoryStatus.Unavailable, Entries.Count: 0 });
        });
        Check("physical DLL aliases count once per role while actual copied files count separately", () =>
        {
            using var f = new Fixture(); var (data, plugins) = Folders(f);
            string original = Path.Combine(plugins, "original.dll"); File.WriteAllBytes(original, [1, 4, 7]);
            File.Copy(original, Path.Combine(plugins, "actual-copy.dll"));
            string staged = Path.Combine(plugins, "Updated Plugins"); Directory.CreateDirectory(staged);
            foreach (string alias in new[] { Path.Combine(plugins, "alias.dll"), Path.Combine(staged, "staged-alias.dll") })
                if (!CreateHardLink(alias, original, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var inventory = ReportingInventoryCollector.CollectDllInventory(data, null);
            Assert(inventory.Status == ReportingDllInventoryStatus.Complete && inventory.Entries.Count == 2);
            Assert(inventory.Entries.Single(item => item.Role == ReportingDllRole.Current).Copies == 2);
            Assert(inventory.Entries.Single(item => item.Role == ReportingDllRole.Staged).Copies == 1);
        });
        Check("DLL read failures preserve successful results and linked locations are not followed", () =>
        {
            using var f = new Fixture(); var (data, plugins) = Folders(f);
            string inaccessible = Path.Combine(plugins, "locked.dll"); File.WriteAllBytes(inaccessible, [1]);
            File.WriteAllBytes(Path.Combine(plugins, "readable.dll"), [2]);
            using (var locked = new FileStream(inaccessible, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var inventory = ReportingInventoryCollector.CollectDllInventory(data, null);
                Assert(inventory is { Status: ReportingDllInventoryStatus.Partial, UnreadableFiles: 1 });
                Assert(inventory.Entries.Single().Sha256 == Hash([2]));
            }
            string outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside); File.WriteAllBytes(Path.Combine(outside, "private.dll"), [3]);
            string link = Path.Combine(plugins, "linked"); InventoryTests.CreateJunction(link, outside);
            try
            {
                var inventory = ReportingInventoryCollector.CollectDllInventory(data, null);
                Assert(inventory is { Status: ReportingDllInventoryStatus.Partial, UnreadableFiles: 0 });
                Assert(inventory.Entries.Count == 2 && inventory.Entries.All(item => item.Sha256 != Hash([3])));
            }
            finally { Directory.Delete(link); }
            string linkedRoot = Path.Combine(f.Root, "linked-root"); InventoryTests.CreateJunction(linkedRoot, data);
            try
            {
                Assert(ReportingInventoryCollector.CollectDllInventory(linkedRoot, null) is
                    { Status: ReportingDllInventoryStatus.Unavailable, Entries.Count: 0 });
            }
            finally { Directory.Delete(linkedRoot); }
        });
        Check("DLL scan budgets stop bounded work without fabricating unreadable-file counts", () =>
        {
            using var f = new Fixture(); var (data, plugins) = Folders(f);
            File.WriteAllBytes(Path.Combine(plugins, "first.dll"), [1, 1, 1]);
            File.WriteAllBytes(Path.Combine(plugins, "second.dll"), [2, 2, 2]);
            var limits = new ReportingInventoryCollector.DllScanLimits();
            foreach (var limit in new[] { limits with { Entries = 1 }, limits with { TotalBytes = 3 }, limits with { Groups = 1 }, limits with { JsonBytes = 256 } })
            {
                var inventory = ReportingInventoryCollector.CollectDllInventory(data, null, null, limit);
                Assert(inventory is { Status: ReportingDllInventoryStatus.Partial, UnreadableFiles: 0 });
                Assert(inventory.Entries.Count == 1 && ReportingWire.Bytes(inventory).Length <= limit.JsonBytes);
                Assert(ReportingWire.ValidDllInventory(inventory));
            }
            var oversized = ReportingInventoryCollector.CollectDllInventory(data, null, null, limits with { FileBytes = 2 });
            Assert(oversized is { Status: ReportingDllInventoryStatus.Partial, Entries.Count: 0, UnreadableFiles: 0 });
            string nested = Path.Combine(plugins, "nested"); Directory.CreateDirectory(nested); File.WriteAllBytes(Path.Combine(nested, "third.dll"), [3]);
            foreach (var limit in new[] { limits with { Depth = 0 }, limits with { Directories = 1 } })
            {
                var inventory = ReportingInventoryCollector.CollectDllInventory(data, null, null, limit);
                Assert(inventory is { Status: ReportingDllInventoryStatus.Partial, UnreadableFiles: 0 });
                Assert(inventory.Entries.All(item => item.Sha256 != Hash([3])));
            }
        });
        Check("production DLL payload cap bounds many distinct unknown files", () =>
        {
            using var f = new Fixture(); var (data, plugins) = Folders(f);
            for (int i = 0; i < 80; i++) File.WriteAllBytes(Path.Combine(plugins, $"unknown-{i}.dll"), BitConverter.GetBytes(i));
            var inventory = ReportingInventoryCollector.CollectDllInventory(data, null);
            Assert(inventory is { Status: ReportingDllInventoryStatus.Partial, UnreadableFiles: 0 });
            Assert(inventory.Entries.Count is > 0 and <= ReportingWire.MaximumDllEntries);
            Assert(ReportingWire.Bytes(inventory).Length <= ReportingWire.MaximumDllInventoryBytes && ReportingWire.ValidDllInventory(inventory));
        });
        return passed;

        void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    }

    private static (string Data, string Plugins) Folders(Fixture fixture)
    {
        string data = Path.Combine(fixture.Root, "data"), plugins = Path.Combine(data, "ESAA", "Plugins");
        Directory.CreateDirectory(plugins); return (data, plugins);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Assert(bool condition) { if (!condition) throw new Exception("Synthetic DLL inventory assertion failed."); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);
}
