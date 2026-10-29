using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private string _workspace = "Apps";
    private string _gngFlowWorkspace = "Apps";
    private string _gngFlowSection = "Gng";
    private GngUpdateControl? _gngUpdateFlow;
    private GngCleanupControl? _gngCleanupFlow => null;
    private bool HasGngFlow => _gngUpdateFlow is not null;

    internal void AttachSyntheticGngFlow(GngUpdateControl flow, string workspace)
    {
        SetupWorkspace.ShowSection("Gng");
        ShowSyntheticWorkspace(workspace);
        _gngFlowWorkspace = workspace;
        _gngFlowSection = "Gng";
        _gngUpdateFlow = flow;
        flow.StateChanged += UpdateGngPresentation;
        UpdateGngPresentation();
    }

    internal void ReturnToSyntheticGngFlow() => ReturnToGngFlow();

    internal void DetachSyntheticGngFlow()
    {
        if (_gngUpdateFlow is { } flow) flow.StateChanged -= UpdateGngPresentation;
        _gngUpdateFlow = null;
        UpdateGngPresentation();
    }
}

internal static class GngBrowserStartupTests
{
    internal static int Run()
    {
        int count = 0;
        foreach (var origin in new[] { "Apps", "Setup" })
        {
            UiFixture.Blank = UiFixture.Minimum = false;
            UiFixture.Scale = 1;
            var main = new MainWindow { Width = 900, Height = 550 };
            var flow = new GngUpdateControl();
            try
            {
                flow.SetSyntheticState(GngUpdateFixtureState.CheckingSavedPackages);
                main.AttachSyntheticGngFlow(flow, origin);
                CheckPresented(main, flow, origin, false);

                // The production host predicate must display the browser before initialization
                // finishes; the fixture never creates WebView2, a native window or a request.
                flow.SetSyntheticState(GngUpdateFixtureState.StartingBrowser);
                CheckPresented(main, flow, origin, true);
                Require(flow.IsBusy && flow.CanCancel && !flow.CanDismiss, "Pending browser initialization must remain busy and cancellable.");
                var browserHost = (ContentControl)flow.FindName("BrowserHost");
                Require(browserHost.Content is null && IsPresented(browserHost), "The empty browser host must be presented before any native browser is attached.");
                Require(browserHost.ActualWidth > 0 && browserHost.ActualHeight > 0, "Browser startup must receive a nonzero layout slot.");

                var other = origin == "Apps" ? "Setup" : "Apps";
                main.ShowSyntheticWorkspace(other);
                CheckPresented(main, flow, origin, false);
                main.ShowSyntheticWorkspace(origin);
                CheckPresented(main, flow, origin, true);

                if (origin == "Setup")
                {
                    main.SetupWorkspace.ShowSection("Preferences");
                    CheckPresented(main, flow, origin, false);
                    main.SetupWorkspace.ShowSection("Gng");
                    CheckPresented(main, flow, origin, true);
                }

                // Continue from the other workspace reparents the same flow, then can return.
                main.ShowSyntheticWorkspace(other);
                main.ReturnToSyntheticGngFlow();
                CheckPresented(main, flow, other, true);
                main.ShowSyntheticWorkspace(origin);
                main.ReturnToSyntheticGngFlow();
                CheckPresented(main, flow, origin, true);

                flow.CancelPreparation();
                CheckPresented(main, flow, origin, true);
                Require(!flow.IsBusy && flow.CanDismiss && !flow.InstallationCompleted, "Stopping browser preparation must release busy state without installing.");

                flow.SetSyntheticState(GngUpdateFixtureState.StartingBrowser);
                CheckPresented(main, flow, origin, true);
                flow.SetSyntheticState(GngUpdateFixtureState.RuntimeFallback);
                CheckPresented(main, flow, origin, true);
                Require(!flow.IsBusy && flow.CanDismiss, "Browser startup failure must expose an idle fallback.");

                flow.SetSyntheticState(GngUpdateFixtureState.StartingBrowser);
                flow.SetSyntheticState(GngUpdateFixtureState.WaitingForLogin);
                CheckPresented(main, flow, origin, true);
                flow.SetSyntheticState(GngUpdateFixtureState.Downloading);
                CheckPresented(main, flow, origin, false);
                Require(flow.IsBusy && flow.CanCancel, "An active download must remain cancellable in the ordinary workspace.");
                Require(!main.IsVisible, "Browser host regression checks must never display a native window.");
                count++;
            }
            finally
            {
                main.DetachSyntheticGngFlow();
                flow.Dispose();
                main.Close();
            }
        }
        return count;
    }

    private static void CheckPresented(MainWindow main, GngUpdateControl flow, string owner, bool expected)
    {
        LayoutValidation.CheckWindowContent(main);
        var appsHost = (ContentControl)main.FindName("GngFlowHost");
        var setupHost = (ContentControl)main.SetupWorkspace.FindName("GngOperationHost");
        var host = owner == "Apps" ? appsHost : setupHost;
        var other = owner == "Apps" ? setupHost : appsHost;
        Require(ReferenceEquals(host.Content, flow) && other.Content is null, "Exactly one workspace must own the same GNG flow.");
        Require(IsPresented(host) == expected, $"{owner} browser host presentation differs while {flow.SyntheticState}.");
        if (expected) Require(IsPresented(flow), "The presented GNG control must have visible ancestors.");
    }

    private static bool IsPresented(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null && current is not Window; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
        return true;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
