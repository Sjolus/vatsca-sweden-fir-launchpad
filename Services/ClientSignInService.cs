using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Inject every native probe and data root in tests; no credential store is accessed here.</summary>
public sealed class ClientSignInOptions
{
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string RoamingAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public Func<string, ConfiguredBinaryMetadata> ReadBinary { get; init; } = ConfiguredPathValidationService.ReadBinaryMetadata;
    public Action<string> VerifyVatisPublisher { get; init; } = path => SoftwareInstallerNative.VerifyPublisher(path, "Justin Shannon");
    public Func<SoftwareApp, bool> IsRunning { get; init; } = AnyClientProcess;
    public Func<bool> HasMaintenanceLease { get; init; } = () => MaintenanceLock.IsHeldByCurrentProcess;
    public Func<string, int, byte[]?> ReadConfiguration { get; init; } = ClientConfigurationFiles.Read;
    public Action<string, byte[]?, byte[], Action> WriteConfiguration { get; init; } = ClientConfigurationFiles.Write;

    private static bool AnyClientProcess(SoftwareApp app)
    {
        string name = app switch { SoftwareApp.Vatis => "vATIS", SoftwareApp.TrackAudio => "trackaudio", _ => throw new ArgumentOutOfRangeException(nameof(app)) };
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

/// <summary>Writes saved sign-in fields only after an explicit configuration/install action.</summary>
public static class ClientSignInService
{
    public const int MaximumConfigurationBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true, MaxDepth = 32 };

    public static IReadOnlyList<ClientSignInOutcome> Apply(AppSettings settings, string password, SoftwareApp? onlyApp = null) =>
        Apply(settings, password, onlyApp, new ClientSignInOptions());

    public static IReadOnlyList<ClientSignInOutcome> Apply(AppSettings settings, string password,
        SoftwareApp? onlyApp, ClientSignInOptions options)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        return new[] { SoftwareApp.Vatis, SoftwareApp.TrackAudio }
            .Where(app => onlyApp is null || onlyApp == app)
            .Select(app => ApplyOne(app, settings, password ?? "", options)).ToArray();
    }

