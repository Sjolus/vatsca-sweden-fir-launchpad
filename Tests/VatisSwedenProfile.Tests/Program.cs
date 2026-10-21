using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

int passed = 0;
async Task Test(string name, Func<Task> run)
{
    await run();
    passed++;
    Console.WriteLine("PASS " + name);
}

await Test("Seeds latest official profile without launching clients or copying identity", async () =>
{
    using var f = new Fixture();
    var result = await f.Run();
    Assert(result.Status == VatisSwedenProfileStatus.Installed && result.Success);
    string installed = File.ReadAllText(f.ProfilePath(Fixture.RemoteId));
    Assert(installed == f.Body && !Directory.EnumerateFiles(f.Root, "AppConfig.json", SearchOption.AllDirectories).Any());
    Assert(f.RequestCount == 1 && f.RequestUri == VatisSwedenProfileService.ProfileUrl && f.ProcessProbes >= 3);
});

await Test("Reopening setup leaves current auto profile unchanged and skips download", async () =>
{
    using var f = new Fixture();
    Assert((await f.Run()).Success);
    string original = File.ReadAllText(f.ProfilePath(Fixture.RemoteId));
    f.Body = Fixture.Profile(serial: 2026102102);
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.AlreadyConfigured);
    Assert(f.RequestCount == 1 && File.ReadAllText(f.ProfilePath(Fixture.RemoteId)) == original);
});

await Test("Recognizes old bundled identity and main URL alias", async () =>
{
    using var f = new Fixture();
    string id = "8149f89b-2433-4d85-be9d-7a5f1c763019";
    string original = Fixture.Profile(id, url: VatisSwedenProfileService.ProfileUrl.Replace("refs/heads/main", "main"));
    f.Add(id, original);
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.AlreadyConfigured && f.RequestCount == 0);
    Assert(File.ReadAllText(f.ProfilePath(id)) == original && Directory.GetFiles(f.Profiles).Length == 1);
});

await Test("Recognizes user-imported local ID without creating duplicate", async () =>
{
    using var f = new Fixture();
    string id = Guid.NewGuid().ToString();
    f.Add(id, Fixture.Profile(id));
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.AlreadyConfigured && f.RequestCount == 0);
});

await Test("Recognizes an existing UTF-8 BOM profile without rewriting it", async () =>
{
    using var f = new Fixture();
    Directory.CreateDirectory(f.Profiles);
    byte[] bytes = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(Fixture.Profile())];
    File.WriteAllBytes(f.ProfilePath(Fixture.RemoteId), bytes);
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.AlreadyConfigured && f.RequestCount == 0);
    Assert(File.ReadAllBytes(f.ProfilePath(Fixture.RemoteId)).SequenceEqual(bytes));
});

await Test("Preserves unrelated custom profiles byte for byte", async () =>
{
    using var f = new Fixture();
    string id = Guid.NewGuid().ToString();
    string custom = Fixture.Profile(id, url: "");
    f.Add(id, custom);
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Installed);
    Assert(File.ReadAllText(f.ProfilePath(id)) == custom && Directory.GetFiles(f.Profiles, "*.json").Length == 2);
});

await Test("Custom copy under official ID is retained for review", async () =>
{
    using var f = new Fixture();
    string custom = Fixture.Profile(url: "");
    f.Add(Fixture.RemoteId, custom);
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Conflict && f.RequestCount == 0);
    Assert(File.ReadAllText(f.ProfilePath(Fixture.RemoteId)) == custom);
});

await Test("Two existing automatic profiles remain untouched", async () =>
{
    using var f = new Fixture();
    f.Add(Fixture.RemoteId, Fixture.Profile());
    string id = Guid.NewGuid().ToString();
    f.Add(id, Fixture.Profile(id));
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Conflict && f.RequestCount == 0);
    Assert(Directory.GetFiles(f.Profiles, "*.json").Length == 2);
});

await Test("Ambiguous stored filename and JSON identity are retained", async () =>
{
    using var f = new Fixture();
    f.Add(Guid.NewGuid().ToString(), Fixture.Profile());
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Conflict && f.RequestCount == 0);
});

await Test("Malformed existing profile is not overwritten", async () =>
{
    using var f = new Fixture();
    f.Add(Fixture.RemoteId, "not json");
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Conflict);
    Assert(File.ReadAllText(f.ProfilePath(Fixture.RemoteId)) == "not json");
});

await Test("No selected client performs no download or write", async () =>
{
    using var f = new Fixture();
    f.Settings.VatisExePath = "";
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.NotConfigured && f.RequestCount == 0 && !Directory.Exists(f.Profiles));
});

