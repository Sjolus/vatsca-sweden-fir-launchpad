namespace VatscaUpdateChecker.Services;

// Identity data only. The fixture never links process enumeration or control APIs.
public sealed record ApplicationProcessIdentity(int Id, DateTime StartedUtc, string ExecutablePath);
