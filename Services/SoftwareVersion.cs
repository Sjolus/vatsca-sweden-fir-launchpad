using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace VatscaUpdateChecker.Services;

/// <summary>Strict SemVer 2.0 precedence, including numeric prerelease identifiers.</summary>
public sealed class SoftwareVersion : IComparable<SoftwareVersion>
{
    private static readonly Regex Pattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly string[] _core;
    private readonly string[] _prerelease;

    private SoftwareVersion(string value, string[] core, string[] prerelease)
    {
        Value = value;
        _core = core;
        _prerelease = prerelease;
    }

    public string Value { get; }
    public bool IsPrerelease => _prerelease.Length > 0;
    public string? PrereleaseLabel => IsPrerelease ? _prerelease[0] : null;
    public override string ToString() => Value;

    public static bool TryParse(string? value, [NotNullWhen(true)] out SoftwareVersion? version)
    {
        version = null;
        // A finite limit also bounds work when parsing untrusted release metadata.
        if (string.IsNullOrEmpty(value) || value.Length > 512) return false;
        var match = Pattern.Match(value);
        if (!match.Success) return false;
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(part => IsNumeric(part) && part.Length > 1 && part[0] == '0')) return false;
        version = new SoftwareVersion(value,
            [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value], prerelease);
        return true;
    }

    public static bool TryCompare(string? left, string? right, out int comparison)
    {
        comparison = 0;
        if (!TryParse(left, out var a) || !TryParse(right, out var b)) return false;
        comparison = a.CompareTo(b);
        return true;
    }

    public int CompareTo(SoftwareVersion? other)
    {
        if (other is null) return 1;
        for (int index = 0; index < _core.Length; index++)
        {
            int result = CompareNumeric(_core[index], other._core[index]);
            if (result != 0) return result;
        }
        if (!IsPrerelease || !other.IsPrerelease)
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        for (int index = 0; index < Math.Min(_prerelease.Length, other._prerelease.Length); index++)
        {
            string left = _prerelease[index], right = other._prerelease[index];
            bool leftNumeric = IsNumeric(left), rightNumeric = IsNumeric(right);
            int result = leftNumeric && rightNumeric ? CompareNumeric(left, right)
                : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1
                : string.CompareOrdinal(left, right);
            if (result != 0) return result;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static bool IsNumeric(string value) => value.All(character => character is >= '0' and <= '9');
    private static int CompareNumeric(string left, string right) =>
        left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
}
