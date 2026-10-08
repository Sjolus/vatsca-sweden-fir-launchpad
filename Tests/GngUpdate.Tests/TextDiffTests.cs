using System.Text.RegularExpressions;
using VatscaUpdateChecker.Services;

internal static class TextDiffTests
{
    internal static void Run(Action<string, Action<Fixture>> test)
    {
        test("text diff shows additions removals and separated context hunks", _ =>
        {
            var before = Enumerable.Range(1, 30).Select(i => "line " + i).ToArray();
            var after = before.ToArray();
            after[3] = "new first value";
            after[25] = "new second value";
            var result = GngTextDiff.Create(string.Join('\n', before), string.Join('\n', after));
            Check(Regex.Matches(result.Text, "(?m)^@@ ").Count == 2, "Separated changes lost their context hunks");
            Check(result.Text.Contains("-line 4") && result.Text.Contains("+new first value"), "Change direction is unclear");
            Check(ApplyHunks(before, result.Text).SequenceEqual(after), "Hunks do not describe the planned content");
        });
        test("text diff reconstructs additions deletions and repeated-line edits", _ =>
        {
            var random = new Random(1832);
            for (var sample = 0; sample < 160; sample++)
            {
                string[] Lines() => Enumerable.Range(0, random.Next(0, 22)).Select(_ => "line " + random.Next(0, 5)).ToArray();
                var before = Lines();
                var after = Lines();
                if (before.SequenceEqual(after)) continue;
                var result = GngTextDiff.Create(string.Join('\n', before), string.Join('\n', after));
                Check(ApplyHunks(before, result.Text).SequenceEqual(after), "Repeated-line diff does not reconstruct the after text");
            }
        });
        test("text diff preserves Swedish text tabs and whitespace changes", _ =>
        {
            var result = GngTextDiff.Create("ÅÄÖ\told  \nunchanged\n", "ÅÄÖ\tnew \nunchanged\n");
            Check(result.Text.Contains("-ÅÄÖ\told  ") && result.Text.Contains("+ÅÄÖ\tnew "), "Whitespace or Swedish characters were lost");
        });
        test("text diff identifies newline-only and identical sanitized views", _ =>
        {
            var endings = GngTextDiff.Create("first\r\nsecond\r\n", "first\nsecond\n");
            Check(endings.Summary.Contains("line endings"), "Line ending changes appear identical");
            var final = GngTextDiff.Create("first", "first\n");
            Check(final.Summary.Contains("final newline"), "Final newline changes disappear");
            var identical = GngTextDiff.Create("masked value", "masked value");
            Check(identical.Summary.Contains("No visible text changes") && identical.Summary.Contains("Sensitive values"), "Identical sanitized text wrongly claims identical file bytes");
        });
        test("text diff handles a small change in a long file without full replacement", _ =>
        {
            var before = Enumerable.Range(0, 15_000).Select(i => "line " + i).ToArray();
            var after = before.ToArray();
            after[7_500] = "changed";
            var result = GngTextDiff.Create(string.Join('\n', before), string.Join('\n', after));
            Check(result.Text.Length < 400 && result.Text.Contains("+changed"), "Small edit in long file was expanded or refused");
            Check(ApplyHunks(before, result.Text).SequenceEqual(after), "Long-file hunk positions are wrong");
        });
        test("text diff bounds line counts edit search and rendered output with an explicit fallback", _ =>
        {
            foreach (var (before, after) in new[]
            {
                (string.Join('\n', Enumerable.Repeat("old", 20_001)), "new"),
                (string.Join('\n', Enumerable.Range(0, 600).Select(i => "old " + i)), string.Join('\n', Enumerable.Range(0, 600).Select(i => "new " + i))),
                ("", new string('x', 200_001))
            })
            {
                var result = GngTextDiff.Create(before, after);
                Check(result.Summary.Contains("limit reached") && result.Text.Contains("Before / After") && !result.Text.Contains("@@"), "Oversize diff was silently truncated");
            }
        });
    }

    private static string[] ApplyHunks(string[] before, string diff)
    {
        var output = new List<string>();
        var current = 0;
        foreach (var line in diff.Replace("\r\n", "\n").Split('\n'))
        {
            var header = Regex.Match(line, @"^@@ -(\d+),(\d+) \+(\d+),(\d+) @@$");
            if (header.Success)
            {
                var start = int.Parse(header.Groups[1].Value) - (header.Groups[2].Value == "0" ? 0 : 1);
                Check(start >= current && start <= before.Length, "Invalid hunk offset");
                while (current < start) output.Add(before[current++]);
                continue;
            }
            if (line.Length == 0 || line[0] == '\\') continue;
            if (line[0] is ' ' or '-')
            {
                Check(current < before.Length && before[current] == line[1..], "Diff line does not match the current file");
                current++;
            }
            if (line[0] is ' ' or '+') output.Add(line[1..]);
        }
        output.AddRange(before.Skip(current));
        return output.ToArray();
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
