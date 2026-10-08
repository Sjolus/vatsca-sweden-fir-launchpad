using System.IO;
using System.Runtime.InteropServices;

namespace VatscaUpdateChecker.Services;

/// <summary>Windows ships msiexec with either an embedded or a system catalog signature.</summary>
internal static class VatEfsMsiTrust
{
    internal static void VerifySystemInstaller(string path)
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
        if (!VatEfsInstaller.FullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only Windows' own system MSI installer may be elevated.");
        VatEfsInstaller.RejectReparse(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { SoftwareInstallerNative.VerifyPublisher(path, "Microsoft Corporation", "Microsoft Windows"); return; }
        catch (InvalidDataException) { } // Try catalog membership as a separate, fully verified signature route.

        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
            throw new InvalidDataException("Windows could not open its signature catalog verifier.");
        try
        {
            uint length = 0;
            CryptCATAdminCalcHashFromFileHandle2(admin, file.SafeFileHandle.DangerousGetHandle(), ref length, null, 0);
            if (length != 32) throw new InvalidDataException("The Windows Installer catalog hash is unavailable.");
            var hash = new byte[length];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, file.SafeFileHandle.DangerousGetHandle(), ref length, hash, 0))
                throw new InvalidDataException("Windows could not hash its MSI installer for catalog verification.");
            var catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, length, 0, IntPtr.Zero);
            if (catalog == IntPtr.Zero) throw new InvalidDataException("Windows Installer has no verifiable system catalog signature.");
            try
            {
                var info = new CatalogInfo { Size = (uint)Marshal.SizeOf<CatalogInfo>(), Path = "" };
                if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0))
                    throw new InvalidDataException("Windows could not locate its installer signature catalog.");
                VatEfsInstaller.RejectReparse(info.Path);
                using var catalogFile = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                // Verify the catalog's signer independently of file membership; a trusted third-party
                // catalog must never authorize an executable to run as Windows Installer.
                SoftwareInstallerNative.VerifyPublisher(info.Path, "Microsoft Corporation", "Microsoft Windows");
                VerifyMember(path, info.Path, file, admin, hash);
            }
            finally { CryptCATAdminReleaseCatalogContext(admin, catalog, 0); }
        }
        finally { CryptCATAdminReleaseContext(admin, 0); }
    }

    private static void VerifyMember(string path, string catalog, FileStream file, IntPtr admin, byte[] hash)
    {
        var member = new TrustCatalog
        {
            Size = (uint)Marshal.SizeOf<TrustCatalog>(), CatalogPath = catalog, MemberTag = Convert.ToHexString(hash),
            MemberPath = path, MemberFile = file.SafeFileHandle.DangerousGetHandle(), Admin = admin
        };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustCatalog>());
        Marshal.StructureToPtr(member, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, RevocationChecks = 1,
            UnionChoice = 2, Info = pointer, StateAction = 1, ProviderFlags = 0x80 };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0)
                throw new InvalidDataException("Windows could not verify its MSI installer's membership in the signed catalog.");
        }
        finally
        {
            data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustCatalog>(pointer); Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo { public uint Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustCatalog
    {
        public uint Size, Version;
        [MarshalAs(UnmanagedType.LPWStr)] public string CatalogPath;
        [MarshalAs(UnmanagedType.LPWStr)] public string MemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string MemberPath;
        public IntPtr MemberFile, CalculatedHash;
        public uint HashLength;
        public IntPtr CatalogContext, Admin;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallback, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr Info;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string algorithm, IntPtr policy, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, IntPtr file, ref uint length, [Out] byte[]? hash, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint length, uint flags, IntPtr previous);
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalog, ref CatalogInfo info, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
