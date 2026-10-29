using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

int passed = 0;
void Check(string name, Action check)
{
    try { check(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + " (" + error.GetType().Name + ")"); throw; }
}
void Require(bool condition) { if (!condition) throw new InvalidOperationException("Synthetic assertion failed."); }
void Reject(Action action)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Expected a rejected synthetic configuration.");
}
static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
static JsonObject Json(byte[] value) => JsonNode.Parse(value)!.AsObject();
static JsonObject Inner(byte[] value) => JsonNode.Parse(Json(value)["configuration"]!.GetValue<string>())!.AsObject();
static AppSettings Identity() => new() { VatsimName = "Synthetic Övning", VatsimCid = "1234567", VatsimRating = 2 };
const string Password = "Synthetic-Passwörd!42";

Check("vATIS fresh identity and vendor password encoding", () =>
{
    var patch = ClientSignInService.PatchVatis(null, Identity(), Password);
    var json = Json(patch.Content);
    Require(patch.Changed && json["name"]!.GetValue<string>() == "Synthetic Övning" && json["userId"]!.GetValue<string>() == "1234567");
    Require(json["networkRating"]!.GetValue<string>() == "S2");
    // Golden fixture independently generated with Node/OpenSSL des-ede, the upstream GUID and UTF-8.
    Require(json["password"]!.GetValue<string>() == "OiK+mMeJH5bVxqdHjsc1i5CJ9nXXsSyx");
    Require(!Encoding.UTF8.GetString(patch.Content).Contains(Password));
});
Check("all Launchpad ratings map to the vendor enum name", () =>
{
    string[] expected = ["Obs", "S1", "S2", "S3", "C1", "C2", "C3", "I1", "I2", "I3", "Sup", "Adm"];
    for (int index = 0; index < expected.Length; index++) Require(ClientSignInService.VatisRating(index) == expected[index]);
    Reject(() => ClientSignInService.VatisRating(12));
});
Check("vATIS preserves unknown preferences and field spelling", () =>
{
    var existing = Bytes("""{"Name":"old","USERID":"7654321","Password":"old-encoded","NetworkRating":1,"microphoneDevice":"device-17","muteOwnAtisUpdateSound":true,"future":{"items":[1,2,3]},"mainWindowPosition":{"x":40,"y":50}}""");
    var patch = ClientSignInService.PatchVatis(existing, Identity(), Password);
    var result = Json(patch.Content);
    Require(result["Name"]!.GetValue<string>() == "Synthetic Övning" && !result.ContainsKey("name"));
    Require(result["USERID"]!.GetValue<string>() == "1234567" && !result.ContainsKey("userId"));
    Require(JsonNode.DeepEquals(result["future"], Json(existing)["future"]));
    Require(JsonNode.DeepEquals(result["mainWindowPosition"], Json(existing)["mainWindowPosition"]));
    Require(result["microphoneDevice"]!.GetValue<string>() == "device-17" && result["muteOwnAtisUpdateSound"]!.GetValue<bool>());
});
Check("vATIS second apply retains identical bytes", () =>
{
    var first = ClientSignInService.PatchVatis(null, Identity(), Password);
    var second = ClientSignInService.PatchVatis(first.Content, Identity(), Password);
    Require(!second.Changed && first.Content.SequenceEqual(second.Content));
});
Check("empty saved fields do not clear vATIS values", () =>
{
    var first = ClientSignInService.PatchVatis(null, Identity(), Password);
    var empty = ClientSignInService.PatchVatis(first.Content, new AppSettings(), "");
    Require(!empty.Changed && empty.Content.SequenceEqual(first.Content));
    var nameOnly = ClientSignInService.PatchVatis(first.Content, new() { VatsimName = "Another synthetic name" }, " ");
    Require(Json(nameOnly.Content)["password"]!.GetValue<string>() == Json(first.Content)["password"]!.GetValue<string>());
    Require(Json(nameOnly.Content)["userId"]!.GetValue<string>() == "1234567");
});
Check("vATIS supported trailing commas and BOM", () =>
{
    byte[] existing = [0xef, 0xbb, 0xbf, .. Bytes("{\"name\":\"old\",}")];
    Require(ClientSignInService.PatchVatis(existing, Identity(), Password).Changed);
});
Check("vATIS rejects malformed ambiguous and wrong-shaped settings", () =>
{
    foreach (string input in new[] { "[]", "null", "{", "{\"name\":1}", "{\"networkRating\":\"Unknown\"}", "{\"networkRating\":0}", "{\"name\":\"a\",\"Name\":\"b\"}" })
        Reject(() => ClientSignInService.PatchVatis(Bytes(input), Identity(), Password));
    Reject(() => ClientSignInService.PatchVatis(new byte[ClientSignInService.MaximumConfigurationBytes + 1], Identity(), Password));
});
Check("TrackAudio fresh configuration is a schema4 JSON string", () =>
{
    var patch = ClientSignInService.PatchTrackAudio(null, Identity(), Password);
    var inner = Inner(patch.Content);
    Require(patch.Changed && inner["version"]!.GetValue<int>() == 4 && inner["cid"]!.GetValue<string>() == "1234567");
    Require(inner["password"]!.GetValue<string>() == Password);
    Require(!inner.ContainsKey("name") && !inner.ContainsKey("networkRating") && !inner.ContainsKey("callsign") && !inner.ContainsKey("audioApi"));
});
foreach (int schema in new[] { 2, 3, 4 })
Check($"TrackAudio schema{schema} preserves its version, audio, callsign, unknown and window settings", () =>
{
    var inner = new JsonObject { ["version"] = schema, ["cid"] = "7654321", ["password"] = "fake-old", ["audioApi"] = 4,
        ["callsign"] = "SYNTH_TEST", ["audioInputDeviceId"] = "microphone", ["speakerOutputDeviceId"] = "speakers", ["headsetOutputDeviceId"] = "headphones",
        ["mainRadioVolume"] = 73, ["future"] = new JsonObject { ["keep"] = true } };
    var outer = new JsonObject { ["configuration"] = inner.ToJsonString(), ["bounds"] = new JsonObject { ["x"] = 40 }, ["futureRoot"] = "keep" };
    var patch = ClientSignInService.PatchTrackAudio(Bytes(outer.ToJsonString()), Identity(), Password);
    var result = Inner(patch.Content);
    Require(patch.Changed && result["cid"]!.GetValue<string>() == "1234567" && result["password"]!.GetValue<string>() == Password);
    foreach (var field in inner.Where(entry => entry.Key is not ("cid" or "password"))) Require(JsonNode.DeepEquals(field.Value, result[field.Key]));
    Require(JsonNode.DeepEquals(outer["bounds"], Json(patch.Content)["bounds"]) && Json(patch.Content)["futureRoot"]!.GetValue<string>() == "keep");
    var again = ClientSignInService.PatchTrackAudio(patch.Content, Identity(), Password);
    Require(!again.Changed && again.Content.SequenceEqual(patch.Content));
});
Check("empty saved values preserve TrackAudio credentials", () =>
{
    var first = ClientSignInService.PatchTrackAudio(null, Identity(), Password);
    var again = ClientSignInService.PatchTrackAudio(first.Content, new AppSettings(), "");
    Require(!again.Changed && again.Content.SequenceEqual(first.Content));
});
Check("TrackAudio rejects unknown schema and invalid settings", () =>
{
    foreach (string inner in new[] { "{}", "{\"version\":1}", "{\"version\":5}", "{\"version\":\"2\"}", "{\"version\":2.5}", "{\"version\":null}", "{\"version\":4,\"cid\":123}", "{\"version\":4,\"version\":4}", "null", "{\"version\":4,}" })
    {
        var outer = JsonSerializer.SerializeToUtf8Bytes(new { configuration = inner });
        Reject(() => ClientSignInService.PatchTrackAudio(outer, Identity(), Password));
    }
    foreach (string outer in new[] { "{}", "{\"configuration\":{\"version\":4}}", "[]", "" })
        Reject(() => ClientSignInService.PatchTrackAudio(Bytes(outer), Identity(), Password));
});
Check("invalid saved identity is rejected without echoing it", () =>
{
    var settings = Identity(); settings.VatsimCid = "private-invalid-value";
    try { ClientSignInService.PatchTrackAudio(null, settings, Password); throw new InvalidOperationException(); }
    catch (InvalidDataException error) { Require(!error.Message.Contains(settings.VatsimCid) && !error.Message.Contains(Password)); }
});

