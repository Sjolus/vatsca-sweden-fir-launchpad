using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using VatscaUpdateChecker.Services;

static class EuroScopeProcessGuardTests
{
    public static void Run(Action<string, Action<Fixture>> test)
    {
        test("EuroScope guard allows a confirmed stopped process without configured paths", _ =>
        {
            var names = new List<string>();
            bool Probe(string name) { names.Add(name); return false; }
            Require(EuroScopeProcessGuard.GetBlockingReason(Probe) is null, "Stopped process was blocked");
            EuroScopeProcessGuard.RequireClosed(Probe);
            Require(names.SequenceEqual(new[] { "EuroScope", "EuroScope" }), "Guard did not check the global EuroScope process name");
        });
        test("EuroScope guard blocks any running instance without needing window or executable path", _ =>
        {
            var reason = EuroScopeProcessGuard.GetBlockingReason(name => name == "EuroScope");
            Require(reason is not null && reason.Contains("all EuroScope instances", StringComparison.Ordinal), "Running instance was not explained");
            Reject(() => EuroScopeProcessGuard.RequireClosed(_ => true));
        });
        test("EuroScope guard rechecks process state rather than caching the stopped result", _ =>
        {
            bool running = false;
            bool Probe(string _) => running;
            EuroScopeProcessGuard.RequireClosed(Probe);
            running = true;
            Reject(() => EuroScopeProcessGuard.RequireClosed(Probe));
            running = false;
            EuroScopeProcessGuard.RequireClosed(Probe);
        });
        foreach (var failure in new Exception[]
        {
            new Win32Exception(5, "synthetic process enumeration failure"),
            new InvalidOperationException("synthetic unavailable process list"),
            new UnauthorizedAccessException("synthetic enumeration denied"),
            new SecurityException("synthetic process permission denied")
        })
        {
            test("EuroScope guard fails closed for " + failure.GetType().Name, _ =>
            {
                bool Probe(string _) => throw failure;
                var reason = EuroScopeProcessGuard.GetBlockingReason(Probe);
                Require(reason is not null && reason.Contains("could not check", StringComparison.Ordinal), "Enumeration failure implied EuroScope was stopped");
                Require(!reason!.Contains(failure.Message, StringComparison.Ordinal), "Internal process error leaked into the blocking message");
                Reject(() => EuroScopeProcessGuard.RequireClosed(Probe));
            });
        }
        test("EuroScope starting during GNG installation blocks writes and rollback until closed", f =>
        {
            var plan = f.Plan();
            var first = plan.Files.First(file => file.WritesFile);
            Require(plan.Files.Count(file => file.WritesFile) > 1, "Fixture needs multiple planned writes");
            var before = f.Snapshot();
            bool running = false;
            bool started = false;
            Dictionary<string, byte[]>? whenStarted = null;
            f.RequireClosed = () =>
            {
                var path = f.PathFor(first.RelativePath);
                if (!started && File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == first.AfterHash)
                {
                    started = running = true;
                    whenStarted = f.Snapshot();
                }
                EuroScopeProcessGuard.RequireClosed(_ => running);
            };
            Reject(() => f.Install(plan));
            Require(started && whenStarted is not null, "Fixture never simulated EuroScope starting after the first installed file");
            AssertSnapshot(whenStarted!, f.Snapshot());
            var pending = GngUpdateService.GetPendingUpdate(f.Root, f.Context);
            Require(pending is not null, "Interrupted install lost its recovery marker");
            Reject(() => f.Restore(pending!));
            AssertSnapshot(whenStarted!, f.Snapshot());
            running = false;
            var restored = f.Restore(pending!);
            Require(restored.SkippedFiles.Count == 0, "Restore failed after EuroScope closed");
            AssertSnapshot(before, f.Snapshot());
            Require(GngUpdateService.GetPendingUpdate(f.Root, f.Context) is null, "Successful restore retained the pending marker");
        });
    }

    private static void AssertSnapshot(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Require(expected.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(actual.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase), "File set changed while EuroScope was running");
        foreach (var path in expected.Keys) Require(expected[path].SequenceEqual(actual[path]), "File changed while EuroScope was running: " + path);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { return; }
        throw new Exception("Expected EuroScope guard to reject the operation");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
