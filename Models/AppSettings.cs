namespace VatscaUpdateChecker.Models;

public class AppSettings
{
    public bool CheckOnStartup { get; set; } = true;
    public bool IsDarkMode { get; set; } = false;
    public string EuroscopeExePath { get; set; } = string.Empty;
    public string EuroscopeDataPath { get; set; } = string.Empty;
    public string TrackAudioExePath { get; set; } = string.Empty;
    public string VacsExePath { get; set; } = string.Empty;
    public string VatisExePath { get; set; } = string.Empty;

    // EuroScope profile fields (password is stored separately in Windows Credential Manager)
    public string VatsimName { get; set; } = string.Empty;
    public int VatsimRating { get; set; } = -1;
    public string VatsimCid { get; set; } = string.Empty;
    public string ObsCallsign { get; set; } = string.Empty;
    public string LastEuroscopeProfile { get; set; } = string.Empty;
}