Check("both configured clients receive only their own settings files", () =>
{
    using var f = new Fixture();
    string ptt = Path.Combine(f.Local, "trackaudio", "settings.ini"); Directory.CreateDirectory(Path.GetDirectoryName(ptt)!); File.WriteAllText(ptt, "synthetic PTT settings");
    var results = ClientSignInService.Apply(f.Settings, Password, null, f.Options());
    Require(results.Count == 2 && results.All(result => result.Status == ClientSignInStatus.Updated));
    Require(File.Exists(f.VatisConfig) && File.Exists(f.TrackConfig) && File.ReadAllText(ptt) == "synthetic PTT settings");
    Require(!Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories).Any());
    Require(ClientSignInService.Apply(f.Settings, Password, null, f.Options()).All(result => result.Status == ClientSignInStatus.Unchanged));
});
Check("onlyApp limits side effects", () =>
{
    using var f = new Fixture();
    var results = ClientSignInService.Apply(f.Settings, Password, SoftwareApp.TrackAudio, f.Options());
    Require(results.Count == 1 && results[0].Changed && File.Exists(f.TrackConfig) && !File.Exists(f.VatisConfig));
    Require(ClientSignInService.Apply(f.Settings, Password, SoftwareApp.Vacs, f.Options()).Count == 0);
});
Check("existing client configuration is replaced atomically without extra backups", () =>
{
    using var f = new Fixture();
    var previousIdentity = Identity(); previousIdentity.VatsimCid = "7654321";
    Directory.CreateDirectory(Path.GetDirectoryName(f.TrackConfig)!);
    File.WriteAllBytes(f.TrackConfig, ClientSignInService.PatchTrackAudio(null, previousIdentity, "synthetic-old-password").Content);
    var result = ClientSignInService.Apply(f.Settings, Password, SoftwareApp.TrackAudio, f.Options()).Single();
    Require(result.Changed && Inner(File.ReadAllBytes(f.TrackConfig))["cid"]!.GetValue<string>() == "1234567");
    Require(Directory.EnumerateFiles(Path.GetDirectoryName(f.TrackConfig)!).Count() == 1);
});
Check("TrackAudio 1.4 executable accepts retained schema2 through the guarded adapter", () =>
{
    using var f = new Fixture();
    Directory.CreateDirectory(Path.GetDirectoryName(f.TrackConfig)!);
    var configuration = new JsonObject { ["version"] = 2, ["cid"] = "7654321", ["password"] = "synthetic-old",
        ["audioInputDeviceId"] = "synthetic-microphone", ["callsign"] = "SYNTH_TEST", ["future"] = new JsonArray(1, 2, 3) };
    var outer = new JsonObject { ["configuration"] = configuration.ToJsonString(), ["bounds"] = new JsonObject { ["x"] = 25 } };
    File.WriteAllText(f.TrackConfig, outer.ToJsonString());
    var options = f.Options(binary: _ => new ConfiguredBinaryMetadata(false, "TrackAudio", ProductVersion: "1.4.0.0"));
    Require(ClientSignInService.Apply(f.Settings, Password, SoftwareApp.TrackAudio, options).Single().Status == ClientSignInStatus.Updated);
    byte[] saved = File.ReadAllBytes(f.TrackConfig);
    var result = Inner(saved);
    Require(result["version"]!.GetValue<int>() == 2 && result["cid"]!.GetValue<string>() == "1234567" && result["password"]!.GetValue<string>() == Password);
    foreach (var field in configuration.Where(entry => entry.Key is not ("cid" or "password"))) Require(JsonNode.DeepEquals(field.Value, result[field.Key]));
    Require(JsonNode.DeepEquals(outer["bounds"], Json(saved)["bounds"]));
    Require(ClientSignInService.Apply(f.Settings, Password, SoftwareApp.TrackAudio, options).Single().Status == ClientSignInStatus.Unchanged);
    Require(saved.SequenceEqual(File.ReadAllBytes(f.TrackConfig)) && Directory.EnumerateFiles(Path.GetDirectoryName(f.TrackConfig)!).Count() == 1);
});
Check("unused and missing clients are not probed", () =>
{
    using var f = new Fixture();
    f.Settings.NotUsedApplications = "TrackAudio";
    f.Settings.VatisExePath = "";
    var options = f.Options(running: _ => throw new InvalidOperationException("Must not probe"));
    var results = ClientSignInService.Apply(f.Settings, Password, null, options);
    Require(results[0].Status == ClientSignInStatus.NotConfigured && results[1].Status == ClientSignInStatus.NotUsed);
});
Check("no CID and no maintenance lease prevent writes", () =>
{
    using var f = new Fixture();
    Require(ClientSignInService.Apply(f.Settings, Password, null, f.Options(lease: false)).All(r => r.Status == ClientSignInStatus.Failed));
    f.Settings.VatsimCid = "";
    Require(ClientSignInService.Apply(f.Settings, Password, null, f.Options()).All(r => r.Status == ClientSignInStatus.NotConfigured));
    Require(!File.Exists(f.VatisConfig) && !File.Exists(f.TrackConfig));
});
Check("running clients remain untouched", () =>
{
    using var f = new Fixture();
    var results = ClientSignInService.Apply(f.Settings, Password, null, f.Options(running: _ => true));
    Require(results.All(result => result.Status == ClientSignInStatus.Running) && !File.Exists(f.TrackConfig) && !File.Exists(f.VatisConfig));
});
Check("a client starting during preparation blocks the atomic write", () =>
{
    using var f = new Fixture(); int probes = 0;
    var result = ClientSignInService.Apply(f.Settings, Password, SoftwareApp.TrackAudio, f.Options(running: _ => ++probes >= 3)).Single();
    Require(result.Status == ClientSignInStatus.Running && !File.Exists(f.TrackConfig));
    Require(!Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories).Any());
});
Check("wrong product version and binary kinds are unsupported", () =>
{
    using var f = new Fixture();
    foreach (var binary in new[] { new ConfiguredBinaryMetadata(false, "Other", ProductVersion: "1.4.0"),
        new ConfiguredBinaryMetadata(false, "TrackAudio", ProductVersion: "1.4.1"),
        new ConfiguredBinaryMetadata(true, "TrackAudio", ProductVersion: "1.4.0"),
        new ConfiguredBinaryMetadata(false, "TrackAudio", ProductVersion: "1.4.0", IsInstaller: true) })
        Require(ClientSignInService.Apply(f.Settings, Password, SoftwareApp.TrackAudio, f.Options(binary: _ => binary)).Single().Status == ClientSignInStatus.Unsupported);
    Require(!File.Exists(f.TrackConfig));
});
Check("vATIS signature failure does not write credentials", () =>
{
    using var f = new Fixture();
    var result = ClientSignInService.Apply(f.Settings, Password, SoftwareApp.Vatis,
        f.Options(publisher: _ => throw new InvalidDataException("synthetic untrusted publisher"))).Single();
    Require(result.Status == ClientSignInStatus.Unsupported && !File.Exists(f.VatisConfig));
});
Check("vATIS root launcher resolves the supported current executable", () =>
{
    using var f = new Fixture();
    f.Settings.VatisExePath = Path.Combine(f.Local, "org.vatsim.vatis", "vATIS.exe");
    Require(ClientSignInService.Apply(f.Settings, Password, SoftwareApp.Vatis, f.Options()).Single().Changed);
});
foreach (string version in new[] { "4.1.0-beta.19", "4.1.0-beta.20", "4.1.0-beta.21", "4.1.0-beta.22", "4.1.0-beta.23",
    "4.1.0-beta.100", "4.1.0-rc.1", "4.1.0", "4.1.1", "4.1.1-beta.1" })
