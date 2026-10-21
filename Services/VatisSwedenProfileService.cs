using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public enum VatisSwedenProfileStatus { Installed, AlreadyConfigured, NotConfigured, Blocked, Conflict, Failed }

public sealed record VatisSwedenProfileResult(VatisSwedenProfileStatus Status, string Message)
{
    public bool Success => Status is VatisSwedenProfileStatus.Installed or VatisSwedenProfileStatus.AlreadyConfigured;
}

internal sealed class VatisSwedenProfileEnvironment
{
    internal required string LocalApplicationData { get; init; }
    internal required Action<string> ValidateInstallation { get; init; }
    internal required Func<bool> IsRunning { get; init; }
    internal required Func<bool> HasMaintenanceLease { get; init; }
    internal required HttpClient HttpClient { get; init; }
}

/// <summary>Seeds the official Swedish profile during explicit setup. Existing profiles are never replaced.</summary>
public sealed class VatisSwedenProfileService
{
    public const string ProfileUrl = "https://raw.githubusercontent.com/Vatsim-Scandinavia/ESAA-vATIS/refs/heads/main/vATIS_ESAA_auto.json";
    internal const int MaximumProfileBytes = 4 * 1024 * 1024;
    internal const int MaximumDirectoryEntries = 256;
    internal const long MaximumInspectionBytes = 32L * 1024 * 1024;
    private static readonly Guid BundledProfileId = Guid.Parse("8149f89b-2433-4d85-be9d-7a5f1c763019");
    private static readonly Guid CurrentProfileId = Guid.Parse("b69dd50d-3deb-4e3a-be55-2f7801ad9755");
    private static readonly HttpClient DownloadClient = CreateHttpClient();
    private readonly VatisSwedenProfileEnvironment _environment;

    public VatisSwedenProfileService()
    {
        var options = new ClientSignInOptions();
        _environment = new()
        {
            LocalApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ValidateInstallation = path => ClientSignInService.ValidateClient(SoftwareApp.Vatis, path, options),
            IsRunning = () => options.IsRunning(SoftwareApp.Vatis),
            HasMaintenanceLease = () => MaintenanceLock.IsHeldByCurrentProcess,
            HttpClient = DownloadClient
        };
    }

    internal VatisSwedenProfileService(VatisSwedenProfileEnvironment environment) => _environment = environment;

