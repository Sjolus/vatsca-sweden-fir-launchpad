namespace VatscaUpdateChecker.Models;

/// <summary>Represents one item in the EuroScope profile launch dropdown.</summary>
/// <param name="DisplayName">Label shown in the ComboBox.</param>
/// <param name="FilePath">Full path to the .prf file, or null for "launch without profile".</param>
public record ProfileOption(string DisplayName, string? FilePath);