await Test("A client marked not used performs no download or write", async () =>
{
    using var f = new Fixture();
    ApplicationPreferences.SetNotUsed(f.Settings, "vATIS", true);
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.NotConfigured && f.RequestCount == 0 && !Directory.Exists(f.Profiles));
});

await Test("Requires caller's maintenance lease", async () =>
{
    using var f = new Fixture { HasLease = false };
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Blocked && f.RequestCount == 0 && !Directory.Exists(f.Profiles));
});

await Test("Invalid executable identity cannot seed a profile", async () =>
{
    using var f = new Fixture { Validate = _ => throw new InvalidDataException("private fixture path") };
    var result = await f.Run();
    Assert(!result.Success && !result.Message.Contains("private") && f.RequestCount == 0 && !Directory.Exists(f.Profiles));
});

await Test("Publisher verification errors return a safe result", async () =>
{
    using var f = new Fixture { Validate = _ => throw new System.Security.Cryptography.CryptographicException("private certificate details") };
    var result = await f.Run();
    Assert(!result.Success && !result.Message.Contains("private") && f.RequestCount == 0 && !Directory.Exists(f.Profiles));
});

await Test("Running vATIS prevents network and file actions", async () =>
{
    using var f = new Fixture { Running = _ => true };
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Blocked && f.RequestCount == 0);
});

await Test("vATIS starting during download prevents writes", async () =>
{
    using var f = new Fixture { Running = count => count >= 2 };
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Blocked && !Directory.Exists(f.Profiles));
});

await Test("vATIS starting just before rename removes incomplete temporary file", async () =>
{
    using var f = new Fixture { Running = count => count >= 3 };
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Blocked && Directory.GetFiles(f.Profiles).Length == 0);
});

await Test("Target created during download cannot be replaced", async () =>
{
    using var f = new Fixture();
    string custom = Fixture.Profile(url: "");
    f.OnRequest = () => f.Add(Fixture.RemoteId, custom);
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Conflict);
    Assert(File.ReadAllText(f.ProfilePath(Fixture.RemoteId)) == custom);
});

await Test("Target created at final process probe cannot be replaced", async () =>
{
    using var f = new Fixture();
    string custom = Fixture.Profile(url: "");
    f.Running = count => { if (count == 3) f.Add(Fixture.RemoteId, custom); return false; };
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Conflict);
    Assert(File.ReadAllText(f.ProfilePath(Fixture.RemoteId)) == custom && Directory.GetFiles(f.Profiles).Length == 1);
});

foreach (var invalid in new Dictionary<string, Func<string>>
{
    ["malformed JSON"] = () => "{",
    ["different schema"] = () => Fixture.Edit("version", JsonValue.Create(5)),
    ["missing schema"] = () => Fixture.Edit("version", null),
    ["empty stations"] = () => Fixture.Edit("stations", new JsonArray()),
    ["invalid ID"] = () => Fixture.Edit("id", JsonValue.Create("../other")),
    ["empty ID"] = () => Fixture.Edit("id", JsonValue.Create(Guid.Empty.ToString())),
    ["invalid serial"] = () => Fixture.Edit("updateSerial", JsonValue.Create(1)),
    ["empty name"] = () => Fixture.Edit("name", JsonValue.Create("")),
    ["foreign update host"] = () => Fixture.Edit("updateUrl", JsonValue.Create("https://example.invalid/profile.json")),
    ["URL credentials"] = () => Fixture.Edit("updateUrl", JsonValue.Create(VatisSwedenProfileService.ProfileUrl.Replace("https://", "https://secret@"))),
    ["URL query"] = () => Fixture.Edit("updateUrl", JsonValue.Create(VatisSwedenProfileService.ProfileUrl + "?token=secret")),
    ["personal settings in profile"] = () => Fixture.Edit("password", JsonValue.Create("fixture-secret")),
    ["duplicate properties"] = () => Fixture.Profile().Replace("\"name\":", "\"Name\":\"Other\",\"name\":")
})
    await Test("Rejects " + invalid.Key, async () =>
    {
        using var f = new Fixture { Body = invalid.Value() };
        var result = await f.Run();
        Assert(!result.Success && !Directory.Exists(f.Profiles) && !result.Message.Contains("secret"));
    });

await Test("Rejects HTTP redirect without a second request", async () =>
{
    using var f = new Fixture { Status = HttpStatusCode.Redirect };
    Assert(!(await f.Run()).Success && f.RequestCount == 1 && !Directory.Exists(f.Profiles));
});

await Test("Rejects a response returned under another host", async () =>
{
    using var f = new Fixture { ResponseUri = "https://example.invalid/profile.json" };
    Assert(!(await f.Run()).Success && !Directory.Exists(f.Profiles));
});

