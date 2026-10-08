using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace VatscaUpdateChecker.Services;

public sealed record GngCachedPackages(string? PackagePath, string? FullPackagePath, string Version);

/// <summary>Finds previously downloaded originals after checking AeroNav's public package version.</summary>
public static class GngPackageCache
{
    private const int MaximumHtmlBytes = 2 * 1024 * 1024;
    private const int MaximumScanEntries = 4096;
    private const int MaximumCandidates = 128;
    private const long MaximumValidationBytes = 4L * 1024 * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false
    }) { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Regex NamePattern = Pattern(@"\AESAA-(Full-Package|Update-Only)_([0-9]{14})-([0-9]{4})([0-9]{2})-([0-9]{4})(?: \([0-9]+\))?\.zip\z");
    private static readonly Regex TransactionPattern = Pattern(@"\A[0-9]{8}-[0-9]{6}-[0-9a-f]{32}\z");
    private static readonly Regex InstallationPattern = Pattern(@"\A[0-9a-f]{64}\z");
    private static readonly Regex TablePattern = Pattern(@"<table\b[^>]*>(.*?)</table\s*>");
    private static readonly Regex RowPattern = Pattern(@"<tr\b[^>]*>(.*?)</tr\s*>");
    private static readonly Regex CellPattern = Pattern(@"<t[dh]\b[^>]*>");
    private static readonly Regex TagPattern = Pattern(@"<[^>]*>");
    private static readonly Regex WhitespacePattern = Pattern(@"\s+");
    private static readonly Regex AiracPattern = Pattern(@"\A([0-9]{4})\s*/\s*([0-9]{2})\z");
    private static readonly Regex RevisionPattern = Pattern(@"\A[0-9]{1,4}\z");
    private sealed record Candidate(string Path, GngPackageKind Kind, string Timestamp, DateTime WrittenUtc, string Version, long Length);
    private sealed record Published(GngPackageKind Kind, string Airac, int Revision)
    {
        internal string Version => Airac[..4] + "/" + Airac[4..] + " rev." + Revision.ToString(CultureInfo.InvariantCulture);
    }

    public static Task<GngCachedPackages?> TryFindAsync(GngPackageKind requestedKind, CancellationToken cancellationToken = default) =>
        TryFindAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VatscaUpdateChecker", "Gng"),
            requestedKind, ReadPublicPageAsync, cancellationToken);

    internal static async Task<GngCachedPackages?> TryFindAsync(string storageRoot, GngPackageKind requestedKind,
        Func<CancellationToken, Task<string>> readPublicPage, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string html = await readPublicPage(cancellationToken).ConfigureAwait(false);
            return await Task.Run(() => Find(storageRoot, requestedKind, html, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsUnavailable(error)) { return null; }
    }

    private static GngCachedPackages? Find(string storageRoot, GngPackageKind kind, string html, CancellationToken token)
    {
        if (kind is not (GngPackageKind.Full or GngPackageKind.UpdateOnly) || html.Length > MaximumHtmlBytes) return null;
        var published = ReadPublished(html);
        var target = Latest(published, kind);
        if (target is null) return null;
        if (kind == GngPackageKind.UpdateOnly && published.Count(p => p.Kind == GngPackageKind.Full && p.Version == target.Version) != 1) return null;
        if (!Path.IsPathFullyQualified(storageRoot) || !Directory.Exists(storageRoot)) return null;
        string root = Path.GetFullPath(storageRoot);
        RequireUnredirected(root);
        var candidates = Scan(root, target.Version, token)
            .OrderByDescending(c => c.Timestamp, StringComparer.Ordinal).ThenByDescending(c => c.WrittenUtc)
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
        long remainingBytes = MaximumValidationBytes;
        string? Select(GngPackageKind requested)
        {
            int attempts = 0;
            foreach (var candidate in candidates.Where(c => c.Kind == requested))
            {
                token.ThrowIfCancellationRequested();
                if (++attempts > 4 || candidate.Length > remainingBytes) return null;
                remainingBytes -= candidate.Length;
                try
                {
                    RequireUnredirected(candidate.Path);
                    if (GngUpdateService.ValidatePackage(candidate.Path) != target.Version) continue;
                    token.ThrowIfCancellationRequested();
                    return candidate.Path;
                }
                catch (Exception error) when (error is not OperationCanceledException && IsUnavailable(error)) { }
            }
            return null;
        }
        // Return each reusable original separately so the caller can download just the missing part.
        // A Full ZIP never substitutes for the explicitly requested Update Only primary package.
        string? package = Select(kind);
        string? reference = kind == GngPackageKind.UpdateOnly ? Select(GngPackageKind.Full) : null;
        return package is null && reference is null ? null : new(package, reference, target.Version);
    }

    private static List<Candidate> Scan(string root, string requiredVersion, CancellationToken token)
    {
        int scanned = 0;
        var result = new List<Candidate>();
        IEnumerable<string> Children(string path)
        {
            if (!Directory.Exists(path)) yield break;
            RequireUnredirected(path);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                token.ThrowIfCancellationRequested();
                if (++scanned > MaximumScanEntries) throw new InvalidDataException("Package cache scan limit reached.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) == 0) yield return entry;
            }
        }
        void Archives(string directory)
        {
            foreach (var file in Children(directory))
            {
                if (!File.Exists(file)) continue;
                var name = NamePattern.Match(Path.GetFileName(file));
                if (!name.Success || !DateTime.TryParseExact(name.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) continue;
                string airac = name.Groups[3].Value + name.Groups[4].Value;
                if (!ValidAirac(airac)) continue;
                var kind = name.Groups[1].Value.Equals("Full-Package", StringComparison.OrdinalIgnoreCase) ? GngPackageKind.Full : GngPackageKind.UpdateOnly;
                var published = new Published(kind, airac, int.Parse(name.Groups[5].Value, CultureInfo.InvariantCulture));
                if (published.Version != requiredVersion) continue;
                var info = new FileInfo(file);
                if (info.Length <= 0 || info.Length > GngBrowserPolicy.MaximumDownloadBytes) continue;
                if (result.Count >= MaximumCandidates) throw new InvalidDataException("Package cache candidate limit reached.");
                result.Add(new(file, kind, name.Groups[2].Value, info.LastWriteTimeUtc, published.Version, info.Length));
            }
        }
        foreach (var download in Children(Path.Combine(root, "Downloads")))
            if (Directory.Exists(download) && Guid.TryParseExact(Path.GetFileName(download), "N", out _)) Archives(download);
        foreach (var installation in Children(Path.Combine(root, "Installations")))
        {
            if (!Directory.Exists(installation) || !InstallationPattern.IsMatch(Path.GetFileName(installation))) continue;
            foreach (var transaction in Children(installation))
            {
                if (!Directory.Exists(transaction) || !TransactionPattern.IsMatch(Path.GetFileName(transaction))) continue;
                Archives(Path.Combine(transaction, "package"));
                Archives(Path.Combine(transaction, "complete-package"));
            }
        }
        return result;
    }

    private static List<Published> ReadPublished(string html)
    {
        var tables = TablePattern.Matches(html);
        if (tables.Count > 64) return [];
        List<Published>? found = null;
        foreach (Match table in tables)
        {
            var rows = RowPattern.Matches(table.Groups[1].Value);
            if (rows.Count == 0) continue;
            var headers = Cells(rows[0].Groups[1].Value);
            if (!headers.SequenceEqual(new[] { "Client", "Packagename", "AIRAC", "Version", "Released", "Download" }, StringComparer.OrdinalIgnoreCase)) continue;
            if (found is not null || rows.Count > 10001) return [];
            found = [];
            foreach (Match row in rows.Cast<Match>().Skip(1))
            {
                var cells = Cells(row.Groups[1].Value);
                if (cells.Count != 7) return [];
                if (!cells[0].Equals("ES", StringComparison.OrdinalIgnoreCase)) continue;
                GngPackageKind kind;
                if (cells[1].Equals("ESAA Full_Package", StringComparison.OrdinalIgnoreCase)) kind = GngPackageKind.Full;
                else if (cells[1].Equals("ESAA Update_Only", StringComparison.OrdinalIgnoreCase)) kind = GngPackageKind.UpdateOnly;
                else continue;
                var airac = AiracPattern.Match(cells[2]);
                if (!airac.Success || !ValidAirac(airac.Groups[1].Value + airac.Groups[2].Value)
                    || !RevisionPattern.IsMatch(cells[3])
                    || !DateTime.TryParseExact(cells[4], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return [];
                found.Add(new(kind, airac.Groups[1].Value + airac.Groups[2].Value, int.Parse(cells[3], CultureInfo.InvariantCulture)));
            }
        }
        return found ?? [];
    }

    private static List<string> Cells(string row)
    {
        var starts = CellPattern.Matches(row);
        var values = new List<string>();
        for (int index = 0; index < starts.Count; index++)
        {
            int start = starts[index].Index + starts[index].Length;
            int end = index + 1 < starts.Count ? starts[index + 1].Index : row.Length;
            string value = TagPattern.Replace(row[start..end], "");
            value = WhitespacePattern.Replace(WebUtility.HtmlDecode(value), " ").Trim();
            if (value.Length > 128) return [];
            values.Add(value);
        }
        return values;
    }

    private static Published? Latest(List<Published> rows, GngPackageKind kind)
    {
        var candidates = rows.Where(row => row.Kind == kind).OrderByDescending(row => row.Airac, StringComparer.Ordinal).ThenByDescending(row => row.Revision).ToArray();
        return candidates.Length == 0 || candidates.Length > 1 && candidates[0].Version == candidates[1].Version ? null : candidates[0];
    }
    private static bool ValidAirac(string airac) => int.Parse(airac.Substring(2, 2), CultureInfo.InvariantCulture) is >= 1 and <= 14
        && int.Parse(airac[4..], CultureInfo.InvariantCulture) >= 1;
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(2));

    private static void RequireUnredirected(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected cache paths are not used.");
    }
    private static bool IsUnavailable(Exception error) => error is IOException or InvalidDataException or InvalidOperationException
        or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException
        or HttpRequestException or OperationCanceledException or RegexMatchTimeoutException;

    private static async Task<string> ReadPublicPageAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, GngBrowserPolicy.AeroNavHome);
        request.Headers.UserAgent.ParseAdd("SwedenFirLaunchpad/2.0");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumHtmlBytes) throw new InvalidDataException("Public package page is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (data.Length + read > MaximumHtmlBytes) throw new InvalidDataException("Public package page is too large.");
            data.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(data.ToArray());
    }
}
