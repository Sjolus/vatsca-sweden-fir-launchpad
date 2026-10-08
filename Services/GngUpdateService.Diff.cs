using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public static partial class GngUpdateService
{
    private const int TextPreviewLimit = 2 * 1024 * 1024;
    private const int TextPreviewLineLimit = 20_000;
    private const string HiddenValue = "[hidden]";
    private static readonly HashSet<string> PreviewTextExtensions = new(Paths)
        { ".prf", ".asr", ".sct", ".ese", ".rwy", ".txt", ".ini", ".cfg" };
    private static readonly Regex CredentialLabel = new(
        @"password|passwd|\bpwd\b|token|secret|credential|auth|api[_ -]?key|access[_ -]?key|private[_ -]?key|client[_ -]?key|hoppie|login|cookie|session[_ -]?id|connection[_ -]?string|webhook",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SensitiveUrl = new(@"[a-z][a-z0-9+.-]*://[^\s]*(?:@|\?)[^\s]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Reads one exact planned result without writing files or exposing personal values.</summary>
    public static GngFilePreview PreviewFile(GngUpdatePlan plan, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var relative = Normalize(relativePath);
        var matches = plan.Files.Where(f => Paths.Equals(f.RelativePath, relative)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Select a file from the current installation review.");
        var selected = matches[0];
        if (!ValidHash(plan.PackageHash) || !ValidHash(selected.AfterHash) ||
            selected.AfterLength is < 0 or > MaxFile || selected.BeforeLength is < 0 or > MaxFile ||
            (selected.BeforeHash is not null && !ValidHash(selected.BeforeHash)))
            throw new InvalidOperationException("The installation review is incomplete. Create a new review.");

        var root = ExistingDirectory(plan.DataFolder);
        var localPath = Contained(root, relative);
        RequireUnprotected(localPath, plan.ProtectedPaths);
        using var sourceDependencies = GuardPreviewDependencies(plan, selected);
        var zipPath = CanonicalPath(plan.PackagePath);
        using var packageParents = new DirectoryGuard(Path.GetDirectoryName(zipPath)!, false);
        using var packageInput = OpenPreviewFile(zipPath);
        if (packageInput.Length > MaxTotal || Hash(packageInput) != plan.PackageHash)
            throw new InvalidOperationException("The package changed after review. Create a new installation review.");
        var package = ReadPackage(zipPath);
        if (package.Hash != plan.PackageHash || !package.Entries.TryGetValue(relative, out var entry))
            throw new InvalidOperationException("The selected file no longer matches the reviewed package.");
        packageInput.Position = 0;

        bool personal = IsPersonal(relative) || CredentialLabel.IsMatch(Path.GetFileNameWithoutExtension(relative)) || relative.Split('\\').Any(p =>
            p.Equals("Local", StringComparison.OrdinalIgnoreCase) || p.Contains("Hoppie", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("LoginProfiles", StringComparison.OrdinalIgnoreCase));
        bool supported = PreviewTextExtensions.Contains(Path.GetExtension(relative));
        bool small = selected.BeforeLength <= TextPreviewLimit && selected.AfterLength <= TextPreviewLimit;
        bool captureText = !personal && supported && small;
        byte[]? before = ReadCurrentPreviewBytes(localPath, selected, captureText);
        byte[]? after = null;
        if (!selected.WritesFile)
        {
            if (selected.BeforeHash is null || selected.AfterHash != selected.BeforeHash || selected.AfterLength != selected.BeforeLength)
                throw new InvalidOperationException("The retained file no longer matches its installation review.");
            after = before;
        }
        else if (selected.MergedBytes is { } merged)
        {
            var bytes = merged.ToArray();
            if (bytes.LongLength != selected.AfterLength || Hash(bytes) != selected.AfterHash)
                throw new InvalidOperationException("The merged file changed after review. Create a new installation review.");
            if (captureText) after = bytes;
        }
        else
        {
            if (entry.Hash != selected.AfterHash || entry.Length != selected.AfterLength)
                throw new InvalidOperationException("The package file no longer matches the planned result.");
            if (captureText)
            {
                using var zip = new ZipArchive(packageInput, ZipArchiveMode.Read, leaveOpen: true);
                var member = zip.GetEntry(entry.ZipName) ?? throw new InvalidOperationException("The selected package file is missing.");
                using var input = member.Open(); using var output = new MemoryStream();
                CopyBounded(input, output, selected.AfterLength);
                after = output.ToArray();
                if (Hash(after) != selected.AfterHash) throw new InvalidOperationException("The selected package file changed after review.");
            }
        }

        string summary = PreviewSummary(selected, entry.Promoted);
        if (personal) return new(relative, selected.Action, summary + "\nPersonal or credential-related file: contents are hidden.", null, null);
        if (!supported) return new(relative, selected.Action, summary + "\nBinary or unsupported format: contents are not shown.", null, null);
        if (!small) return new(relative, selected.Action, summary + "\nText preview is limited to 2 MiB per file.", null, null);
        bool profile = Path.GetExtension(relative).Equals(".prf", StringComparison.OrdinalIgnoreCase);
        bool euroScopeText = profile || selected.IsListLayout || IsConventionalLayoutName(relative);
        if (!TryDecodePreview(before ?? [], euroScopeText, out var beforeText, out var beforeEncoding) ||
            !TryDecodePreview(after ?? [], euroScopeText, out var afterText, out var afterEncoding))
            return new(relative, selected.Action, summary + "\nBinary or invalid text encoding: contents are not shown.", null, null);
        if (PreviewLineLimitExceeded(beforeText) || PreviewLineLimitExceeded(afterText))
            return new(relative, selected.Action, summary + "\nText preview is limited to 20,000 lines per file; contents are not shown.", null, null);

        summary += "\nBefore: " + TextMetadata(beforeText, beforeEncoding) + ".\nAfter: " + TextMetadata(afterText, afterEncoding) + ".";
        if (!TrySanitizePreview(beforeText, profile, out var safeBefore, out var hiddenBefore) ||
            !TrySanitizePreview(afterText, profile, out var safeAfter, out var hiddenAfter))
            return new(relative, selected.Action, summary + "\nSensitive content could not be safely separated; contents are hidden.", null, null);
        if (hiddenBefore || hiddenAfter) summary += "\nPersonal and credential-like values are hidden on both sides.";
        if (selected.BeforeHash != selected.AfterHash && safeBefore == safeAfter)
            summary += "\nFile bytes differ, but the difference is in hidden values or encoding.";
        return new(relative, selected.Action, summary, safeBefore, safeAfter);
    }

    private static byte[]? ReadCurrentPreviewBytes(string path, GngUpdateFile selected, bool capture)
    {
        EnsureNoReparse(path);
        if (selected.BeforeHash is null)
        {
            if (File.Exists(path) || Directory.Exists(path))
                throw new InvalidOperationException("The selected destination appeared after review. Create a new installation review.");
            return capture ? [] : null;
        }
        using var parents = new DirectoryGuard(Path.GetDirectoryName(path)!, false);
        using var input = OpenPreviewFile(path);
        if (input.Length != selected.BeforeLength || Hash(input) != selected.BeforeHash)
            throw new InvalidOperationException("The selected file changed after review. Create a new installation review.");
        if (!capture) return null;
        input.Position = 0; using var output = new MemoryStream();
        CopyBounded(input, output, selected.BeforeLength);
        return output.ToArray();
    }

    private static FileStream OpenPreviewFile(string path)
    {
        var handle = CreateFile(ExtendedPath(path), 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw new IOException("The reviewed file could not be opened. Create a new installation review.");
            CheckHandle(handle, false);
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    private static string PreviewSummary(GngUpdateFile file, bool promoted)
    {
        string Number(long size) => size.ToString("N0", CultureInfo.InvariantCulture);
        return (promoted ? "Planned source: promoted DLL from Updated Plugin.\n" : file.MergedBytes is not null ?
                file.IsListLayout ? "Planned source: package settings with saved list positions and whole-list visibility. Columns, items and sorting come from the package.\n" :
                "Planned source: merged profile, including retained personal settings.\n" :
                file.WritesFile ? "Planned source: package file.\n" : "Planned source: retained current file.\n") +
            (file.BeforeHash is null ? "Before: file absent." : $"Before: {Number(file.BeforeLength)} bytes; SHA-256 {file.BeforeHash}.") +
            $"\nAfter: {Number(file.AfterLength)} bytes; SHA-256 {file.AfterHash}.\n" +
            (file.BeforeHash == file.AfterHash ? "File bytes are unchanged." : "File bytes will change.");
    }

    private static bool TryDecodePreview(byte[] bytes, bool euroScopeText, out string text, out string encoding)
    {
        text = ""; encoding = "UTF-8";
        try
        {
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { encoding = "UTF-32 LE with BOM"; text = new UTF32Encoding(false, false, true).GetString(bytes, 4, bytes.Length - 4); }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { encoding = "UTF-32 BE with BOM"; text = new UTF32Encoding(true, false, true).GetString(bytes, 4, bytes.Length - 4); }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = "UTF-16 LE with BOM"; text = new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2); }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = "UTF-16 BE with BOM"; text = new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2); }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = "UTF-8 with BOM"; text = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3); }
            else
            {
                if (euroScopeText) { encoding = "Windows-1252"; text = PrfEncoding.GetString(bytes); }
                else
                {
                    try { text = new UTF8Encoding(false, true).GetString(bytes); }
                    catch (DecoderFallbackException) { encoding = "Windows-1252"; text = PrfEncoding.GetString(bytes); }
                }
            }
            return !text.Any(c => (char.IsControl(c) && c is not '\r' and not '\n' and not '\t') || c == '\uFFFD');
        }
        catch (DecoderFallbackException) { text = ""; return false; }
    }

    private static string TextMetadata(string text, string encoding)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; } else cr++; }
            else if (text[i] == '\n') lf++;
        return $"{encoding}; CRLF {crlf}, LF {lf}, CR {cr}; " +
            (text.Length == 0 ? "empty text" : text[^1] is '\r' or '\n' ? "ends with a line break" : "no final line break");
    }

    private static bool PreviewLineLimitExceeded(string text)
    {
        int count = text.Length == 0 ? 0 : 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (i + 1 < text.Length && ++count > TextPreviewLineLimit) return true;
        }
        return false;
    }

    private static bool TrySanitizePreview(string text, bool profile, out string sanitized, out bool hidden)
    {
        var result = new StringBuilder(); hidden = false;
        foreach (Match match in Regex.Matches(text, @"[^\r\n]*(?:\r\n|\r|\n|$)"))
        {
            var line = match.Value;
            if (line.Length == 0) continue;
            int contentLength = line.TrimEnd('\r', '\n').Length;
            var content = line[..contentLength]; var ending = line[contentLength..];
            var fields = content.Split('\t', 3);
            if (profile && fields.Length >= 1 && (fields[0].Trim().Equals("LastSession", StringComparison.OrdinalIgnoreCase) || fields[0].Trim().Equals("TeamSpeakVccs", StringComparison.OrdinalIgnoreCase)))
            {
                result.Append(fields.Length == 3 ? fields[0] + "\t" + fields[1] + "\t" + HiddenValue : HiddenValue).Append(ending);
                hidden = true; continue;
            }
            if (CredentialLabel.IsMatch(content) || SensitiveUrl.IsMatch(content))
            {
                // Wrapped/multiline secret syntax has no reliable boundary in these text formats.
                if (content.TrimEnd().EndsWith('\\') || content.Count(c => c == '"') % 2 != 0 || content.Count(c => c == '\'') % 2 != 0 ||
                    content.TrimEnd().EndsWith('=') || content.TrimEnd().EndsWith(':') ||
                    content.TrimEnd().EndsWith('|') || content.TrimEnd().EndsWith('>') ||
                    content.IndexOfAny(['=', ':', '\t']) < 0 || content.Contains('<') ||
                    content.Contains('[') || content.Contains('{') || content.Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase))
                { sanitized = ""; return false; }
                result.Append(HiddenValue).Append(ending); hidden = true;
            }
            else if (content.Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase))
            { sanitized = ""; return false; }
            else result.Append(line);
        }
        sanitized = result.ToString(); return true;
    }
}