await Test("Rejects oversized declared download", async () =>
{
    using var f = new Fixture { DeclaredLength = VatisSwedenProfileService.MaximumProfileBytes + 1 };
    Assert(!(await f.Run()).Success && !Directory.Exists(f.Profiles));
});

await Test("Rejects oversized streaming download without content length", async () =>
{
    using var f = new Fixture { Body = new string(' ', VatisSwedenProfileService.MaximumProfileBytes + 1), Streaming = true };
    Assert(!(await f.Run()).Success && !Directory.Exists(f.Profiles));
});

await Test("Bounds inspected files before modifying directory", async () =>
{
    using var f = new Fixture();
    Directory.CreateDirectory(f.Profiles);
    for (int i = 0; i <= VatisSwedenProfileService.MaximumDirectoryEntries; i++) File.WriteAllText(Path.Combine(f.Profiles, i + ".txt"), "");
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Conflict && f.RequestCount == 0);
});

await Test("Bounds each existing profile", async () =>
{
    using var f = new Fixture();
    f.Add(Fixture.RemoteId, new string(' ', VatisSwedenProfileService.MaximumProfileBytes + 1));
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Conflict && f.RequestCount == 0);
});

await Test("Bounds aggregate existing profile inspection", async () =>
{
    using var f = new Fixture();
    for (int i = 0; i < 9; i++)
    {
        string id = Guid.NewGuid().ToString();
        f.Add(id, Fixture.Profile(id, url: "").PadRight(VatisSwedenProfileService.MaximumProfileBytes));
    }
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Conflict && f.RequestCount == 0);
});

await Test("Rejects linked profile directory", async () =>
{
    using var f = new Fixture();
    string target = Path.Combine(f.Root, "unrelated");
    Directory.CreateDirectory(target);
    Fixture.Junction(f.Profiles, target, f.Root);
    Assert(!(await f.Run()).Success && f.RequestCount == 0 && Directory.GetFiles(target).Length == 0);
});

await Test("Rejects linked child and leaves target unchanged", async () =>
{
    using var f = new Fixture();
    Directory.CreateDirectory(f.Profiles);
    string target = Path.Combine(f.Root, "unrelated");
    Directory.CreateDirectory(target);
    File.WriteAllText(Path.Combine(target, "sentinel.txt"), "retain");
    Fixture.Junction(Path.Combine(f.Profiles, "link"), target, f.Root);
    Assert((await f.Run()).Status == VatisSwedenProfileStatus.Conflict && f.RequestCount == 0);
    Assert(File.ReadAllText(Path.Combine(target, "sentinel.txt")) == "retain");
});

await Test("Rejects network data root", async () =>
{
    using var f = new Fixture { OverrideRoot = @"\\localhost\invalid-test-share" };
    Assert(!(await f.Run()).Success && f.RequestCount == 0);
});

await Test("Rejects a hard-linked existing profile without changing either link", async () =>
{
    using var f = new Fixture();
    Directory.CreateDirectory(f.Profiles);
    string original = Path.Combine(f.Root, "original.json");
    File.WriteAllText(original, Fixture.Profile());
    Fixture.HardLink(f.ProfilePath(Fixture.RemoteId), original, f.Root);
    Assert(!(await f.Run()).Success && f.RequestCount == 0);
    Assert(File.ReadAllText(original) == Fixture.Profile() && File.ReadAllText(f.ProfilePath(Fixture.RemoteId)) == Fixture.Profile());
});

await Test("Cancellation before setup does nothing", async () =>
{
    using var f = new Fixture();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try { await f.Run(cancellation.Token); throw new Exception("Cancellation was swallowed."); }
    catch (OperationCanceledException) { }
    Assert(f.RequestCount == 0 && !Directory.Exists(f.Profiles));
});

await Test("Cancellation before rename leaves no temporary or profile file", async () =>
{
    using var f = new Fixture();
    using var cancellation = new CancellationTokenSource();
    f.Running = count => { if (count == 3) cancellation.Cancel(); return false; };
    try { await f.Run(cancellation.Token); throw new Exception("Cancellation was swallowed."); }
    catch (OperationCanceledException) { }
    Assert(Directory.GetFiles(f.Profiles).Length == 0);
});

Console.WriteLine($"{passed} Swedish vATIS profile checks passed.");
if (args is ["--profile-fixture", var publicProfile])
{
    // Explicit public-source fixture validation; does not inspect any installed client or write configuration.
    var info = new FileInfo(publicProfile);
    Assert(info.Length <= VatisSwedenProfileService.MaximumProfileBytes);
    _ = VatisSwedenProfileService.ValidateProfile(File.ReadAllBytes(publicProfile));
    Console.WriteLine("PASS supplied public Swedish profile accepted by production validator");
}
static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }

