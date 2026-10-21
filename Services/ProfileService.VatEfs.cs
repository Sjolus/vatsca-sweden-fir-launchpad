using System.IO;
using System.Text.RegularExpressions;

namespace VatscaUpdateChecker.Services;

public static partial class ProfileService
{
    private sealed record VatEfsLine(string Text, string Ending);
    private static readonly Regex VatEfsSlot = new(@"^Plugin(\d+)(.*)$", RegexOptions.CultureInvariant);

    // Shared by profile-only toggles and the legacy full controller apply. Never reads credentials.
    internal static string TransformVatEfs(string text, string dllPath, bool enabled)
    {
        var lines = Regex.Matches(text, @"([^\r\n]*)(\r\n|\r|\n|$)").Cast<Match>()
            .Where(match => match.Length > 0).Select(match => new VatEfsLine(match.Groups[1].Value, match.Groups[2].Value)).ToList();
        string newline = lines.FirstOrDefault(line => line.Ending.Length > 0)?.Ending ?? "\r\n";
        bool finalNewline = text.EndsWith('\n') || text.EndsWith('\r');
        var slots = new List<(int Number, int Line, string Path)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        int lastPluginAt = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var parts = lines[i].Text.Split('\t', 3);
            if (parts.Length != 3 || parts[0] != "Plugins") continue;
            lastPluginAt = i;
            var match = VatEfsSlot.Match(parts[1]);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, out int slot) || slot > 1_000_000 || !keys.Add(parts[1]))
                throw new InvalidDataException("The EuroScope profile contains ambiguous plugin entries. Check its plugin list before changing VatEFS.");
            if (match.Groups[2].Value.Length == 0) slots.Add((slot, i, parts[2]));
        }
        var vat = slots.Where(slot => IsVatEfsPluginPath(slot.Path.Trim('"'))).ToArray();
        if (!enabled)
        {
            if (vat.Length == 0) return text;
            var removed = vat.Select(slot => slot.Number).ToHashSet();
            var output = new List<VatEfsLine>();
            foreach (var line in lines)
            {
                var parts = line.Text.Split('\t', 3);
                var match = parts.Length == 3 && parts[0] == "Plugins" ? VatEfsSlot.Match(parts[1]) : Match.Empty;
                if (!match.Success) { output.Add(line); continue; }
                int number = int.Parse(match.Groups[1].Value);
                if (removed.Contains(number)) continue;
                int next = number - removed.Count(slot => slot < number);
                output.Add(next == number ? line : line with { Text = "Plugins\tPlugin" + next + match.Groups[2].Value + "\t" + parts[2] });
            }
            if (output.Count > 0 && !finalNewline) output[^1] = output[^1] with { Ending = "" };
            return string.Concat(output.Select(line => line.Text + line.Ending));
        }
        if (vat.Length > 1)
            throw new InvalidDataException("More than one VatEFS plugin entry was found. Disable VatEFS first, then enable it again.");
        var screens = DiscoverDisplayScreens(lines.Select(line => line.Text));
        if (screens.Count > 1024) throw new InvalidDataException("The EuroScope profile contains too many display names to change safely.");
        if (vat.Length == 0)
        {
            int slot = MaxPluginIndex(lines.Select(line => line.Text)) + 1;
            var block = new List<string> { "Plugins\tPlugin" + slot + "\t" + dllPath };
            for (int i = 0; i < screens.Count; i++) block.Add("Plugins\tPlugin" + slot + "Display" + i + "\t" + screens[i]);
            Insert(lastPluginAt >= 0 ? lastPluginAt + 1 : lines.Count, block);
        }
        else
        {
            var existing = vat[0];
            if (!string.Equals(existing.Path, dllPath, StringComparison.OrdinalIgnoreCase))
                lines[existing.Line] = lines[existing.Line] with { Text = "Plugins\tPlugin" + existing.Number + "\t" + dllPath };
            var names = new HashSet<string>(StringComparer.Ordinal);
            int maxDisplay = -1, last = existing.Line;
            for (int i = 0; i < lines.Count; i++)
            {
                var parts = lines[i].Text.Split('\t', 3);
                if (parts.Length != 3 || parts[0] != "Plugins") continue;
                var match = Regex.Match(parts[1], "^Plugin" + existing.Number + @"Display(\d+)$");
                if (!match.Success) continue;
                if (!int.TryParse(match.Groups[1].Value, out int display) || display > 1_000_000)
                    throw new InvalidDataException("The VatEFS display entries could not be safely read.");
                names.Add(parts[2]); maxDisplay = Math.Max(maxDisplay, display); last = Math.Max(last, i);
            }
            var missing = screens.Where(screen => !names.Contains(screen))
                .Select(screen => "Plugins\tPlugin" + existing.Number + "Display" + (++maxDisplay) + "\t" + screen).ToArray();
            Insert(last + 1, missing);
        }
        return string.Concat(lines.Select(line => line.Text + line.Ending));

        void Insert(int index, IEnumerable<string> added)
        {
            var block = added.Select(line => new VatEfsLine(line, newline)).ToList();
            if (block.Count == 0) return;
            if (index == lines.Count && !finalNewline)
            {
                if (lines.Count > 0) lines[^1] = lines[^1] with { Ending = newline };
                block[^1] = block[^1] with { Ending = "" };
            }
            lines.InsertRange(index, block);
        }
    }
}
