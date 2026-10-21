namespace VatscaUpdateChecker.Models;

public enum ClientSignInStatus { Updated, Unchanged, NotConfigured, NotUsed, Running, Unsupported, Failed }

public sealed record ClientSignInOutcome(SoftwareApp App, ClientSignInStatus Status, string Message)
{
    public bool Changed => Status == ClientSignInStatus.Updated;
}

/// <summary>Configuration contents stay local and must never be included in diagnostics or reports.</summary>
public sealed record ClientConfigurationPatch(byte[] Content, bool Changed);