    // The caller owns MaintenanceLock across related setup actions. Never reacquire its process-wide lease here.
    public async Task<VatisSwedenProfileResult> EnsureAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (ApplicationPreferences.IsNotUsed(settings, "vATIS"))
            return new(VatisSwedenProfileStatus.NotConfigured, "vATIS is not selected for use.");
        if (string.IsNullOrWhiteSpace(settings.VatisExePath))
            return new(VatisSwedenProfileStatus.NotConfigured, "Choose the installed vATIS copy first.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireReady(settings);
            string localData = FileSafety.LocalPath(_environment.LocalApplicationData);
            string directory = Path.Combine(localData, "org.vatsim.vatis", "Profiles");
            FileSafety.NoLinks(directory);
            var existing = Inspect(directory, null);
            if (existing != null) return existing;

            byte[] downloaded = await DownloadAsync(cancellationToken).ConfigureAwait(false);
            using var document = Parse(downloaded);
            Guid profileId = ValidateDownloaded(document.RootElement);
            RequireReady(settings);
            cancellationToken.ThrowIfCancellationRequested();

            using var directories = new FileSafety.DirectoryLease(directory, create: true);
            existing = Inspect(directory, profileId);
            if (existing != null) return existing;

            string destination = Path.Combine(directory, profileId.ToString("D") + ".json");
            string temporary = Path.Combine(directory, ".launchpad-" + Guid.NewGuid().ToString("N") + ".tmp");
            using var output = FileSafety.Create(temporary);
            bool moved = false;
            try
            {
                await output.WriteAsync(downloaded, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                RequireReady(settings);
                cancellationToken.ThrowIfCancellationRequested();
                existing = Inspect(directory, profileId);
                if (existing != null) return existing;
                FileSafety.Move(output.SafeFileHandle, destination);
                moved = true;
                return new(VatisSwedenProfileStatus.Installed, "Swedish vATIS profile added. vATIS will keep it up to date.");
            }
            finally
            {
                if (!moved) FileSafety.Delete(output.SafeFileHandle);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ProfileConflictException)
        {
            return new(VatisSwedenProfileStatus.Conflict, "Existing vATIS profiles need review. They were kept unchanged; import the Swedish profile in vATIS if needed.");
        }
        catch (ProfileBlockedException)
        {
            return new(VatisSwedenProfileStatus.Blocked, "Close vATIS and finish other maintenance before setting up its Swedish profile.");
        }
        catch (HttpRequestException)
        {
            return new(VatisSwedenProfileStatus.Failed, "The Swedish vATIS profile could not be downloaded. Try setup again later.");
        }
        catch (OperationCanceledException)
        {
            return new(VatisSwedenProfileStatus.Failed, "The Swedish vATIS profile download timed out. Try setup again later.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or
            System.Security.Cryptography.CryptographicException or
            JsonException or ArgumentException or NotSupportedException or Win32Exception or InvalidOperationException)
        {
            // No client JSON, exception text, paths or credentials enter activity/reporting messages.
            return new(VatisSwedenProfileStatus.Failed, "The Swedish vATIS profile could not be set up safely. Existing profiles were kept unchanged.");
        }
    }

    private void RequireReady(AppSettings settings)
    {
        if (!_environment.HasMaintenanceLease() || _environment.IsRunning()) throw new ProfileBlockedException();
        _environment.ValidateInstallation(settings.VatisExePath);
    }

    private async Task<byte[]> DownloadAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
        request.Headers.UserAgent.ParseAdd("SwedenFirLaunchpad");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _environment.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri?.AbsoluteUri != ProfileUrl)
            throw new HttpRequestException("Unexpected profile response.");
        if (response.Content.Headers.ContentLength is > MaximumProfileBytes)
            throw new InvalidDataException("Profile size limit exceeded.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16384];
        while (true)
        {
            int count = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaximumProfileBytes) throw new InvalidDataException("Profile size limit exceeded.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = MaximumProfileBytes
    };

    private static JsonDocument Parse(byte[] bytes)
    {
        ReadOnlyMemory<byte> json = bytes;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) json = json[3..];
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        try { RequireUnambiguousJson(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    internal static Guid ValidateProfile(byte[] bytes)
    {
        if (bytes.Length > MaximumProfileBytes) throw new InvalidDataException("Profile size limit exceeded.");
        using var document = Parse(bytes);
        return ValidateDownloaded(document.RootElement);
    }

    private static void RequireUnambiguousJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new InvalidDataException("Duplicate JSON field.");
                RequireUnambiguousJson(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RequireUnambiguousJson(child);
    }

    private static Guid ValidateDownloaded(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid profile.");
        // These are profile fields, not a personal AppConfig. Station-specific schema remains the vendor's.
        string[] allowed = ["name", "id", "updateUrl", "updateSerial", "stations", "version"];
        if (root.EnumerateObject().Any(field => !allowed.Contains(field.Name, StringComparer.Ordinal)))
            throw new InvalidDataException("Unexpected profile field.");
        if (!TryGuid(root, "id", out var id) || !IsOfficialUrl(Text(root, "updateUrl")) ||
            !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int schema) || schema != 4 ||
            !root.TryGetProperty("updateSerial", out var serial) || !serial.TryGetInt32(out int revision) || revision < 2000010100 ||
            !ValidText(Text(root, "name"), 120) || !root.TryGetProperty("stations", out var stations) ||
            stations.ValueKind != JsonValueKind.Array || stations.GetArrayLength() is < 1 or > 128)
            throw new InvalidDataException("Invalid Swedish profile metadata.");
        var stationIds = new HashSet<Guid>();
        foreach (var station in stations.EnumerateArray())
        {
            string identifier = Text(station, "identifier");
            if (!TryGuid(station, "id", out var stationId) || !stationIds.Add(stationId) ||
                identifier.Length != 4 || !identifier.StartsWith("ES", StringComparison.Ordinal) ||
                identifier.Any(c => c is < 'A' or > 'Z') || !ValidText(Text(station, "name"), 120) ||
                !station.TryGetProperty("atisFormat", out var format) || format.ValueKind != JsonValueKind.Object ||
                !station.TryGetProperty("presets", out var presets) || presets.ValueKind != JsonValueKind.Array || presets.GetArrayLength() is < 1 or > 128)
                throw new InvalidDataException("Invalid Swedish station.");
        }
        return id;
    }

    private static VatisSwedenProfileResult? Inspect(string directory, Guid? remoteId)
    {
        if (!Directory.Exists(directory))
        {
            if (File.Exists(directory)) throw new ProfileConflictException();
            return null;
        }
        using var parents = new FileSafety.DirectoryLease(directory, create: false);
        int count = 0, matching = 0;
        long inspectedBytes = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (++count > MaximumDirectoryEntries) throw new ProfileConflictException();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ProfileConflictException();
            if ((attributes & FileAttributes.Directory) != 0 || !Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
            using var file = FileSafety.Read(path);
            if (file.Length > MaximumProfileBytes || (inspectedBytes += file.Length) > MaximumInspectionBytes)
                throw new ProfileConflictException();
            byte[] bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            try
            {
                using var document = Parse(bytes);
                var root = document.RootElement;
                if (!TryGuid(root, "id", out var id)) throw new ProfileConflictException();
                string updateUrl = Text(root, "updateUrl");
                bool official = IsOfficialUrl(updateUrl);
                bool reservedId = id == BundledProfileId || id == CurrentProfileId || id == remoteId;
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var filenameId) || filenameId != id)
                    throw new ProfileConflictException();
                if (official)
                {
                    _ = ValidateDownloaded(root);
                    if (++matching > 1) throw new ProfileConflictException();
                }
                else if (reservedId) throw new ProfileConflictException();
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
            {
                throw new ProfileConflictException();
            }
        }
        return matching == 1 ? new(VatisSwedenProfileStatus.AlreadyConfigured,
            "Swedish vATIS profile is already set up. vATIS handles its profile updates.") : null;
    }

    internal static bool IsOfficialUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return false;
        return uri.AbsolutePath is "/Vatsim-Scandinavia/ESAA-vATIS/refs/heads/main/vATIS_ESAA_auto.json" or
            "/Vatsim-Scandinavia/ESAA-vATIS/main/vATIS_ESAA_auto.json";
    }

    private static bool TryGuid(JsonElement root, string name, out Guid value) =>
        Guid.TryParseExact(Text(root, name), "D", out value) && value != Guid.Empty;
    private static string Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    private static bool ValidText(string value, int maximum) => value.Length is > 0 && value.Length <= maximum &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private sealed class ProfileConflictException : Exception;
    private sealed class ProfileBlockedException : Exception;

    private static class FileSafety
    {
        internal static string LocalPath(string path)
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
                path.IndexOfAny(['"', '\r', '\n', '*', '?', '\0']) >= 0)
                throw new InvalidDataException("Unsupported profile location.");
            string full = Path.GetFullPath(path);
            if (full.AsSpan(2).Contains(':') || full[3..].Split(Path.DirectorySeparatorChar).Any(p => p.EndsWith('.') || p.EndsWith(' ')))
                throw new InvalidDataException("Ambiguous profile location.");
            NoLinks(full);
            return full;
        }

