namespace VatscaUpdateChecker.Services;

/// <summary>The supported token-authentication build is an explicit policy, never a latest-release lookup.</summary>
public static class EuroScopePolicy
{
    public const string SupportedVersion = "3.2.3.2";
    public const string DownloadUrl = "https://euroscope.hu/install/EuroScopeSetup.3.2.3.2.msi";
    public const string ReleaseNotesUrl = "https://www.euroscope.hu/wp/2024/06/09/v3-2-2-3-and-v3-2-3-2-with-token-authentication-update/";
    public const string PackageFileName = "EuroScopeSetup.3.2.3.2.msi";
    public const long PackageSize = 18_494_976;
    public const string PackageSha256 = "DE11BF2F62E47D8BDA7E6C54F49F24FD96E46DE37B0D2381E6020623C48CC7F1";
    public const string ProductCode = "{8A06FB62-717E-460D-A285-C67226EE7B59}";
    public const string MainComponentCode = "{CAD0180C-591D-B672-79C7-0D8FD37A5AF1}";
    public const string UpgradeCode = "{72B38204-EB04-49DF-9F1B-A7038B9DE4F1}";
    public const string MsiVersion = "3.2.3";
}
