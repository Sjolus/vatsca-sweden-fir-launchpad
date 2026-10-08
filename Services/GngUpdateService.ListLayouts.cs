using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public static partial class GngUpdateService
{
    private const int ListLayoutByteLimit = 2 * 1024 * 1024;
    private const int ListLayoutLineLimit = 20_000;
    private static readonly HashSet<string> NativeListGroups = new(StringComparer.Ordinal)
        { "SIL", "SEL", "DEP", "ARR", "FP", "CONFLICT", "PILOTING", "ORIGPLANE", "STUP", "TAXIOUT", "TAKEOFF", "ADCS", "TAXIIN" };
    private sealed record LayoutText(string Text, Encoding Encoding, byte[] Preamble);
    private sealed record LayoutField(int Start, int Length, int Value);
    private sealed record ListLayout(string Group, string Identity, IReadOnlyDictionary<string, LayoutField> Fields);
    private sealed record LayoutDocument(LayoutText Source, HashSet<string> Sections, Dictionary<string, ListLayout> Lists);
    private sealed record LayoutSource(GngUpdateDependency Snapshot, LayoutText? Text);
    private sealed record LayoutRoute(LayoutDocument? Source, GngUpdateDependency Profile, GngUpdateDependency? File);

    private static void ApplyListLayouts(string root, Package package, ZipArchive archive, List<GngUpdateFile> files,
        IReadOnlyList<string> protections, List<GngUpdateDependency> dependencies)
    {
        var byPath = files.ToDictionary(f => f.RelativePath, Paths);
        var packagePaths = new Dictionary<string, string>(Paths);
        foreach (var file in files)
            if (!packagePaths.TryAdd(CanonicalPath(Contained(root, file.RelativePath)), file.RelativePath))
                throw new InvalidOperationException("Two package paths identify the same installed file. Review the data folder before updating.");
        var local = new Dictionary<string, LayoutSource>(Paths);
        var localDocuments = new Dictionary<string, LayoutDocument>(Paths);
        var incoming = new Dictionary<string, LayoutDocument?>(Paths);
        var routes = new Dictionary<string, Dictionary<string, List<LayoutRoute>>>(Paths);
        long retainedTextSize = 0;

        void Retain(LayoutText text, string label)
        {
            retainedTextSize += text.Text.Length * 2L;
            if (retainedTextSize > 32 * 1024 * 1024) throw LayoutError(label, "the combined profile and list settings text exceeds the 32 MiB limit");
        }

        LayoutSource Local(string path, bool required, string label)
        {
            path = CanonicalPath(path);
            RequireLayoutPath(root, path, protections, label);
            if (!local.TryGetValue(path, out var source))
            {
                if (Directory.Exists(path)) throw LayoutError(label, "a settings file path names a folder");
                if (!File.Exists(path)) source = new(new(path, null, 0), null);
                else
                {
                    using var parents = new DirectoryGuard(Path.GetDirectoryName(path)!, false);
                    using var input = OpenPreviewFile(path);
                    if (input.Length > ListLayoutByteLimit) throw LayoutError(label, "a profile or list settings file exceeds the 2 MiB limit");
                    using var output = new MemoryStream(); CopyBounded(input, output, input.Length);
                    var bytes = output.ToArray();
                    source = new(new(path, Hash(bytes), bytes.Length), DecodeLayoutText(bytes, label));
                    Retain(source.Text!, label);
                }
                local.Add(path, source);
                if (packagePaths.TryGetValue(path, out var relative))
                {
                    var planned = byPath[relative];
                    if (planned.BeforeHash != source.Snapshot.Hash || planned.BeforeLength != source.Snapshot.Length)
                        throw LayoutError(label, "a source file changed while the review was being built");
                }
            }
            if (required && source.Text is null) throw LayoutError(label, "the active settings file is missing");
            return source;
        }

        LayoutDocument? Incoming(string relative, bool required)
        {
            if (!incoming.TryGetValue(relative, out var document))
            {
                var entry = package.Entries[relative];
                document = null;
                if (entry.Length <= ListLayoutByteLimit)
                {
                    LayoutText? decoded = null;
                    try { decoded = ReadPackageLayoutText(archive, entry); }
                    catch (InvalidOperationException) when (!required && !IsConventionalLayoutName(relative)) { }
                    if (decoded is not null)
                    {
                        var parsed = ParseLayout(decoded, relative);
                        if (parsed.Sections.Count > 0) { document = parsed; Retain(decoded, relative); }
                    }
                }
                else if (required || IsConventionalLayoutName(relative)) throw LayoutError(relative, "the package settings file exceeds the 2 MiB limit");
                incoming.Add(relative, document);
            }
            if (required && (document is null || document.Sections.Count == 0))
                throw LayoutError(relative, "the package settings file has no recognized list settings section");
            return document;
        }

        LayoutDocument LocalDocument(LayoutSource source, string label)
        {
            if (!localDocuments.TryGetValue(source.Snapshot.Path, out var document))
                localDocuments.Add(source.Snapshot.Path, document = ParseLayout(source.Text!, label));
            return document;
        }

        foreach (var entry in package.Entries.Values.Where(e => Path.GetExtension(e.Path).Equals(".prf", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path, Paths))
        {
            var newRoutes = ProfileLayoutRoutes(ReadPackageLayoutText(archive, entry).Text, entry.Path);
            if (newRoutes.Count == 0) continue;
            var profile = Local(Contained(root, entry.Path), false, entry.Path);
            var oldRoutes = profile.Text is null ? null : ProfileLayoutRoutes(profile.Text.Text, entry.Path);
            foreach (var route in newRoutes)
            {
                var target = ResolveLayoutRoute(root, route.Value, protections, entry.Path);
                if (!packagePaths.TryGetValue(target, out var relative) || IsPersonal(relative))
                    throw LayoutError(entry.Path, "the new " + route.Key + " route does not identify an installable file in this package");
                var next = Incoming(relative, true)!;
                if (!next.Sections.Contains(route.Key)) throw LayoutError(relative, "the package file is missing its routed " + route.Key + " section");
                if (!routes.TryGetValue(relative, out var groups)) routes.Add(relative, groups = new(StringComparer.Ordinal));
                if (!groups.TryGetValue(route.Key, out var sources)) groups.Add(route.Key, sources = []);
                LayoutDocument? previous = null; GngUpdateDependency? sourceSnapshot = null;
                if (oldRoutes is not null && oldRoutes.TryGetValue(route.Key, out var sourceRoute))
                {
                    var sourcePath = ResolveLayoutRoute(root, sourceRoute, protections, entry.Path);
                    var source = Local(sourcePath, true, entry.Path);
                    previous = LocalDocument(source, Path.GetRelativePath(root, sourcePath));
                    if (!previous.Sections.Contains(route.Key)) throw LayoutError(entry.Path, "the active settings file is missing its " + route.Key + " section");
                    sourceSnapshot = source.Snapshot;
                }
                sources.Add(new(previous, profile.Snapshot, sourceSnapshot));
            }
        }

        foreach (var file in files.ToArray())
        {
            bool routed = routes.TryGetValue(file.RelativePath, out var groups);
            if (IsPersonal(file.RelativePath) || (!routed && !Path.GetExtension(file.RelativePath).Equals(".txt", StringComparison.OrdinalIgnoreCase))) continue;
            var next = Incoming(file.RelativePath, routed);
            if (next is null) continue;
            var used = new Dictionary<string, GngUpdateDependency>(Paths);
            LayoutDocument? fallback = null;
            if (routed)
            {
                foreach (var source in groups!.Values.SelectMany(g => g))
                {
                    AddLayoutDependency(used, source.Profile);
                    if (source.File is not null) AddLayoutDependency(used, source.File);
                }
            }
            else
            {
                var source = Local(Contained(root, file.RelativePath), false, file.RelativePath);
                AddLayoutDependency(used, source.Snapshot);
                if (source.Text is not null) fallback = LocalDocument(source, file.RelativePath);
            }
            var changes = new List<(LayoutField Field, int Value)>();
            int preserved = 0;
            foreach (var list in next.Lists.Values)
            {
                var candidates = routed
                    ? groups!.TryGetValue(list.Group, out var selected) ? selected.Select(r => r.Source) : []
                    : new[] { fallback };
                ListLayout? chosen = null;
                foreach (var candidate in candidates)
                {
                    if (candidate is null || !candidate.Lists.TryGetValue(list.Identity, out var current)) continue;
                    if (chosen is not null && chosen.Fields.Any(p => p.Value.Value != current.Fields[p.Key].Value))
                        throw LayoutError(file.RelativePath, "profiles use conflicting positions or visibility for the same list");
                    chosen = current;
                }
                if (chosen is null) continue;
                preserved++;
                foreach (var field in list.Fields) changes.Add((field.Value, chosen.Fields[field.Key].Value));
            }
            byte[]? merged = preserved == 0 ? null : EncodeLayout(next.Source, changes);
            string action = file.Action, detail = file.Detail;
            if (merged is not null)
            {
                action = file.BeforeHash == Hash(merged) ? "Unchanged" : "Merge list layout";
                detail = action == "Unchanged" ? "Already matches the planned result; list positions and visibility are kept." :
                    "Keep list positions and whether each list is shown. Refresh package columns, items, sorting and other settings.";
            }
            var replacement = file with { Action = action, Detail = detail, MergedBytes = merged ?? file.MergedBytes,
                AfterHash = merged is null ? file.AfterHash : Hash(merged), AfterLength = merged?.LongLength ?? file.AfterLength,
                Dependencies = used.Values.OrderBy(d => d.Path, Paths).ToArray(), IsListLayout = true };
            files[files.IndexOf(file)] = replacement;
            foreach (var dependency in used.Values)
            {
                var existing = dependencies.FirstOrDefault(d => Paths.Equals(d.Path, dependency.Path));
                if (existing is not null && existing != dependency) throw LayoutError(file.RelativePath, "a shared source changed during review");
                if (existing is null) dependencies.Add(dependency);
            }
        }
        dependencies.Sort((a, b) => Paths.Compare(a.Path, b.Path));
    }

    private static Dictionary<string, string> ProfileLayoutRoutes(string text, string label)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in LayoutLines(text, label))
        {
            var parts = text.Substring(line.Start, line.Length).Split('\t');
            if (parts.Length < 2 || !parts[0].Equals("Settings", StringComparison.OrdinalIgnoreCase) || !parts[1].StartsWith("Settingsfile", StringComparison.OrdinalIgnoreCase)) continue;
            var group = parts[1]["Settingsfile".Length..].ToUpperInvariant();
            if (group != "PLUGINS" && !NativeListGroups.Contains(group)) continue;
            if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[2]) || !result.TryAdd(group, parts[2]))
                throw LayoutError(label, "a list settings route is empty, duplicated or malformed");
        }
        return result;
    }

    private static string ResolveLayoutRoute(string root, string value, IReadOnlyList<string> protections, string label)
    {
        try
        {
            value = value.Trim().Replace('/', '\\');
            if (value.StartsWith('"') && value.EndsWith('"') && value.Length > 1) value = value[1..^1];
            if (value.StartsWith('\\') && !value.StartsWith("\\\\", StringComparison.Ordinal)) value = value[1..];
            if (value.StartsWith(@".\", StringComparison.Ordinal)) value = value[2..];
            string path;
            if (Path.IsPathFullyQualified(value))
            {
                path = CanonicalPath(value);
                if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
                _ = Normalize(Path.GetRelativePath(root, value));
            }
            else path = CanonicalPath(Contained(root, value));
            RequireLayoutPath(root, path, protections, label);
            return path;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
        { throw LayoutError(label, "a list settings route is unsafe, outside the reviewed folder or overlaps a protected path"); }
    }

    private static void RequireLayoutPath(string root, string path, IReadOnlyList<string> protections, string label)
    {
        if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw LayoutError(label, "a settings source is outside the reviewed folder");
        RequireUnprotected(path, protections);
    }

    private static LayoutText ReadPackageLayoutText(ZipArchive archive, PackageEntry entry)
    {
        if (entry.Length > ListLayoutByteLimit) throw LayoutError(entry.Path, "a profile or list settings file exceeds the 2 MiB limit");
        using var input = archive.GetEntry(entry.ZipName)!.Open(); using var output = new MemoryStream();
        CopyBounded(input, output, entry.Length);
        var bytes = output.ToArray();
        if (Hash(bytes) != entry.Hash) throw LayoutError(entry.Path, "the package source changed during review");
        return DecodeLayoutText(bytes, entry.Path);
    }

    private static LayoutText DecodeLayoutText(byte[] bytes, string label)
    {
        Encoding encoding = PrfEncoding; int offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, false, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = new UTF8Encoding(false, true); offset = 3; }
        try
        {
            var text = encoding.GetString(bytes, offset, bytes.Length - offset);
            if (text.Any(c => (char.IsControl(c) && c is not '\r' and not '\n' and not '\t') || c == '\uFFFD')) throw LayoutError(label, "the settings file is not supported text");
            _ = LayoutLines(text, label);
            return new(text, encoding, bytes[..offset]);
        }
        catch (DecoderFallbackException) { throw LayoutError(label, "the settings file has invalid text encoding"); }
    }

    private static List<(int Start, int Length)> LayoutLines(string text, string label)
    {
        var result = new List<(int, int)>(); int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not '\r' and not '\n') continue;
            if (result.Count == ListLayoutLineLimit) throw LayoutError(label, "the settings file exceeds the 20,000 line limit");
            result.Add((start, i - start));
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start < text.Length)
        {
            if (result.Count == ListLayoutLineLimit) throw LayoutError(label, "the settings file exceeds the 20,000 line limit");
            result.Add((start, text.Length - start));
        }
        return result;
    }

    private static LayoutDocument ParseLayout(LayoutText source, string label)
    {
        var sections = new HashSet<string>(StringComparer.Ordinal);
        var fields = new Dictionary<string, (string Group, Dictionary<string, LayoutField> Fields)>(StringComparer.Ordinal);
        string? section = null;
        foreach (var span in LayoutLines(source.Text, label))
        {
            var line = source.Text.Substring(span.Start, span.Length);
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            if (trimmed == "END") { section = null; continue; }
            if (NativeListGroups.Contains(trimmed) || trimmed == "PLUGINS")
            {
                if (section is not null || !sections.Add(trimmed)) throw LayoutError(label, "a list settings section is duplicated or not closed");
                section = trimmed; continue;
            }
            if (section is null) continue;
            string identity = section, field; int valueStart;
            if (section == "PLUGINS")
            {
                const string prefix = "TopSky plugin:ACList/";
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                int slash = line.LastIndexOf('/'); int colon = line.IndexOf(':', slash + 1);
                if (slash < prefix.Length || colon < 0) continue;
                identity = line[..slash]; field = line[(slash + 1)..colon]; valueStart = colon + 1;
            }
            else
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                field = line[..colon].Trim(); valueStart = colon + 1;
            }
            if (field is not "m_X" and not "m_Y" and not "m_Visible") continue;
            var raw = line[valueStart..]; var valueText = raw.Trim();
            bool integer = valueText.Length > 0 && valueText.Where((c, i) => i != 0 || c is not '+' and not '-').All(c => c is >= '0' and <= '9');
            if (!integer || !int.TryParse(valueText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ||
                (field == "m_Visible" && valueText is not "0" and not "1")) throw LayoutError(label, "a list position or visibility value is invalid");
            if (!fields.TryGetValue(identity, out var list)) fields.Add(identity, list = (section, new(StringComparer.Ordinal)));
            if (!list.Fields.TryAdd(field, new(span.Start + valueStart + raw.Length - raw.TrimStart().Length, valueText.Length, value)))
                throw LayoutError(label, "a list position or visibility field is duplicated");
        }
        if (section is not null) throw LayoutError(label, "a list settings section has no END marker");
        foreach (var group in sections.Where(NativeListGroups.Contains))
            if (!fields.ContainsKey(group)) throw LayoutError(label, "a native list section has no position and visibility values");
        if (fields.Values.Any(v => v.Fields.Count != 3)) throw LayoutError(label, "a list has incomplete position and visibility values");
        return new(source, sections, fields.ToDictionary(p => p.Key, p => new ListLayout(p.Value.Group, p.Key, p.Value.Fields), StringComparer.Ordinal));
    }

    private static byte[] EncodeLayout(LayoutText source, IEnumerable<(LayoutField Field, int Value)> changes)
    {
        var result = new StringBuilder(source.Text.Length); int position = 0;
        foreach (var change in changes.OrderBy(c => c.Field.Start))
        {
            result.Append(source.Text, position, change.Field.Start - position);
            result.Append(change.Value.ToString(CultureInfo.InvariantCulture));
            position = change.Field.Start + change.Field.Length;
        }
        result.Append(source.Text, position, source.Text.Length - position);
        return [.. source.Preamble, .. source.Encoding.GetBytes(result.ToString())];
    }

    private static bool IsConventionalLayoutName(string path) => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) &&
        (Path.GetFileName(path).StartsWith("Lists", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).StartsWith("Plugin", StringComparison.OrdinalIgnoreCase));
    private static InvalidOperationException LayoutError(string label, string reason) => new("Cannot preserve list layout in " + label + ": " + reason +
        ". No files were changed. Correct the settings or clear 'Keep my list positions and visibility' to use package defaults.");
    private static void AddLayoutDependency(Dictionary<string, GngUpdateDependency> result, GngUpdateDependency dependency)
    {
        if (result.TryGetValue(dependency.Path, out var old) && old != dependency) throw new InvalidOperationException("A list settings source changed during review.");
        result[dependency.Path] = dependency;
    }

    private static IDisposable GuardPreviewDependencies(GngUpdatePlan plan, GngUpdateFile selected)
    {
        var root = ExistingDirectory(plan.DataFolder);
        foreach (var dependency in selected.Dependencies)
        {
            var path = CanonicalPath(dependency.Path);
            RequireLayoutPath(root, path, plan.ProtectedPaths, selected.RelativePath);
            if (!Paths.Equals(path, dependency.Path) || dependency.Length is < 0 or > ListLayoutByteLimit ||
                (dependency.Hash is not null && !ValidHash(dependency.Hash)))
                throw new InvalidOperationException("The list settings review is incomplete. Create a new installation review.");
            if (dependency.Hash is null && (dependency.Length != 0 || File.Exists(path) || Directory.Exists(path)))
                throw new InvalidOperationException("A list settings source appeared after review. Create a new installation review.");
        }
        return new DependencyGuard(selected.Dependencies.Where(d => d.Hash is not null));
    }
}
