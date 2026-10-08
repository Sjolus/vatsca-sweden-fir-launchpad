using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public sealed record SoftwarePrerequisiteResult(bool RestartRequired);

// The real Microsoft adapter has its own synthetic harness. This double prevents native setup here.
public static class SoftwarePrerequisiteService
{
    public static Func<SoftwareApp, IProgress<string>?, CancellationToken, Action?, Task<SoftwarePrerequisiteResult>> Handler { get; set; } =
        (_, _, _, _) => throw new InvalidOperationException("No synthetic prerequisite handler was configured.");

    public static Task<SoftwarePrerequisiteResult> InstallAsync(SoftwareApp app, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, Action? onRestartRequired = null) =>
        Handler(app, progress, cancellationToken, onRestartRequired);
}
