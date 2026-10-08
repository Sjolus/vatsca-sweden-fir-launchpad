using System.IO;
using System.Text.RegularExpressions;

namespace VatscaUpdateChecker.Services;

internal enum GngDownloadRequestPhase { Stopped, WaitingForPage, Selecting, WaitingForDownload, Downloading }
internal enum GngInspectionRetry { Ignored, WaitingForPage, LimitReached }

/// <summary>One explicit package request shared by the main browser and its sign-in windows.</summary>
internal sealed class GngDownloadRequestState
{
    public long Generation { get; private set; }
    public GngDownloadRequestPhase Phase { get; private set; }
    public bool IsPending => Phase is GngDownloadRequestPhase.WaitingForPage or GngDownloadRequestPhase.Selecting or GngDownloadRequestPhase.WaitingForDownload;
    private bool _referenceFollows;
    private int _changedInspections;
    public string? SelectedIdentity { get; private set; }

    public void Arm(bool referenceFollows = false) { Generation++; SelectedIdentity = null; _changedInspections = 0; _referenceFollows = referenceFollows; Phase = GngDownloadRequestPhase.WaitingForPage; }
    public void Disarm() { Generation++; SelectedIdentity = null; _changedInspections = 0; _referenceFollows = false; Phase = GngDownloadRequestPhase.Stopped; }
    public bool TryBeginReference()
    {
        if (!_referenceFollows || Phase != GngDownloadRequestPhase.Downloading) return false;
        _referenceFollows = false;
        _changedInspections = 0;
        SelectedIdentity = null;
        Generation++;
        Phase = GngDownloadRequestPhase.WaitingForPage;
        return true;
    }
    public bool TryBeginSelection(out long generation)
    {
        generation = Generation;
        if (Phase != GngDownloadRequestPhase.WaitingForPage) return false;
        Phase = GngDownloadRequestPhase.Selecting;
        return true;
    }
    public bool CompleteSelection(long generation, string status, string? identity = null)
    {
        if (generation != Generation || Phase != GngDownloadRequestPhase.Selecting) return false;
        if (status == "ready" && identity is null) status = "ambiguous";
        SelectedIdentity = status == "ready" ? identity : null;
        Phase = status switch
        {
            "ready" => GngDownloadRequestPhase.WaitingForDownload,
            "waiting-for-login" or "wrong-page" => GngDownloadRequestPhase.WaitingForPage,
            _ => GngDownloadRequestPhase.Stopped
        };
        return true;
    }
    public GngInspectionRetry RetryChangedInspection(long generation)
    {
        // Inspection has no side effects, so normal navigation can safely discard it.
        // Bound repeated changes instead of looping indefinitely on an unstable page.
        if (generation != Generation || Phase != GngDownloadRequestPhase.Selecting) return GngInspectionRetry.Ignored;
        SelectedIdentity = null;
        if (++_changedInspections > 4)
        {
            Phase = GngDownloadRequestPhase.Stopped;
            return GngInspectionRetry.LimitReached;
        }
        Phase = GngDownloadRequestPhase.WaitingForPage;
        return GngInspectionRetry.WaitingForPage;
    }
    public bool CompleteClick(long generation, string status)
    {
        // The download may finish before its navigation-triggering script returns.
        // Its identity was already recorded by the read-only inspection.
        if (generation != Generation || Phase != GngDownloadRequestPhase.WaitingForDownload) return false;
        if (status != "clicked") { SelectedIdentity = null; Phase = GngDownloadRequestPhase.Stopped; }
        return true;
    }
    public bool TryStartDownload()
    {
        if (Phase != GngDownloadRequestPhase.WaitingForDownload || SelectedIdentity is null) return false;
        Phase = GngDownloadRequestPhase.Downloading;
        return true;
    }
    public bool CanReportNavigationFailure(long generation) =>
        generation == Generation && Phase == GngDownloadRequestPhase.WaitingForPage;
    public bool ExpireDownloadWait(long generation)
    {
        if (generation != Generation || Phase != GngDownloadRequestPhase.WaitingForDownload) return false;
        Phase = GngDownloadRequestPhase.Stopped;
        return true;
    }

    public bool MatchesSelectedFilename(string path)
    {
        if (SelectedIdentity is null) return false;
        var match = Regex.Match(Path.GetFileName(path), @"_([0-9]{14}-[0-9]{6}-[0-9]{4})(?: \([0-9]+\))?\.zip\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && match.Groups[1].Value.Equals(SelectedIdentity, StringComparison.Ordinal);
    }
}
