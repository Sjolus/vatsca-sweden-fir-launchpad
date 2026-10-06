namespace VatscaUpdateChecker.Services;

// Test-only services: the project never links Credential Manager P/Invoke or the disk logger.
internal static class CredentialManagerService
{
    public const string TargetVatsim = "synthetic/VATSIM";
    public const string TargetHoppie = "synthetic/Hoppie";
    public static Dictionary<string, string> Values { get; } = new();
    public static int Reads { get; private set; }
    public static string? Load(string target) { Reads++; return Values.GetValueOrDefault(target); }
    public static bool Has(string target) { Reads++; return Values.ContainsKey(target); }
    public static void Reset() { Reads = 0; Values.Clear(); }
}

internal static class Logger
{
    public static List<string> Messages { get; } = new();
    public static void Log(string category, string message) => Messages.Add(category + ": " + message);
}
