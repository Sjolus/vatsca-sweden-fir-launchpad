using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public sealed record VatEfsProfileResult(bool Enabled, int ProfilesChecked, int ProfilesChanged, string? RecoveryFolder);

public sealed class VatEfsProfileRecoveryException(string recoveryFolder, Exception cause) : IOException(
    "The VatEFS profile change stopped and could not be fully restored. Close EuroScope, then recover the original profiles from: " + recoveryFolder, cause)
{
    public string RecoveryFolder { get; } = recoveryFolder;
}

/// <summary>Changes only VatEFS plugin entries. The caller owns the maintenance lease and saves the choice after success.</summary>
public static class VatEfsProfileService
{
    private const int MaximumProfiles = 128, MaximumProfileBytes = 4 * 1024 * 1024, MaximumTotalBytes = 32 * 1024 * 1024;
    internal sealed record EnvironmentOptions(string RecoveryRoot, Action RequireClosed, Action<string> RequireNoPendingUpdate,
        Func<bool> HasMaintenanceLease, Action<int>? BeforeWrite = null);
    private sealed record Prepared(string Path, FileStream File, byte[] Before, byte[] After)
    {
        internal bool Changed => !Before.AsSpan().SequenceEqual(After);
    }

    public static VatEfsProfileResult Apply(AppSettings settings, bool enabled) => Apply(settings, enabled, new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VatscaUpdateChecker", "VatEfs", "ProfileRecovery"),
        EuroScopeProcessGuard.RequireClosed, GngUpdateService.RequireNoPendingUpdate, () => MaintenanceLock.IsHeldByCurrentProcess));

    internal static VatEfsProfileResult Apply(AppSettings settings, bool enabled, EnvironmentOptions options)
    {
        RequireAllowed();
        if (string.IsNullOrWhiteSpace(settings.EuroscopeDataPath))
            throw new InvalidOperationException("Choose your EuroScope data folder before changing VatEFS.");
        string root = SaamanFileSafety.Canonical(settings.EuroscopeDataPath);
        if (!Directory.Exists(root) || root.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an existing EuroScope data folder before changing VatEFS.");
        options.RequireNoPendingUpdate(root);
        using var dataParents = new SaamanFileSafety.DirectoryGuard(root, false);
        string[] paths = ProfilePaths(root);
        if (paths.Length == 0) throw new InvalidOperationException("No Swedish ES*.prf profiles were found in the EuroScope data folder.");

        string dllPath = "";
        SaamanFileSafety.DirectoryGuard? dllParents = null;
        FileStream? dll = null;
        var prepared = new List<Prepared>();
        try
        {
            if (enabled)
            {
                if (string.IsNullOrWhiteSpace(settings.VatEfsPath))
                    throw new InvalidOperationException("Choose the installed VatEFS folder before enabling its plugin.");
                dllPath = SaamanFileSafety.Canonical(Path.Combine(settings.VatEfsPath, "VatEFS.dll"));
                dllParents = new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(dllPath)!, false);
                dll = SaamanFileSafety.OpenRead(dllPath);
                RequireSingleFile(dll.SafeFileHandle);
                ValidateDll(dll);
            }
            int total = 0;
            foreach (var path in paths)
            {
                SaamanFileSafety.NoLinks(path);
                var file = SaamanFileSafety.OpenReadWrite(path);
                try
                {
                    RequireSingleFile(file.SafeFileHandle);
                    if (file.Length > MaximumProfileBytes || (total += checked((int)file.Length)) > MaximumTotalBytes)
                        throw new InvalidDataException("The EuroScope profiles exceed the supported change size.");
                    var before = new byte[(int)file.Length]; file.ReadExactly(before);
                    var after = Transform(before, dllPath, enabled);
                    if (after.Length > MaximumProfileBytes) throw new InvalidDataException("The changed EuroScope profile would be too large.");
                    prepared.Add(new(path, file, before, after));
                }
                catch { file.Dispose(); throw; }
            }
            var changed = prepared.Where(item => item.Changed).ToArray();
            RequireAllowed(); options.RequireNoPendingUpdate(root); RequireSameProfiles();
            if (changed.Length == 0) return new(enabled, prepared.Count, 0, null);

            string recoveryRoot = SaamanFileSafety.Canonical(options.RecoveryRoot);
            if (SaamanFileSafety.Overlaps(recoveryRoot, root))
                throw new InvalidOperationException("VatEFS recovery must be outside the EuroScope data folder.");
            string backup = Path.Combine(recoveryRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            using var recoveryParents = new SaamanFileSafety.DirectoryGuard(backup, true);
            ProtectRecoveryDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "RESTORE.txt"),
                "VatEFS profile recovery\r\n\r\n" +
                "These are the original EuroScope profiles from before this VatEFS change.\r\n" +
                "Close EuroScope before restoring. Copy the .prf files into the original EuroScope data folder, replacing the matching files.\r\n" +
                "Restoring also replaces any later changes to these profiles. Keep a copy of their current versions first if needed.\r\n" +
                "Profiles may contain login details. Keep this recovery folder private.\r\n", new UTF8Encoding(false));
            foreach (var item in changed)
            {
                using var recovery = new FileStream(Path.Combine(backup, Path.GetFileName(item.Path)), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                recovery.Write(item.Before); recovery.Flush(true); recovery.Position = 0;
                if (!SHA256.HashData(recovery).AsSpan().SequenceEqual(SHA256.HashData(item.Before)))
                    throw new IOException("The VatEFS recovery copy could not be verified. No profiles were changed.");
            }
            var touched = new List<Prepared>();
            try
            {
                for (int index = 0; index < changed.Length; index++)
                {
                    options.BeforeWrite?.Invoke(index);
                    RequireAllowed(); options.RequireNoPendingUpdate(root); RequireSameProfiles();
                    var item = changed[index]; touched.Add(item);
                    WriteVerified(item.File, item.After);
                }
            }
            catch (Exception cause)
            {
                bool restored = true;
                foreach (var item in touched.AsEnumerable().Reverse())
                    try
                    {
                        RequireAllowed(); options.RequireNoPendingUpdate(root);
                        WriteVerified(item.File, item.Before);
                    }
                    catch { restored = false; }
                if (!restored)
                    throw new VatEfsProfileRecoveryException(backup, cause);
                throw;
            }
            return new(enabled, prepared.Count, changed.Length, backup);
        }
        finally
        {
            foreach (var item in prepared) item.File.Dispose();
            dll?.Dispose(); dllParents?.Dispose();
        }

        void RequireAllowed()
        {
            if (!options.HasMaintenanceLease()) throw new InvalidOperationException("Another Launchpad operation is active. Try the VatEFS change again when it finishes.");
            options.RequireClosed();
        }
        void RequireSameProfiles()
        {
            if (!paths.SequenceEqual(ProfilePaths(root), StringComparer.OrdinalIgnoreCase))
                throw new IOException("The EuroScope profile list changed while preparing VatEFS. Try again.");
        }
    }

    internal static byte[] Transform(byte[] bytes, string dllPath, bool enabled)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        byte[] bom = [];
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = new UTF8Encoding(false, true); bom = [0xEF, 0xBB, 0xBF]; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); bom = [0xFF, 0xFE]; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); bom = [0xFE, 0xFF]; }
        string text = encoding.GetString(bytes, bom.Length, bytes.Length - bom.Length);
        if (text.Contains('\0')) throw new InvalidDataException("The EuroScope profile is not a supported text file.");
        string transformed = ProfileService.TransformVatEfs(text, dllPath, enabled);
        return transformed == text ? bytes : bom.Concat(encoding.GetBytes(transformed)).ToArray();
    }

    private static string[] ProfilePaths(string root)
    {
        var paths = Directory.EnumerateFiles(root, "ES*.prf", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".prf", StringComparison.OrdinalIgnoreCase))
            .Take(MaximumProfiles + 1).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length > MaximumProfiles) throw new InvalidDataException("Too many EuroScope profiles were found to change safely.");
        return paths;
    }

    private static void WriteVerified(FileStream stream, byte[] bytes)
    {
        stream.Position = 0; stream.Write(bytes); stream.SetLength(bytes.Length); stream.Flush(true); stream.Position = 0;
        if (!SHA256.HashData(stream).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
            throw new IOException("The written VatEFS profile did not verify.");
    }

    private static void ProtectRecoveryDirectory(string path)
    {
        // The unique child is still empty; never change permissions on a shared parent.
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using var user = WindowsIdentity.GetCurrent();
        var sid = user.User ?? throw new InvalidOperationException("Windows account identity is unavailable for private profile recovery.");
        foreach (var account in new[] { sid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void ValidateDll(FileStream file)
    {
        if (file.Length < 88 || file.Length > SaamanFileSafety.MaximumDllSize) throw new InvalidDataException("Choose a valid VatEFS plugin DLL.");
        byte[] header = new byte[64]; file.ReadExactly(header);
        int pe = BitConverter.ToInt32(header, 60);
        if (header[0] != 'M' || header[1] != 'Z' || pe < 64 || pe > file.Length - 24) throw new InvalidDataException("VatEFS.dll is not a Windows plugin DLL.");
        file.Position = pe; byte[] image = new byte[24]; file.ReadExactly(image);
        if (BitConverter.ToUInt32(image) != 0x4550 || BitConverter.ToUInt16(image, 4) != 0x14c || (BitConverter.ToUInt16(image, 22) & 0x2000) == 0)
            throw new InvalidDataException("VatEFS.dll must be a 32-bit Windows plugin DLL for EuroScope.");
    }

    private static void RequireSingleFile(SafeFileHandle file)
    {
        if (!GetFileInformationByHandle(file, out var info) || info.NumberOfLinks != 1 ||
            (info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("VatEFS profile changes require regular files without links.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