    private static ClientSignInOutcome ApplyOne(SoftwareApp app, AppSettings settings, string password, ClientSignInOptions options)
    {
        string name = app == SoftwareApp.Vatis ? "vATIS" : "TrackAudio";
        ClientSignInOutcome Result(ClientSignInStatus status, string message) => new(app, status, name + ": " + message);
        if (ApplicationPreferences.IsNotUsed(settings, name)) return Result(ClientSignInStatus.NotUsed, "not selected for use.");
        string exe = app == SoftwareApp.Vatis ? settings.VatisExePath : settings.TrackAudioExePath;
        if (string.IsNullOrWhiteSpace(exe)) return Result(ClientSignInStatus.NotConfigured, "no installation selected.");
        if (string.IsNullOrWhiteSpace(settings.VatsimCid))
            return Result(ClientSignInStatus.NotConfigured, "save your VATSIM CID in Controller profile first.");
        try
        {
            if (!options.HasMaintenanceLease()) return Result(ClientSignInStatus.Failed, "finish the current Launchpad operation and try again.");
            if (options.IsRunning(app)) return Result(ClientSignInStatus.Running, "close the application, then save Controller profile again to copy your sign-in details.");
            _ = ValidateClient(app, exe, options);
            string target = app == SoftwareApp.Vatis
                ? Path.Combine(options.LocalAppData, "org.vatsim.vatis", "AppConfig.json")
                : Path.Combine(options.RoamingAppData, "trackaudio", "config.json");
            target = ClientConfigurationFiles.ValidatePath(target);
            byte[]? previous = options.ReadConfiguration(target, MaximumConfigurationBytes);
            var patch = app == SoftwareApp.Vatis ? PatchVatis(previous, settings, password) : PatchTrackAudio(previous, settings, password);
            if (!patch.Changed) return Result(ClientSignInStatus.Unchanged, "sign-in details already match.");
            options.WriteConfiguration(target, previous, patch.Content, () =>
            {
                if (!options.HasMaintenanceLease()) throw new IOException("Configuration operation is no longer active.");
                if (options.IsRunning(app)) throw new ClientRunningException();
                _ = ValidateClient(app, exe, options);
            });
            return Result(ClientSignInStatus.Updated, "sign-in details copied.");
        }
        catch (ClientRunningException) { return Result(ClientSignInStatus.Running, "the application started before saving; its settings were left unchanged."); }
        catch (ClientConfigurationVersionException)
        { return Result(ClientSignInStatus.Unsupported, "this version needs a Launchpad compatibility update for sign-in setup. Its settings were left unchanged."); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or NotSupportedException)
        { return Result(ClientSignInStatus.Unsupported, "this installation or settings format is not supported for sign-in setup. Its settings were left unchanged."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            CryptographicException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return Result(ClientSignInStatus.Failed, "sign-in details could not be copied. Close the application and try again; your saved Launchpad details are retained."); }
    }

    /// <summary>Checks known executable identity without reading credentials, launching clients or checking for releases.</summary>
    public static string ValidateClient(SoftwareApp app, string configuredExe, ClientSignInOptions options)
    {
        string exe = ClientConfigurationFiles.ValidatePath(configuredExe);
        string expectedName;
        if (app == SoftwareApp.Vatis)
        {
            string root = ClientConfigurationFiles.ValidatePath(Path.Combine(options.LocalAppData, "org.vatsim.vatis"));
            string current = Path.Combine(root, "current", "vATIS.exe");
            if (!SamePath(exe, current) && !SamePath(exe, Path.Combine(root, "vATIS.exe")))
                throw new InvalidDataException("Unsupported client location.");
            exe = ClientConfigurationFiles.ValidatePath(current);
            expectedName = "vATIS";
        }
        else if (app == SoftwareApp.TrackAudio)
        {
            expectedName = "TrackAudio";
        }
        else throw new ArgumentOutOfRangeException(nameof(app));
        if (!Path.GetFileName(exe).Equals(expectedName + ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe))
            throw new InvalidDataException("Unsupported client executable.");
        using var parents = new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(exe)!, false);
        using var binaryLease = SaamanFileSafety.OpenRead(exe);
        var binary = options.ReadBinary(exe);
        string version = binary.ProductVersion.Split('+')[0].Trim();
        if (version == "1.4.0.0") version = "1.4.0";
        if (binary.IsDll || binary.IsInstaller || !binary.ProductName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported client identity.");
        bool compatible = app == SoftwareApp.Vatis
            ? SupportsVatisConfiguration(binary.ProductVersion.Trim())
            : version == "1.4.0";
        if (!compatible)
            throw new ClientConfigurationVersionException();
        if (app == SoftwareApp.Vatis) options.VerifyVatisPublisher(exe);
        return exe;
    }

    private static bool SupportsVatisConfiguration(string version)
    {
        // Accept later beta/rc and patch builds under the 4.1 configuration contract
        // reviewed from beta.19 onward. A new minor version needs review.
        if (!SoftwareVersion.TryParse(version, out var parsed) ||
            !SoftwareVersion.TryCompare(version, "4.1.0-beta.19", out int minimum) || minimum < 0 ||
            !SoftwareVersion.TryCompare(version, "4.2.0-0", out int maximum) || maximum >= 0)
            return false;
        if (!parsed.IsPrerelease) return true;
        string[] prerelease = version.Split('+')[0].Split('-', 2)[1].Split('.');
        return prerelease.Length == 2 && prerelease[0] is ("beta" or "rc") &&
            prerelease[1].All(character => character is >= '0' and <= '9');
    }

    public static ClientConfigurationPatch PatchVatis(byte[]? existing, AppSettings settings, string password)
    {
        ValidateIdentity(settings, password);
        JsonObject root = ParseObject(existing, caseInsensitive: true, trailingCommas: true);
        foreach (string field in new[] { "name", "userId", "password" }) RequireString(root, field, true);
        var rating = Find(root, "networkRating", true);
        if (rating.Value is not null && !IsVatisRating(rating.Value)) throw UnsupportedFormat();
        bool changed = false;
        if (!string.IsNullOrWhiteSpace(settings.VatsimName)) changed |= Set(root, "name", settings.VatsimName.Trim(), true);
        if (!string.IsNullOrWhiteSpace(settings.VatsimCid)) changed |= Set(root, "userId", settings.VatsimCid.Trim(), true);
        if (!string.IsNullOrWhiteSpace(password)) changed |= Set(root, "password", EncodeVatisPassword(password), true);
        if (settings.VatsimRating >= 0) changed |= Set(root, "networkRating", VatisRating(settings.VatsimRating), true);
        return Finish(existing, root, changed);
    }

    public static ClientConfigurationPatch PatchTrackAudio(byte[]? existing, AppSettings settings, string password)
    {
        ValidateIdentity(settings, password);
        JsonObject root = ParseObject(existing, caseInsensitive: false, trailingCommas: false);
        JsonObject configuration;
        if (existing is null) configuration = new JsonObject { ["version"] = 4 };
        else
        {
            if (root["configuration"] is not JsonValue value || !value.TryGetValue<string>(out string? json) || json is null)
                throw UnsupportedFormat();
            configuration = ParseObject(Encoding.UTF8.GetBytes(json), false, false);
            // TrackAudio 1.4 retains existing v2/v3 settings; only new configurations default to v4.
            // Leave migration (including legacy audio-device changes) to TrackAudio itself.
            if (configuration["version"] is not JsonValue version || !version.TryGetValue<int>(out int schema) || schema is not (2 or 3 or 4))
                throw UnsupportedFormat();
        }
        RequireString(configuration, "cid", false);
        RequireString(configuration, "password", false);
        bool changed = false;
        if (!string.IsNullOrWhiteSpace(settings.VatsimCid)) changed |= Set(configuration, "cid", settings.VatsimCid.Trim(), false);
        if (!string.IsNullOrWhiteSpace(password)) changed |= Set(configuration, "password", password, false);
        if (changed) root["configuration"] = configuration.ToJsonString();
        return Finish(existing, root, changed);
    }

    private static ClientConfigurationPatch Finish(byte[]? existing, JsonObject root, bool changed)
    {
        if (!changed) return new(existing ?? [], false);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(root, OutputOptions);
        if (content.Length > MaximumConfigurationBytes) throw UnsupportedFormat();
        return new(content, true);
    }

    private static JsonObject ParseObject(byte[]? contents, bool caseInsensitive, bool trailingCommas)
    {
        if (contents is null) return new JsonObject();
        if (contents.Length == 0 || contents.Length > MaximumConfigurationBytes) throw UnsupportedFormat();
        var bytes = contents.AsSpan();
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = trailingCommas });
            CheckDuplicateNames(document.RootElement, caseInsensitive);
            return JsonNode.Parse(document.RootElement.GetRawText(), documentOptions:
                new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = trailingCommas }) as JsonObject ?? throw UnsupportedFormat();
        }
        catch (JsonException) { throw UnsupportedFormat(); }
    }

    private static void CheckDuplicateNames(JsonElement element, bool caseInsensitive)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw UnsupportedFormat();
                CheckDuplicateNames(property.Value, caseInsensitive);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicateNames(item, caseInsensitive);
    }

    private static KeyValuePair<string, JsonNode?> Find(JsonObject root, string name, bool ignoreCase) =>
        root.FirstOrDefault(entry => entry.Key.Equals(name, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    private static void RequireString(JsonObject root, string name, bool ignoreCase)
    {
        var field = Find(root, name, ignoreCase);
        if (field.Value is not null && (field.Value is not JsonValue value || !value.TryGetValue<string>(out _))) throw UnsupportedFormat();
    }

    private static bool Set(JsonObject root, string name, string value, bool ignoreCase)
    {
        var field = Find(root, name, ignoreCase);
        if (field.Value is JsonValue current && current.TryGetValue<string>(out string? text) && text == value) return false;
        root[field.Key ?? name] = value;
        return true;
    }

    private static bool IsVatisRating(JsonNode node) => node is JsonValue value &&
        (value.TryGetValue<int>(out int number) && number is >= 1 and <= 12 ||
        value.TryGetValue<string>(out string? text) && Enumerable.Range(0, 12).Any(rating => VatisRating(rating).Equals(text, StringComparison.OrdinalIgnoreCase)));

    public static string VatisRating(int launchpadRating) => launchpadRating switch
    {
        0 => "Obs", 1 => "S1", 2 => "S2", 3 => "S3", 4 => "C1", 5 => "C2", 6 => "C3",
        7 => "I1", 8 => "I2", 9 => "I3", 10 => "Sup", 11 => "Adm", _ => throw UnsupportedFormat()
    };

    private static void ValidateIdentity(AppSettings settings, string password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string cid = settings.VatsimCid?.Trim() ?? "";
        if (cid.Length > 10 || cid.Any(character => character is < '0' or > '9') ||
            settings.VatsimName is { Length: > 256 } || (settings.VatsimName ?? "").Any(char.IsControl) ||
            password.Length > 1024 || password.Contains('\0') || settings.VatsimRating is < -1 or > 11)
            throw UnsupportedFormat();
    }

    // vATIS beta.19–22 AppConfig uses this deterministic vendor format. It is not a secure secret store.
    // https://github.com/vatis-project/vatis/blob/v4.1.0-beta.22/vATIS.Desktop/Config/AppConfig.cs
    private static string EncodeVatisPassword(string password)
    {
        using var cipher = TripleDES.Create();
        cipher.Key = MD5.HashData(Encoding.UTF8.GetBytes("b650f0bd-9823-46b7-8ea6-128a3b2f98af"));
        cipher.Mode = CipherMode.ECB;
        cipher.Padding = PaddingMode.PKCS7;
        byte[] cleartext = Encoding.UTF8.GetBytes(password);
        try { return Convert.ToBase64String(cipher.CreateEncryptor().TransformFinalBlock(cleartext, 0, cleartext.Length)); }
        finally { CryptographicOperations.ZeroMemory(cleartext); }
    }

    private static bool SamePath(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static InvalidDataException UnsupportedFormat() => new("The client configuration format is not supported.");
    private sealed class ClientRunningException : IOException;
}
