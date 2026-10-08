using Microsoft.Web.WebView2.Core;
using System.IO;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

int passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    passed++;
}

Check(WebViewRuntimeService.ReadAvailableVersion(() => "140.0.0.0") == "140.0.0.0", "Available version is returned");
Check(WebViewRuntimeService.ReadAvailableVersion(() => "  ") is null, "Empty runtime result is unavailable");
Check(WebViewRuntimeService.ReadAvailableVersion(() => throw new WebView2RuntimeNotFoundException()) is null, "Missing-runtime exception is unavailable");
try
{
    WebViewRuntimeService.ReadAvailableVersion(() => throw new UnauthorizedAccessException("Synthetic access error"));
    throw new InvalidOperationException("Unexpected runtime detection failures must not be reported as missing runtime.");
}
catch (UnauthorizedAccessException) { passed++; }

using var cancellation = new CancellationTokenSource();
var progress = new ImmediateProgress();
bool restart = false;
SoftwarePrerequisiteService.Handler = (app, forwardedProgress, token, onRestart) =>
{
    Check(app == SoftwareApp.Vacs, "Facade selects the shared WebView2 route");
    Check(ReferenceEquals(progress, forwardedProgress), "Progress is forwarded unchanged");
    Check(token == cancellation.Token, "Cancellation is forwarded unchanged");
    forwardedProgress?.Report("Synthetic WebView2 setup");
    onRestart?.Invoke();
    return Task.FromResult(new SoftwarePrerequisiteResult(true));
};
var result = await WebViewRuntimeService.InstallAsync(progress, cancellation.Token, () => restart = true);
Check(result.RestartRequired && restart, "Result and early restart notification are preserved");
Check(progress.Last == "Synthetic WebView2 setup", "Shared-route progress reaches the caller");

restart = false;
SoftwarePrerequisiteService.Handler = (_, _, _, onRestart) =>
{
    onRestart?.Invoke();
    return Task.FromException<SoftwarePrerequisiteResult>(new IOException("Synthetic registration verification failure"));
};
try
{
    await WebViewRuntimeService.InstallAsync(onRestartRequired: () => restart = true);
    throw new InvalidOperationException("Post-install verification failure must propagate.");
}
catch (IOException) { Check(restart, "Restart remains required after later verification failure"); }

cancellation.Cancel();
SoftwarePrerequisiteService.Handler = (_, _, token, _) => Task.FromCanceled<SoftwarePrerequisiteResult>(token);
try
{
    await WebViewRuntimeService.InstallAsync(cancellationToken: cancellation.Token);
    throw new InvalidOperationException("Cancellation must propagate.");
}
catch (OperationCanceledException) { passed++; }

Console.WriteLine($"PASS: {passed} WebView2 facade checks; no runtime probing, browser, network or installers used.");

sealed class ImmediateProgress : IProgress<string>
{
    public string? Last { get; private set; }
    public void Report(string value) => Last = value;
}
