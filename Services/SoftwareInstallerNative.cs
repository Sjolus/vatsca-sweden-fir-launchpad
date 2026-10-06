using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace VatscaUpdateChecker.Services;

internal static class SoftwareInstallerNative
{
    internal static void VerifyPublisher(string path, string expectedPublisher)
    {
        SoftwareInstaller.RejectReparse(path);
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), FilePath = Marshal.StringToCoTaskMemUni(path) };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, filePointer, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, RevocationChecks = 1,
            UnionChoice = 1, File = filePointer, StateAction = 1, ProviderFlags = 0x80
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            // Windows verifies the signed digest, timestamp, chain and revocation. A certificate merely
            // embedded in a PE is not sufficient. Short-lived publisher certificates are not thumbprint-pinned.
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0)
                throw new InvalidDataException("Windows could not verify the expected publisher's trusted signature.");
#pragma warning disable SYSLIB0057 // CreateFromSignedFile reads the verified Authenticode signer, not a PFX/private key.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!signer.GetNameInfo(X509NameType.SimpleName, false).Equals(expectedPublisher, StringComparison.Ordinal) ||
                !signer.Subject.Split(',').Any(component => component.Trim() == "O=" + expectedPublisher))
                throw new InvalidDataException("The executable is signed by an unexpected publisher.");
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeCoTaskMem(file.FilePath);
        }
    }

    internal static async Task<int> RunAsync(SoftwareInstallCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            Arguments = string.Join(" ", command.Arguments.Select(QuoteArgument)),
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = command.Elevate,
            Verb = command.Elevate ? "runas" : "",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process? process;
        try
        {
            try { process = Process.Start(startInfo); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 740 && !command.Elevate)
            {
                // runas can switch HKCU to another account with supplied credentials. Do not risk
                // changing a per-user installer destination; the official UI can handle this case.
                throw new IOException("This per-user installer needs elevation. Use its official installer manually so Windows keeps the intended user and installation path.", ex);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return 1223; }
        if (process == null) throw new IOException("Windows did not provide an installer process to monitor.");
        using (process)
        {
            // The supported NSIS scripts use ExecWait for their prerequisites/uninstallers, and
            // elevation is requested before this monitored process starts. Velopack apply is itself
            // synchronous. Never adopt arbitrary descendants by PID, which can be reused.
            // No cancellation, timeout or Kill: installation must run to completion once started.
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }

    internal static string QuoteArgument(string value)
    {
        // NSIS reads some switches directly from its command line. Leave simple tokens intact;
        // only paths/arguments that need grouping receive CRT escaping.
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        // CommandLineToArgvW/CRT quoting: double backslashes before a quote or closing delimiter.
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append(character);
            else result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile { public uint Size; public IntPtr FilePath; public IntPtr FileHandle; public IntPtr KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallback, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);
}
