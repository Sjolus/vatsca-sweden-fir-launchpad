// The registration carrier has no behavior; this fixture keeps the isolated project independent
// of production registry/process discovery. The application build validates its integration.
namespace VatscaUpdateChecker.Services;
internal sealed record AtcMsiRegistration(string ProductCode, string Name, string Publisher,
    string Version, string InstallLocation, string DisplayIcon, bool WindowsInstaller, string Scope);
