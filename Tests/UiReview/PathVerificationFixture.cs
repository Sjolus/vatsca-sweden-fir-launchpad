using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VatscaUpdateChecker;

// Presentation data only. No file, binary, registry or production verifier probes.
internal static partial class UiFixture
{
    internal static readonly (string Field, string Status)[] VerificationFields =
    [
        ("EuroscopeExePath", "EuroscopeExeStatus"),
        ("EuroscopePath", "EuroscopeDataStatus"),
        ("TrackAudioPath", "TrackAudioStatus"),
        ("VacsPath", "VacsStatus"),
        ("VatisPath", "VatisStatus"),
        ("VatEfsPath", "VatEfsStatus")
    ];

    internal static void PathChanged(SettingsWindow window, object sender)
    {
        if (sender is not TextBox field) return;
        var names = VerificationFields.FirstOrDefault(pair => pair.Field == field.Name);
        if (names.Status is null || Find<TextBlock>(window, names.Status) is not { } status) return;
        status.Text = string.IsNullOrWhiteSpace(field.Text)
            ? "Not chosen. This application is optional."
            : "Synthetic path changed — verification pending.";
        status.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        if (Find<CheckBox>(window, "KeepUnverified") is { } keep) keep.IsChecked = false;
    }

    internal static void PopulatePathVerification(SettingsWindow window)
    {
        foreach (var (field, status) in VerificationFields)
        {
            Text(window, status, Blank ? "Not chosen. This application is optional." : "Synthetic recognized path — no real file inspected.");
            Find<TextBlock>(window, status)?.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        }
        Text(window, "ValidationSummary", "");
        if (Find<CheckBox>(window, "KeepUnverified") is { } keep)
        {
            keep.IsChecked = false;
            keep.Visibility = Visibility.Collapsed;
        }
    }

    internal static void ShowSyntheticVerificationWarnings(SettingsWindow window)
    {
        var samples = new[]
        {
            ("EuroScope 3.2.3.2 found (synthetic).", "SuccessFg"),
            ("Incomplete Swedish GNG folder: sector, environment and profile files do not match. Choose the folder containing the installed package.", "WarningFg"),
            ("Wrong application: this is a synthetic VACS executable, not TrackAudio. Choose the correct program file.", "WarningFg"),
            ("Unrecognized custom copy. Its identity could not be established; keeping this path does not enable managed installation or removal.", "WarningFg"),
            ("File not found. The saved location may have moved or been removed.", "WarningFg"),
            ("VatEFS plugin found (synthetic). Backend configuration is separate.", "SuccessFg")
        };
        for (var index = 0; index < VerificationFields.Length; index++)
        {
            var (field, status) = VerificationFields[index];
            Text(window, field, @"C:\Synthetic path review\" + new string('x', 130) + "\\" + field + @"\example.exe");
            Text(window, status, samples[index].Item1);
            Find<TextBlock>(window, status)?.SetResourceReference(TextBlock.ForegroundProperty, samples[index].Item2);
        }
        Text(window, "ValidationSummary", "Some locations need attention. Correct invalid paths or explicitly keep an unrecognized custom location. No files are changed by this fixture.");
        if (Find<CheckBox>(window, "KeepUnverified") is { } keep)
        {
            keep.IsChecked = false;
            keep.Visibility = Visibility.Visible;
        }
    }

    internal static void PopulateDiscoveryVerification(InstallationDiscoveryWindow window)
    {
        Brush Color(string key) => (Brush)Application.Current.FindResource(key);
        Text(window, "ResultText", "Choose at most one synthetic copy per program or folder. Invalid results cannot be selected; nothing is imported by this fixture.");
        Find<ItemsControl>(window, "CandidatesList")!.ItemsSource = new[]
        {
            new DiscoveryRow
            {
                Candidate = new("EuroScope", "Recognized Windows Installer copy (synthetic).", "Current user"),
                Path = FakePath("EuroScope/EuroScope.exe"), LocationKind = "Program executable", ScopeLabel = "Current user (synthetic)",
                VerificationText = "EuroScope 3.2.3.2 verified (synthetic).", VerificationBrush = Color("SuccessFg")
            },
            new DiscoveryRow
            {
                Candidate = new("VACS", "Automatic management is unavailable for this custom copy.", "Custom"),
                Path = FakePath("Custom location/" + new string('x', 140) + "/vacs-client.exe"), LocationKind = "Program executable", ScopeLabel = "Custom location (synthetic)",
                VerificationText = "Recognized application at a custom location. The path can be adopted for launch; installer ownership is not established.", VerificationBrush = Color("WarningFg")
            },
            new DiscoveryRow
            {
                Candidate = new("Swedish GNG data", "", "Data folder"),
                Path = FakePath("Incomplete GNG package/ESAA"), CanSelect = false, LocationKind = "EuroScope data folder", ScopeLabel = "Data folder (synthetic)",
                VerificationText = "Incomplete package: an ESAA folder alone does not establish a matching sector, environment and profile. This location cannot be selected.", VerificationBrush = Color("WarningFg")
            },
            new DiscoveryRow
            {
                Candidate = new("TrackAudio", "", "Current user"),
                Path = FakePath("Wrong application/trackaudio.exe"), CanSelect = false, LocationKind = "Program executable", ScopeLabel = "Current user (synthetic)",
                VerificationText = "Wrong application identity (synthetic). This file is not TrackAudio and cannot be selected.", VerificationBrush = Color("WarningFg")
            }
        };
    }
}