Check($"vATIS {version} updates identity while preserving preferences and repeated apply", () =>
{
    using var f = new Fixture();
    byte[] original = Bytes("""{"name":"Old synthetic name","userId":"7654321","password":"old-encoded","networkRating":"Obs","miniWindowUseMagneticWind":true,"autoFetchDatis":false,"microphoneDevice":"synthetic-device","future":{"keep":[1,2,3]}}""");
    File.WriteAllBytes(f.VatisConfig, original);
    int publisherChecks = 0;
    var options = f.Options(binary: _ => new(false, "vATIS", ProductVersion: version + "+synthetic"),
        publisher: path => { Require(path == f.Settings.VatisExePath); publisherChecks++; });
    var outcome = ClientSignInService.Apply(f.Settings, Password, SoftwareApp.Vatis, options).Single();
    Require(outcome.Status == ClientSignInStatus.Updated && publisherChecks > 0);
    byte[] saved = File.ReadAllBytes(f.VatisConfig);
    var result = Json(saved);
    Require(result["name"]!.GetValue<string>() == "Synthetic Övning" && result["userId"]!.GetValue<string>() == "1234567");
    Require(result["networkRating"]!.GetValue<string>() == "S2" && result["password"]!.GetValue<string>() == "OiK+mMeJH5bVxqdHjsc1i5CJ9nXXsSyx");
    foreach (var field in Json(original).Where(entry => entry.Key is not ("name" or "userId" or "password" or "networkRating")))
        Require(JsonNode.DeepEquals(field.Value, result[field.Key]));
    Require(ClientSignInService.Apply(f.Settings, Password, SoftwareApp.Vatis, options).Single().Status == ClientSignInStatus.Unchanged);
    Require(File.ReadAllBytes(f.VatisConfig).SequenceEqual(saved));
});
foreach (string version in new[] { "4.1.0-beta.18", "4.0.9", "4.2.0-beta.1", "4.2.0-0", "5.0.0",
    "4.1.1-alpha.1", "4.1.1-dev.1", "4.1.1-custom.1", "4.1.0-beta.preview", "4.1.0-rc",
    "4.1.0-beta.23.preview", "4.1.0-beta.023", "4.1.0-beta.23+", "4.1.0-beta.23+build+extra", "04.1.0-beta.23" })