        internal static void NoLinks(string path)
        {
            for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked profile location.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
        }

        internal sealed class DirectoryLease : IDisposable
        {
            private readonly List<SafeFileHandle> _handles = [];
            internal DirectoryLease(string path, bool create)
            {
                var chain = new Stack<string>();
                for (string? current = LocalPath(path); current != null; current = Path.GetDirectoryName(current)) chain.Push(current);
                try
                {
                    foreach (string directory in chain)
                    {
                        if (create && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                        var handle = CreateFile(Extended(directory), 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot hold profile directory."); }
                        _handles.Add(handle);
                        Check(handle, directory: true);
                    }
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
                _handles.Clear();
            }
        }

        internal static FileStream Read(string path) => Open(path, create: false);
        internal static FileStream Create(string path) => Open(path, create: true);
        private static FileStream Open(string path, bool create)
        {
            var handle = CreateFile(Extended(path), create ? 0xC0010000u : 0x80000000u, create ? 0u : 1u,
                IntPtr.Zero, create ? 1u : 3u, 0x00200080, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot hold profile file."); }
            try { Check(handle, directory: false); return new FileStream(handle, create ? FileAccess.ReadWrite : FileAccess.Read); }
            catch { handle.Dispose(); throw; }
        }

        internal static void Move(SafeFileHandle handle, string destination)
        {
            byte[] name = Encoding.Unicode.GetBytes(Extended(destination));
            int rootOffset = IntPtr.Size == 8 ? 8 : 4, lengthOffset = rootOffset + IntPtr.Size, nameOffset = lengthOffset + 4;
            int size = nameOffset + name.Length + 2;
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, memory, size);
                Marshal.WriteInt32(memory, lengthOffset, name.Length);
                Marshal.Copy(name, 0, IntPtr.Add(memory, nameOffset), name.Length);
                if (!SetFileInformationByHandle(handle, 3, memory, (uint)size)) throw new IOException("Existing profiles cannot be replaced.");
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        internal static void Delete(SafeFileHandle handle)
        {
            var memory = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(memory, 1);
                if (!SetFileInformationByHandle(handle, 4, memory, 4)) throw new IOException("Cannot remove incomplete profile download.");
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        private static void Check(SafeFileHandle handle, bool directory)
        {
            if (!GetFileInformationByHandle(handle, out var info) ||
                (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory || (!directory && info.NumberOfLinks != 1))
                throw new IOException("Profile path changed or is linked.");
        }
        private static string Extended(string path) => @"\\?\" + Path.GetFullPath(path);
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    }
}
