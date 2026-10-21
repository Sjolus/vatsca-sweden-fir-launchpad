namespace VatscaUpdateChecker.Models;

/// <summary>The Apps view retains the original row objects without changing service indices.</summary>
public static class ApplicationRows
{
    public static CheckResult[] Visible(IEnumerable<CheckResult> rows)
    {
        var source = rows.ToArray();
        var euroScope = source.Where(row => row.HasEuroScopeManagement);
        var children = source.Where(row => row.IsEuroScopeChild)
            .OrderBy(row => row.HasFontsCheck ? 0 : row.HasSaamanManagement ? 2 : 1);
        var remaining = source.Where(row => !row.HasEuroScopeManagement && !row.IsEuroScopeChild);
        return euroScope.Concat(children).Concat(remaining)
            .Where(row => row.IsVisibleInApplications).ToArray();
    }
}