sealed class Fixture : IDisposable
{
    internal const string RemoteId = "b69dd50d-3deb-4e3a-be55-2f7801ad9755";
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchpadVatisProfileTests", Guid.NewGuid().ToString("N"));
    internal string Profiles => Path.Combine(Root, "org.vatsim.vatis", "Profiles");
    internal string? OverrideRoot { get; set; }
    internal AppSettings Settings { get; } = new() { VatisExePath = "synthetic-vATIS.exe", VatsimCid = "synthetic-identity-not-to-copy" };
    internal string Body { get; set; } = Profile();
    internal int RequestCount, ProcessProbes;
    internal string? RequestUri, ResponseUri;
    internal long? DeclaredLength;
    internal bool HasLease = true, Streaming;
    internal HttpStatusCode Status = HttpStatusCode.OK;
    internal Action<string> Validate = _ => { };
    internal Action? OnRequest;
    internal Func<int, bool> Running = _ => false;

    internal Fixture() => Directory.CreateDirectory(Root);
    internal string ProfilePath(string id) => Path.Combine(Profiles, id + ".json");
    internal void Add(string id, string body) { Directory.CreateDirectory(Profiles); File.WriteAllText(ProfilePath(id), body); }
    internal async Task<VatisSwedenProfileResult> Run(CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            RequestCount++;
            RequestUri = request.RequestUri!.AbsoluteUri;
            OnRequest?.Invoke();
            HttpContent content = Streaming ? new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(Body))) : new StringContent(Body);
            if (DeclaredLength.HasValue) content.Headers.ContentLength = DeclaredLength;
            if (Streaming) content.Headers.ContentLength = null;
            return new HttpResponseMessage(Status)
            {
                RequestMessage = ResponseUri == null ? request : new HttpRequestMessage(HttpMethod.Get, ResponseUri),
                Content = content
            };
        }));
        var service = new VatisSwedenProfileService(new()
        {
            LocalApplicationData = OverrideRoot ?? Root,
            ValidateInstallation = path => Validate(path),
            IsRunning = () => Running(++ProcessProbes),
            HasMaintenanceLease = () => HasLease,
            HttpClient = http
        });
        return await service.EnsureAsync(Settings, cancellationToken);
    }

    internal static string Profile(string id = RemoteId, string? url = null, int serial = 2026040102) => JsonSerializer.Serialize(new
    {
        name = "ESAA synthetic profile", id, updateUrl = url ?? VatisSwedenProfileService.ProfileUrl, updateSerial = serial, version = 4,
        stations = new[] { new { id = "794b4b73-c585-44ae-8a0c-663cc636f44b", identifier = "ESSA", name = "Synthetic airport", atisFormat = new { }, presets = new[] { new { name = "Synthetic preset" } } } }
    });
    internal static string Edit(string key, JsonNode? value)
    {
        var root = JsonNode.Parse(Profile())!.AsObject();
        if (value == null) root.Remove(key); else root[key] = value;
        return root.ToJsonString();
    }

    public void Dispose()
    {
        string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LaunchpadVatisProfileTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe test cleanup.");
        if (Directory.Exists(Root)) DeleteDirectory(Root);
    }
    private static void DeleteDirectory(string directory)
    {
        foreach (string child in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.Directory) == 0) File.Delete(child);
            else if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(child);
            else DeleteDirectory(child);
        }
        Directory.Delete(directory);
    }

    internal static void Junction(string link, string target, string root)
    {
        string allowed = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (new[] { link, target }.Any(p => !Path.GetFullPath(p).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Unsafe test junction.");
        Directory.CreateDirectory(link);
        using var handle = CreateFileW(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Cannot create fixture junction.");
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target), print = Encoding.Unicode.GetBytes(target);
        var bytes = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(bytes, 0);
        BitConverter.GetBytes(checked((ushort)(bytes.Length - 8))).CopyTo(bytes, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(bytes, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(bytes, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(bytes, 14);
        substitute.CopyTo(bytes, 16);
        print.CopyTo(bytes, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x900A4, bytes, bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Cannot set fixture junction.");
    }
    internal static void HardLink(string link, string target, string root)
    {
        string allowed = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (new[] { link, target }.Any(p => !Path.GetFullPath(p).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Unsafe test hard link.");
        if (!CreateHardLinkW(link, target, IntPtr.Zero)) throw new IOException("Cannot create fixture hard link.");
    }
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] bytes, int size, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