Check($"unsupported vATIS family or version {version} is blocked before reading settings", () =>
{
    using var f = new Fixture();
    byte[] original = Bytes("{\"synthetic-preserve\":true}");
    File.WriteAllBytes(f.VatisConfig, original);
    int reads = 0;
    var options = f.Options(binary: _ => new(false, "vATIS", ProductVersion: version),
        read: (path, bound) => { reads++; return ClientConfigurationFiles.Read(path, bound); });
    var outcome = ClientSignInService.Apply(f.Settings, Password, SoftwareApp.Vatis, options).Single();
    Require(outcome.Status == ClientSignInStatus.Unsupported && outcome.Message.Contains("Launchpad compatibility update"));
    Require(reads == 0 && File.ReadAllBytes(f.VatisConfig).SequenceEqual(original));
});
Check("future vATIS beta23 still rejects wrong identity and untrusted publisher before reading settings", () =>
{
    using var f = new Fixture();
    int reads = 0;
    byte[]? Read(string path, int bound) { reads++; return ClientConfigurationFiles.Read(path, bound); }
    foreach (var binary in new[] { new ConfiguredBinaryMetadata(false, "Other", ProductVersion: "4.1.0-beta.23"),
        new ConfiguredBinaryMetadata(true, "vATIS", ProductVersion: "4.1.0-beta.23"),
        new ConfiguredBinaryMetadata(false, "vATIS", ProductVersion: "4.1.0-beta.23", IsInstaller: true) })
        Require(ClientSignInService.Apply(f.Settings, Password, SoftwareApp.Vatis, f.Options(binary: _ => binary, read: Read)).Single().Status == ClientSignInStatus.Unsupported);
    var outcome = ClientSignInService.Apply(f.Settings, Password, SoftwareApp.Vatis,
        f.Options(binary: _ => new(false, "vATIS", ProductVersion: "4.1.0-beta.23"), read: Read,
            publisher: _ => throw new InvalidDataException("synthetic untrusted publisher"))).Single();
    Require(outcome.Status == ClientSignInStatus.Unsupported && reads == 0 && !File.Exists(f.VatisConfig));
});
foreach (int runningAtProbe in new[] { 1, 3 })
Check($"future vATIS beta23 starting at probe {runningAtProbe} preserves its original settings", () =>
{
    using var f = new Fixture();
    byte[] original = Bytes("{\"name\":\"Old synthetic name\",\"autoFetchDatis\":true}");
    File.WriteAllBytes(f.VatisConfig, original);
    int probes = 0;
    var options = f.Options(binary: _ => new(false, "vATIS", ProductVersion: "4.1.0-beta.23"), running: _ => ++probes >= runningAtProbe);
    Require(ClientSignInService.Apply(f.Settings, Password, SoftwareApp.Vatis, options).Single().Status == ClientSignInStatus.Running);
    Require(File.ReadAllBytes(f.VatisConfig).SequenceEqual(original) && !Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories).Any());
});
Check("future vATIS beta23 still rejects malformed or incompatible settings without changing them", () =>
{
    using var f = new Fixture();
    foreach (string contents in new[] { "{", "{\"name\":{\"value\":\"synthetic\"}}", "{\"password\":42}", "{\"networkRating\":\"NewRating\"}" })
    {
        byte[] original = Bytes(contents);
        File.WriteAllBytes(f.VatisConfig, original);
        var options = f.Options(binary: _ => new(false, "vATIS", ProductVersion: "4.1.0-beta.23"));
        var outcome = ClientSignInService.Apply(f.Settings, Password, SoftwareApp.Vatis, options).Single();
        Require(outcome.Status == ClientSignInStatus.Unsupported && !outcome.Message.Contains("Launchpad compatibility update"));
        Require(File.ReadAllBytes(f.VatisConfig).SequenceEqual(original) && !Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories).Any());
    }
});
Check("vATIS changing to an unsupported family before commit preserves original settings and clears the temporary file", () =>
{
    using var f = new Fixture();
    byte[] original = Bytes("{\"name\":\"Old synthetic name\",\"autoFetchDatis\":true}");
    File.WriteAllBytes(f.VatisConfig, original);
    int binaryProbes = 0;
    var options = f.Options(binary: _ => new(false, "vATIS", ProductVersion: ++binaryProbes >= 3 ? "4.2.0-beta.1" : "4.1.0-beta.23"));
    var outcome = ClientSignInService.Apply(f.Settings, Password, SoftwareApp.Vatis, options).Single();
    Require(binaryProbes == 3 && outcome.Status == ClientSignInStatus.Unsupported && outcome.Message.Contains("Launchpad compatibility update"));
    Require(File.ReadAllBytes(f.VatisConfig).SequenceEqual(original) && !Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories).Any());
});
Check("unexpected vATIS root and nonlocal paths are rejected", () =>
{
    using var f = new Fixture();
    foreach (var path in new[] { "vATIS.exe", @"\\server\folder\vATIS.exe", @"\\?\C:\bad\vATIS.exe", Path.Combine(f.Root, "vATIS.exe"), f.Settings.VatisExePath + ":stream" })
    {
        f.Settings.VatisExePath = path;
        Require(ClientSignInService.Apply(f.Settings, Password, SoftwareApp.Vatis, f.Options()).Single().Status == ClientSignInStatus.Unsupported);
    }
});
Check("malformed existing file is preserved with fixed safe message", () =>
{
    using var f = new Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.TrackConfig)!);
    string raw = "private malformed marker " + Password; File.WriteAllText(f.TrackConfig, raw);
    var result = ClientSignInService.Apply(f.Settings, Password, SoftwareApp.TrackAudio, f.Options()).Single();
    Require(result.Status == ClientSignInStatus.Unsupported && File.ReadAllText(f.TrackConfig) == raw && !result.Message.Contains(Password));
});
Check("read exceptions do not reveal contents or prevent the other client", () =>
{
    using var f = new Fixture();
    var result = ClientSignInService.Apply(f.Settings, Password, null,
        f.Options(read: (path, bound) => path == f.VatisConfig ? throw new IOException(Password) : ClientConfigurationFiles.Read(path, bound)));
    Require(result[0].Status == ClientSignInStatus.Failed && !result[0].Message.Contains(Password) && result[1].Changed);
});
Check("failed final guard preserves the original and removes prepared secrets", () =>
{
    using var f = new Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.TrackConfig)!);
    byte[] original = ClientSignInService.PatchTrackAudio(null, Identity(), "synthetic-previous").Content;
    File.WriteAllBytes(f.TrackConfig, original); int guards = 0;
    try
    {
        ClientConfigurationFiles.Write(f.TrackConfig, original, ClientSignInService.PatchTrackAudio(original, Identity(), Password).Content,
            () => { if (++guards == 2) throw new IOException("synthetic failed guard"); });
        throw new InvalidOperationException("Expected a final guard failure.");
    }
    catch (IOException) { Require(File.ReadAllBytes(f.TrackConfig).SequenceEqual(original)); }
    Require(guards == 2 && Directory.EnumerateFiles(Path.GetDirectoryName(f.TrackConfig)!).Count() == 1);
});
Check("an initial failed guard creates no temporary configuration", () =>
{
    using var f = new Fixture();
    try
    {
        ClientConfigurationFiles.Write(f.TrackConfig, null, Bytes("{}"), () => throw new IOException("synthetic failed guard"));
        throw new InvalidOperationException("Expected an initial guard failure.");
    }
    catch (IOException) { Require(!File.Exists(f.TrackConfig)); }
    Require(!Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories).Any());
});
Check("process inspection exceptions are sanitized", () =>
{
    using var f = new Fixture();
    var result = ClientSignInService.Apply(f.Settings, Password, SoftwareApp.TrackAudio,
        f.Options(running: _ => throw new System.ComponentModel.Win32Exception(Password))).Single();
    Require(result.Status == ClientSignInStatus.Failed && !result.Message.Contains(Password) && !File.Exists(f.TrackConfig));
});
Check("concurrent configuration changes are preserved", () =>
{
    using var f = new Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.TrackConfig)!);
    byte[] original = ClientSignInService.PatchTrackAudio(null, Identity(), Password).Content;
    File.WriteAllBytes(f.TrackConfig, original); byte[] concurrent = Bytes("{\"external\":true}");
    try
    {
        ClientConfigurationFiles.Write(f.TrackConfig, original, Bytes("{}"), () => File.WriteAllBytes(f.TrackConfig, concurrent));
        throw new InvalidOperationException("Expected concurrent edit refusal.");
    }
    catch (IOException) { Require(File.ReadAllBytes(f.TrackConfig).SequenceEqual(concurrent)); }
    Require(!Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories).Any());
});
Check("oversized configuration is never read in full or overwritten", () =>
{
    using var f = new Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.TrackConfig)!);
    using (var file = File.Create(f.TrackConfig)) file.SetLength(ClientSignInService.MaximumConfigurationBytes + 1L);
    Require(ClientSignInService.Apply(f.Settings, Password, SoftwareApp.TrackAudio, f.Options()).Single().Status == ClientSignInStatus.Unsupported);
    Require(new FileInfo(f.TrackConfig).Length == ClientSignInService.MaximumConfigurationBytes + 1L);
});
Check("hardlinked configuration is refused without touching its other name", () =>
{
    using var f = new Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.TrackConfig)!);
    string original = Path.Combine(f.Root, "original.json"); File.WriteAllText(original, "{}");
    if (!Native.CreateHardLink(f.TrackConfig, original, IntPtr.Zero)) throw new IOException("Could not create synthetic hardlink.");
    Require(ClientSignInService.Apply(f.Settings, Password, SoftwareApp.TrackAudio, f.Options()).Single().Status == ClientSignInStatus.Unsupported);
    Require(File.ReadAllText(original) == "{}");
});
Check("linked data directory is refused", () =>
{
    using var f = new Fixture(); string target = Path.Combine(f.Root, "unrelated"); Directory.CreateDirectory(target);
    SyntheticJunction.Create(Path.Combine(f.Roaming, "trackaudio"), target);
    var result = ClientSignInService.Apply(f.Settings, Password, SoftwareApp.TrackAudio, f.Options()).Single();
    Require(result.Status is ClientSignInStatus.Unsupported or ClientSignInStatus.Failed);
    Require(!Directory.EnumerateFileSystemEntries(target).Any());
    Directory.Delete(Path.Combine(f.Roaming, "trackaudio"));
});
Console.WriteLine($"{passed} client configuration checks passed.");

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Launchpad-client-configuration-tests-" + Guid.NewGuid().ToString("N"));
    public string Local => Path.Combine(Root, "Local");
    public string Roaming => Path.Combine(Root, "Roaming");
    public string VatisConfig => Path.Combine(Local, "org.vatsim.vatis", "AppConfig.json");
    public string TrackConfig => Path.Combine(Roaming, "trackaudio", "config.json");
    public AppSettings Settings { get; }
    public Fixture()
    {
        Directory.CreateDirectory(Local); Directory.CreateDirectory(Roaming);
        Settings = new() { VatsimName = "Synthetic Övning", VatsimCid = "1234567", VatsimRating = 2,
            VatisExePath = Path.Combine(Local, "org.vatsim.vatis", "current", "vATIS.exe"),
            TrackAudioExePath = Path.Combine(Local, "Programs", "trackaudio", "trackaudio.exe") };
        foreach (string path in new[] { Settings.VatisExePath, Settings.TrackAudioExePath })
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "synthetic non-executable fixture"); }
    }
    public ClientSignInOptions Options(Func<SoftwareApp, bool>? running = null, bool lease = true,
        Func<string, ConfiguredBinaryMetadata>? binary = null, Action<string>? publisher = null, Func<string, int, byte[]?>? read = null) => new()
    {
        LocalAppData = Local, RoamingAppData = Roaming, IsRunning = running ?? (_ => false), HasMaintenanceLease = () => lease,
        ReadBinary = binary ?? (path => new(false, Path.GetFileNameWithoutExtension(path), ProductVersion: path.EndsWith("vATIS.exe") ? "4.1.0-beta.19+synthetic" : "1.4.0.0")),
        VerifyVatisPublisher = publisher ?? (_ => { }), ReadConfiguration = read ?? ClientConfigurationFiles.Read
    };
    public void Dispose()
    {
        string full = Path.GetFullPath(Root), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("Launchpad-client-configuration-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Synthetic cleanup escaped its test root.");
        Directory.Delete(full, recursive: true);
    }
}
static class Native
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateHardLink(string newPath, string existingPath, IntPtr security);
}
