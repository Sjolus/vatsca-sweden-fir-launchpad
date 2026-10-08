using System.Text;

namespace VatscaUpdateChecker.Services;

public sealed record GngTextDiffResult(string Text, string Summary);

public static class GngTextDiff
{
    private const int MaxLines = 20_000;
    private const int MaxDepth = 512;
    private const int MaxWork = 2_000_000;
    private const int MaxOutput = 200_000;
    private const int Context = 3;
    private sealed record Line(char Kind, string Text);

    // Inputs are the already-sanitized current and planned text, never raw profile data.
    public static GngTextDiffResult Create(string before, string after)
    {
        if (LineLimitExceeded(before) || LineLimitExceeded(after)) return TooLarge();
        var left = SplitLines(before);
        var right = SplitLines(after);
        var prefix = 0;
        while (prefix < left.Length && prefix < right.Length && left[prefix] == right[prefix]) prefix++;
        var suffix = 0;
        while (suffix < left.Length - prefix && suffix < right.Length - prefix &&
               left[^(suffix + 1)] == right[^(suffix + 1)]) suffix++;
        var middle = Compare(left[prefix..(left.Length - suffix)], right[prefix..(right.Length - suffix)]);
        if (middle is null) return TooLarge();
        var lines = left.Take(prefix).Select(text => new Line(' ', text)).Concat(middle)
            .Concat(left.Skip(left.Length - suffix).Select(text => new Line(' ', text))).ToArray();
        var changes = Enumerable.Range(0, lines.Length).Where(i => lines[i].Kind != ' ').ToArray();
        if (changes.Length == 0)
        {
            var notice = before == after
                ? "No visible text changes. Sensitive values are hidden; the file details show whether its bytes will change."
                : "Only line endings or the final newline differ. See the file details and Before / After view.";
            return new(notice, notice);
        }

        var oldAt = new int[lines.Length + 1];
        var newAt = new int[lines.Length + 1];
        oldAt[0] = newAt[0] = 1;
        for (var i = 0; i < lines.Length; i++)
        {
            oldAt[i + 1] = oldAt[i] + (lines[i].Kind == '+' ? 0 : 1);
            newAt[i + 1] = newAt[i] + (lines[i].Kind == '-' ? 0 : 1);
        }
        var output = new StringBuilder();
        for (var change = 0; change < changes.Length;)
        {
            var start = Math.Max(0, changes[change] - Context);
            var end = Math.Min(lines.Length, changes[change] + Context + 1);
            while (++change < changes.Length && changes[change] - Context <= end)
                end = Math.Min(lines.Length, changes[change] + Context + 1);
            var oldCount = oldAt[end] - oldAt[start];
            var newCount = newAt[end] - newAt[start];
            output.AppendLine($"@@ -{oldAt[start] - (oldCount == 0 ? 1 : 0)},{oldCount} +{newAt[start] - (newCount == 0 ? 1 : 0)},{newCount} @@");
            for (var i = start; i < end; i++)
            {
                output.Append(lines[i].Kind).AppendLine(lines[i].Text);
                if (output.Length > MaxOutput) return TooLarge();
            }
        }
        if (EndsInNewline(before) != EndsInNewline(after))
            output.AppendLine(EndsInNewline(after) ? "\\ Final newline added." : "\\ Final newline removed.");
        var removed = lines.Count(line => line.Kind == '-');
        var added = lines.Count(line => line.Kind == '+');
        return new(output.ToString(), $"{removed:N0} lines removed · {added:N0} lines added · {Context} lines of surrounding context");
    }

    private static List<Line>? Compare(string[] left, string[] right)
    {
        if (left.Length == 0) return right.Select(text => new Line('+', text)).ToList();
        if (right.Length == 0) return left.Select(text => new Line('-', text)).ToList();
        const int offset = MaxDepth + 1;
        var frontier = new int[2 * MaxDepth + 3];
        var trace = new List<int[]>();
        var work = 0;
        for (var depth = 0; depth <= Math.Min(MaxDepth, left.Length + right.Length); depth++)
        {
            trace.Add((int[])frontier.Clone());
            for (var diagonal = -depth; diagonal <= depth; diagonal += 2)
            {
                if (++work > MaxWork) return null;
                var index = offset + diagonal;
                var x = diagonal == -depth || diagonal != depth && frontier[index - 1] < frontier[index + 1]
                    ? frontier[index + 1] : frontier[index - 1] + 1;
                var y = x - diagonal;
                while (x < left.Length && y < right.Length && left[x] == right[y])
                {
                    x++; y++;
                    if (++work > MaxWork) return null;
                }
                frontier[index] = x;
                if (x < left.Length || y < right.Length) continue;
                var result = new List<Line>();
                for (var step = depth; step >= 0; step--)
                {
                    var previous = trace[step];
                    var current = x - y;
                    var previousDiagonal = current == -step || current != step && previous[offset + current - 1] < previous[offset + current + 1]
                        ? current + 1 : current - 1;
                    var previousX = previous[offset + previousDiagonal];
                    var previousY = previousX - previousDiagonal;
                    while (x > previousX && y > previousY) { result.Add(new(' ', left[--x])); y--; }
                    if (step == 0) break;
                    if (x == previousX) result.Add(new('+', right[--y]));
                    else result.Add(new('-', left[--x]));
                }
                result.Reverse();
                return result;
            }
        }
        return null;
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return EndsInNewline(text) ? lines[..^1] : lines;
    }

    private static bool LineLimitExceeded(string text)
    {
        var count = text.Length == 0 ? 0 : 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (i + 1 < text.Length && ++count > MaxLines) return true;
        }
        return false;
    }

    private static bool EndsInNewline(string text) => text.EndsWith('\n') || text.EndsWith('\r');

    private static GngTextDiffResult TooLarge() => new(
        "This file has too many lines or changes for the compact diff. Choose Before / After to inspect the complete available text.",
        "Compact diff limit reached. The Before / After view remains available.");
}
