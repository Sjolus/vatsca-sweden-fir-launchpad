namespace VatscaUpdateChecker.Models;

/// <summary>Short purpose descriptions shared by setup and the compact application list.</summary>
public static class ApplicationDescriptions
{
    public const string EuroScope = "Radar screens and your primary connection to the VATSIM network.";
    public const string SwedishGng = "Sector data for displaying Swedish airspace in EuroScope.";
    public const string TrackAudio = "Your primary radio client for communication on the network.";
    public const string Vacs = "An internal phone for coordination with other controllers.";
    public const string Vatis = "ATIS broadcasts for the airports under your control.";
    public const string VatEfs = "Electronic flight strips.";
    public const string Vatiris = "Information and tools for controllers in the Swedish VACC.";

    public static string For(string appName) => appName switch
    {
        "EuroScope" => EuroScope,
        "EuroScope (GNG Pack)" => SwedishGng,
        "TrackAudio" => TrackAudio,
        "VACS" => Vacs,
        "vATIS" => Vatis,
        "VatEFS" => VatEfs,
        "VATIRIS" => Vatiris,
        "Sweden FIR Launchpad" => "Manage your ATC applications from one place.",
        _ => string.Empty
    };
}
