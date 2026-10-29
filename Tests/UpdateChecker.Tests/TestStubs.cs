namespace VatscaUpdateChecker.Services;

// These checks must never inspect production recovery state or write user logs.
public static class GngUpdateService
{
    public static void RequireNoPendingUpdate(string? path) =>
        throw new InvalidOperationException("The test must supply its synthetic pending-update guard.");
    public static bool IsTestingInstallation(string? path) =>
        throw new InvalidOperationException("The test must supply its synthetic installation receipt probe.");
}

public static class Logger
{
    public static void Log(string category, string message) { }
}

public enum FontsState { Unknown, AllOk, NeedsAction }
