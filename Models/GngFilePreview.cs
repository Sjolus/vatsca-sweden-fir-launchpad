namespace VatscaUpdateChecker.Models;

/// <summary>Read-only, sanitized before/after text for one validated installation-plan file.</summary>
public sealed record GngFilePreview(
    string RelativePath, string Action, string Summary, string? BeforeText, string? AfterText)
{
    public bool HasText => BeforeText is not null && AfterText is not null;
}
