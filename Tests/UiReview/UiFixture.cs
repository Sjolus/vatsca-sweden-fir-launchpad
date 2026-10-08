using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

internal static class UiReviewProgram
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => UiFixture.Log(e.ExceptionObject as Exception ?? new Exception("Unknown fixture error"));
        var app = new App();
        app.DispatcherUnhandledException += (_, e) => { UiFixture.Log(e.Exception); e.Handled = true; };
        try
        {
            app.InitializeComponent();
            if (args.Contains("--check") || args.Contains("--validate-layouts"))
            {
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var snapshotOption = Array.IndexOf(args, "--snapshots");
                if (snapshotOption >= 0)
                {
                    if (snapshotOption + 1 >= args.Length) throw new ArgumentException("--snapshots needs an output directory.");
                    LayoutSnapshots.Capture(args[snapshotOption + 1]);
                }
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "layout-validation.txt"), "RUNNING: hidden content and binding validation.");
                var result = LayoutMatrix.Run();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "layout-validation.txt"), result);
                Console.WriteLine(result);
                app.Shutdown();
                return 0;
            }
            app.Run(new UiReviewHost());
            return 0;
        }
        catch (Exception error) { UiFixture.Log(error); return 1; }
    }
}

public sealed class UiReviewHost : Window
{
    public UiReviewHost()
    {
        Title = "UI review — synthetic"; Width = 620; Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(18) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Production WPF layouts • inert fixture handlers", FontSize = 19, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "No production services, startup hooks, credentials, registry, network or process operations are compiled into this fixture.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,10) });
        var screens = new WrapPanel(); panel.Children.Add(screens);
        Add(screens, "Main window", () => Open(new MainWindow()));
        Add(screens, "Settings", () => Open(new SettingsWindow()));
        Add(screens, "Controller profile", () => Open(new AppConfigWindow()));
        Add(screens, "Manage / remove", () => Open(new MaintenanceWindow()));
        Add(screens, "Find installations", () => Open(new InstallationDiscoveryWindow()));
        Add(screens, "Manage EuroScope", () => Open(new EuroScopeInstallWindow()));
        Add(screens, "Set up vATIS", () => Open(new FreshSoftwareInstallWindow()));
        Add(screens, "Setup wizard", () => Open(new SetupWizardWindow()));
        Add(screens, "GNG cleanup", () => Open(new GngCleanupWindow()));
        Add(screens, "GNG setup / update", () => Open(new GngUpdateWindow()));
        Add(screens, "GNG file changes", () => Open(GngFileDiffFixture.Create(GngFileDiffFixtureState.Changes, UiFixture.Minimum)));
        var options = new WrapPanel(); panel.Children.Add(options);
        Add(options, "Light / dark", UiFixture.ToggleTheme);
        Add(options, "Mixed / blank paths", () => { UiFixture.Blank = !UiFixture.Blank; UiFixture.Refresh(); });
        Add(options, "Default / minimum", () => { UiFixture.Minimum = !UiFixture.Minimum; UiFixture.Refresh(); });
        Add(options, "Cycle simulated progress", UiFixture.CycleProgress);
        Add(options, "Toggle restart notice", () => { UiFixture.RestartRequired = !UiFixture.RestartRequired; UiFixture.Refresh(); });
        var scales = new WrapPanel(); panel.Children.Add(scales);
        foreach (double scale in new[] { 1d, 1.5d, 2d })
            Add(scales, "Content " + (scale*100) + "%", () => { UiFixture.Scale = scale; UiFixture.Refresh(); });
        panel.Children.Add(new TextBlock { Text = "Content scaling is a layout simulation, NOT actual OS DPI. Original resize modes remain unchanged. Launch/install/download buttons are inert.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0) });
    }
    private void Open(Window window) { window.Owner = this; window.Show(); }
    private static void Add(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(9,6,9,6), Margin = new Thickness(0,0,7,7) };
        button.Click += (_,_) => { try { action(); } catch (Exception error) { UiFixture.Log(error); MessageBox.Show(error.ToString(), "UI fixture error — synthetic"); } }; panel.Children.Add(button);
    }
}

internal static partial class UiFixture
{
    public static int ErrorCount { get; private set; }
    public static void Log(Exception error) { ErrorCount++; File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "fixture-errors.log"), DateTimeOffset.Now + "\n" + error + "\n\n"); }
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "fixture-data");
    private static readonly List<Window> Windows = [];
    private static readonly Dictionary<Window,(double Width,double Height)> Sizes = [];
    private static readonly Dictionary<Window,int> Steps = [];
    public static bool Blank, Minimum, Dark, RestartRequired;
    public static double Scale = 1;

    public static void Initialize(Window window)
    {
        window.Title += " — UI review — synthetic";
        Windows.Add(window); Sizes[window] = (window.Width,window.Height); Steps[window] = 0;
        window.Closed += (_,_) => { Windows.Remove(window); Sizes.Remove(window); Steps.Remove(window); };
        Populate(window); Size(window);
    }
    public static void Refresh()
    {
        foreach (var window in Windows.ToArray()) { Populate(window); Size(window); }
    }
    private static void Size(Window window)
    {
        var original = Sizes[window];
        window.Width = Minimum && window.MinWidth > 0 ? window.MinWidth : original.Width;
        window.Height = Minimum && window.MinHeight > 0 ? window.MinHeight : original.Height;
        if (window.Content is FrameworkElement content) content.LayoutTransform = new ScaleTransform(Scale,Scale);
    }
    public static void ToggleTheme()
    {
        Dark = !Dark;
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri(Dark ? "Themes/Dark.xaml" : "Themes/Light.xaml",UriKind.Relative) };
        Refresh();
    }
    private static void Populate(Window window)
    {
        if (window is GngCleanupWindow cleanup) cleanup.RefreshSyntheticScenario();
        if (window is GngUpdateWindow update) update.RefreshSyntheticScenario();
        if (window is SetupWizardWindow wizard) wizard.RefreshSyntheticScenario();
        if (window is MainWindow main)
        {
            main.SetSyntheticRestart(RestartRequired);
            Find<ItemsControl>(window,"AppList")!.ItemsSource = Rows();
            main.ResetSyntheticDetails();
            Text(window,"LastCheckedText","Synthetic UI review • no checks or clients run");
            Find<FrameworkElement>(window,"DiscoveryPrompt")!.Visibility = Blank ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>(window,"ThemeToggleButton")!.Content = Dark ? "☀" : "☽";
            var profileButton = Find<Button>(window,"AppConfigButton")!;
            profileButton.ClearValue(Control.BackgroundProperty);
            profileButton.ClearValue(Control.ForegroundProperty);
            profileButton.Content = "Controller profile · review";
            profileButton.ToolTip = "Review your controller identity and application configuration.";
        }
        if (window is SettingsWindow)
        {
            foreach (var field in new[] { "EuroscopeExePath","EuroscopePath","TrackAudioPath","VacsPath","VatisPath","VatEfsPath" })
                Text(window,field,Blank ? "" : FakePath(field + "/Synthetic example path.txt"));
            Find<CheckBox>(window,"CheckOnStartup")!.IsChecked = false;
            Text(window,"DiscoveryStatus","Synthetic paths only. Save and Browse stay inside this inert review fixture.");
            PopulatePathVerification((SettingsWindow)window);
        }
        if (window is AppConfigWindow)
        {
            Text(window,"NameBox","Åsa Östergård (synthetic)"); Text(window,"CidBox","0000000"); Text(window,"ObsBox","ES");
            Find<ComboBox>(window,"RatingBox")!.ItemsSource = new[] { "Observer (OBS)","Student 1 (S1)","Student 2 (S2)","Student 3 (S3)","Controller (C1)" };
            Find<ComboBox>(window,"RatingBox")!.SelectedIndex = 2;
            Find<PasswordBox>(window,"PasswordBox")!.Password = "FAKE-PASSWORD";
            Find<PasswordBox>(window,"HoppieBox")!.Password = "FAKE-HOPPIE";
            Find<Border>(window,"SyncBanner")!.SetResourceReference(Border.BackgroundProperty,"WarningBg");
            Find<TextBlock>(window,"SyncText")!.SetResourceReference(TextBlock.ForegroundProperty,"WarningFg");
            Text(window,"SyncText","Synthetic attention state: profile settings need review. No real credentials were loaded.");
            Text(window,"TargetPathText",Blank ? "Not configured" : FakePath("EuroScope data/An intentionally long controller profile directory name/ESAA/Profiles"));
            Text(window,"ApplyHintText",Blank ? "Save stores the synthetic profile only; no EuroScope files can be updated." : "Save & apply would update all EuroScope profiles (ES*.prf) in the folder above. This fixture changes nothing.");
            Find<Button>(window,"SaveProfileButton")!.Content = Blank ? "Save profile" : "Save & apply";
            Find<Button>(window,"PreviewButton")!.IsEnabled = !Blank;
        }
        if (window is MaintenanceWindow)
        {
            Find<ItemsControl>(window,"TargetsList")!.ItemsSource = new[] { "EuroScope", "GNG package", "VACS", "vATIS", "TrackAudio", "VatEFS", "VATIRIS" }.Select(name => new RemovalRow { Target = new(name, "Synthetic recognized installation and personal settings.", true, true), Details = FakePath(name + "/installation and data") }).ToList();
            Text(window,"StatusText","Synthetic selection only. No real inventory or credentials were read.");
            Find<Button>(window,"ReviewButton")!.IsEnabled = true;
        }
        if (window is InstallationDiscoveryWindow discovery)
            PopulateDiscoveryVerification(discovery);
        if (window is EuroScopeInstallWindow or FreshSoftwareInstallWindow)
        {
            Text(window,"SupportedText","Supported EuroScope version: 3.2.3.2");
            Text(window,"Heading","Set up vATIS");
            Text(window,"ConfiguredPath", Blank ? "No executable configured (synthetic)." : "Configured path: " + FakePath("Current installation/application.exe"));
            Text(window,"StatusText","Synthetic preparation only. Review, confirmation and progress do not run installers.");
            if (Find<CheckBox>(window,"AllowVatisBeta") is {} beta) beta.Visibility = Visibility.Visible;
            if (window is FreshSoftwareInstallWindow { IsVatEfsFixture: true })
            {
                Text(window,"Heading","Set up VatEFS");
                Find<CheckBox>(window,"AllowVatisBeta")!.Visibility = Visibility.Collapsed;
                Find<FrameworkElement>(window,"PrereleaseNotice")!.Visibility = Visibility.Visible;
                Find<Button>(window,"PrerequisiteButton")!.Content = "Check required x86 runtime…";
                Text(window,"ProcessNotice","EuroScope is running. Close all EuroScope instances before changing GNG or VatEFS files.");
                Find<FrameworkElement>(window,"ProcessNotice")!.Visibility = Visibility.Visible;
            }
        }
    }
    private static ObservableCollection<CheckResult> Rows()
    {
        var rows = new ObservableCollection<CheckResult>
        {
            new() { AppName="EuroScope", HasEuroScopeManagement=true, InstalledVersion=Blank?"—":"3.2.4.0", LatestVersion="3.2.3.2", Status=Blank?CheckStatus.NotConfigured:CheckStatus.Unsupported, LaunchPath=Blank?"":FakeFile("EuroScope.exe"), StatusMessage="Synthetic unsupported-version example." },
            new() { AppName="EuroScope (GNG Pack)", IsFolder=true, HasFontsCheck=true, InstalledVersion=Blank?"—":"260201-0003", LatestVersion="260901-0001", Status=Blank?CheckStatus.NotConfigured:CheckStatus.UpdateAvailable, FontsState=FontsState.NeedsAction, LaunchPath=Blank?"":FakeFolder("GNG") },
            Software("TrackAudio",SoftwareApp.TrackAudio,SoftwareUpdatePhase.Available,"1.3.0","1.4.0"),
            Software("VACS",SoftwareApp.Vacs,SoftwareUpdatePhase.Downloading,"2.7.0","2.8.0"),
            Software("vATIS",SoftwareApp.Vatis,SoftwareUpdatePhase.Error,"4.1.0-beta.18","4.1.0-beta.19"),
            new() { AppName="VatEFS", SoftwareApp=SoftwareApp.VatEfs, SoftwareExecutablePath=Blank?"":FakeFile("efs.exe"),
                SoftwareUpdate=new(SoftwareApp.VatEfs,Blank?SoftwareUpdatePhase.Unavailable:SoftwareUpdatePhase.Available,"Synthetic VatEFS package update."), IsWebApp=true, IsLocalUrl=true, IsLocalUrlReachable=!Blank, LaunchPath="http://localhost:17770",
                InstalledVersion=Blank?"Not installed":"v0.0.14", LatestVersion="v0.0.15", LatestIsPrerelease=true,
                DownloadUrl="https://github.com/minsulander/vatefs/releases/tag/v0.0.15", Status=Blank?CheckStatus.NotConfigured:CheckStatus.UpdateAvailable },
            new() { AppName="VATIRIS", IsWebApp=true, LaunchPath="https://vatiris.se", Status=CheckStatus.WebApp, InstalledVersion="N/A", LatestVersion="N/A" },
            new() { AppName="Sweden FIR Launchpad", HasSelfUpdate=true, InstalledVersion="2.0.0-dev.1", LatestVersion="2.0.0", ShowSelfUpdateAction=true, SelfUpdateActionText="Download update", SelfUpdateSummary="Update available", StatusMessage="Synthetic self-update; no network requests." }
        };
        if (!Blank) { rows[0].Profiles.Add(new("ESAA TOPSKY — Stockholm approach",FakePath("GNG/ESAA TOPSKY.prf"))); rows[0].SelectedProfile=rows[0].Profiles[0]; }
        return rows;
    }
    private static CheckResult Software(string name, SoftwareApp app, SoftwareUpdatePhase phase,string installed,string latest) =>
        new() { AppName=name, SoftwareApp=app, LaunchPath=Blank?"":FakeFile(name+".exe"), InstalledVersion=Blank?"—":installed, LatestVersion=Blank?"—":latest,
            StatusMessage=Blank?"Configure or adopt an installed copy first.":"Synthetic update state — no application is running.",
            SoftwareUpdate=new(app,Blank?SoftwareUpdatePhase.Unavailable:phase,Blank?"Configure or adopt an installed copy first.":"Synthetic update state — no application is running.",ProgressPercent:phase==SoftwareUpdatePhase.Downloading?43:null) };

    public static void Handle(Window window, object sender, RoutedEventArgs args,string action)
    {
        if (window is MaintenanceWindow or EuroScopeInstallWindow or FreshSoftwareInstallWindow && HandleWorkflow(window, action)) return;
        switch (action)
        {
            case "Window_Loaded": return;
            case "Header_MouseLeftButtonDown": if (args is MouseButtonEventArgs { LeftButton:MouseButtonState.Pressed }) window.DragMove(); return;
            case "ThemeToggle_Click": ToggleTheme(); return;
            case "DensityToggle_Click": window.Tag=Equals(window.Tag,"Compact")?"Comfortable":"Compact"; return;
            case "Minimize_Click": window.WindowState=WindowState.Minimized; return;
            case "Maximize_Click": window.WindowState=window.WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized; return;
            case "Close_Click": case "Cancel_Click": window.Close(); return;
            case "Settings_Click": Show(new SettingsWindow(),window); return;
            case "SetupWizard_Click": Show(new SetupWizardWindow(),window); return;
            case "AppConfig_Click": Show(new AppConfigWindow(),window); return;
            case "Maintenance_Click": Show(new MaintenanceWindow(),window); return;
            case "GngCleanup_Click": Show(new GngCleanupWindow(),window); return;
            case "GngUpdate_Click": Show(new GngUpdateWindow(),window); return;
            case "DiscoverInstallations_Click": case "FindInstallations_Click": Show(new InstallationDiscoveryWindow(),window); return;
            case "EuroScopeManage_Click": Show(new EuroScopeInstallWindow(),window); return;
            case "SoftwareInstall_Click": Show(new FreshSoftwareInstallWindow(),window); return;
            case "Use_Click": Text(window,"ResultText","Synthetic selection recorded in memory only. No real Settings changed."); return;
            case "Check_Click": Blank=!Blank; Refresh(); return;
            case "TogglePassword_Click": TogglePassword(window,"PasswordBox","PasswordBoxText"); return;
            case "ToggleHoppie_Click": TogglePassword(window,"HoppieBox","HoppieBoxText"); return;
            case "ProfileDropdown_Click":
                if (sender is Button profileButton && profileButton.DataContext is CheckResult row)
                { var menu = new ContextMenu(); foreach(var profile in row.Profiles) { var item=new MenuItem { Header=profile.DisplayName }; item.Click+=(_,_)=>row.SelectedProfile=profile; menu.Items.Add(item); } profileButton.ContextMenu=menu; menu.IsOpen=true; }
                return;
            case "Save_Click": Text(window,window is AppConfigWindow?"SyncText":"DiscoveryStatus","Synthetic save only. No settings, credentials or external profiles were changed."); return;
            case "Preview_Click": Text(window,"SyncText","Synthetic preview: fake name, rating and profile fields only. No real files were read."); return;
            case "Clear_Click": case "InsertDefault_Click": case "BrowseExe_Click": case "BrowseFolder_Click":
                var tag=(sender as FrameworkElement)?.Tag as string ?? "EuroscopeData";
                var field=tag switch { "EuroScope"=>"EuroscopeExePath","EuroscopeData"=>"EuroscopePath","TrackAudio"=>"TrackAudioPath","VACS"=>"VacsPath","vATIS"=>"VatisPath",_=>"VatEfsPath" };
                Text(window,field,action=="Clear_Click"?"":FakePath(tag+"/Synthetic choice.txt")); return;
            default:
                if (window is MainWindow) Text(window,"LastCheckedText","Synthetic action only: "+action+" • no process, network or files changed");
                else Text(window,window is AppConfigWindow?"SyncText":"DiscoveryStatus","Synthetic action only: "+action+" • production services excluded");
                return;
        }
    }
    private static bool HandleWorkflow(Window window,string action)
    {
        var confirmation = Find<CheckBox>(window,"ConfirmRemoval") ?? Find<CheckBox>(window,"ConfirmInstall") ?? Find<CheckBox>(window,"ConfirmChange");
        switch (action)
        {
            case "Adopt_Click": Show(new InstallationDiscoveryWindow(),window); return true;
            case "Removal_Click": Show(new MaintenanceWindow(),window); return true;
            case "ChooseBackup_Click": Text(window,"BackupPath",FakePath("Recovery exports")); return true;
            case "Prerequisite_Click": Text(window,"StatusText","Synthetic prerequisite state: missing runtime. The real dialog would ask before downloading; this fixture does nothing."); return true;
            case "Refresh_Click":
                Populate(window);
                Text(window,"StatusText","Synthetic inventory refreshed. All destructive selections are clear.");
                goto case "SelectionChanged";
            case "Review_Click":
                Text(window,"ReviewText","SYNTHETIC REVIEW — NO REAL PLAN\n\nApplication: " + (window is EuroScopeInstallWindow ? "EuroScope 3.2.3.2 (replace newer copy; downgrade)" : window is FreshSoftwareInstallWindow ? "vATIS 4.1.0-beta.19 (explicit beta)" : "Selected ATC applications") + "\nDestination: " + FakePath("Reviewed installation") + "\nRecovery export: " + FakePath("Recovery exports/2026-10-03-synthetic") + "\n\nExisting settings remain where supported. Removed or replaced files may need manual recovery from the export.\nNo clients will be started.\n\nThis text exercises the production review layout only.");
                if (confirmation is not null) { confirmation.IsEnabled=true; confirmation.IsChecked=false; }
                Text(window,"StatusText","Synthetic review ready. Confirm to exercise the layout.");
                if (Find<ScrollViewer>(window,"ContentScroll") is {} reviewScroll &&
                    Find<FrameworkElement>(window,"ReviewPanel") is {} reviewPanel && Find<TextBox>(window,"ReviewText") is {} reviewText)
                    ReviewScrollHelper.Show(reviewScroll, reviewPanel, reviewText, () => confirmation?.IsEnabled == true);
                return true;
            case "ConfirmationChanged":
                if (Find<Button>(window,"ApplyButton") is {} apply) apply.IsEnabled=confirmation?.IsChecked==true;
                return true;
            case "Apply_Click": CycleProgress(window); return true;
            case "CancelOperation_Click":
                if (Find<ProgressBar>(window,"Activity") is {} activity) activity.Visibility=Visibility.Collapsed;
                Text(window,"StatusText","Synthetic preparation cancelled. Nothing changed."); return true;
            case "SelectApps_Click": case "SelectData_Click": case "Clear_Click":
                if (Find<ItemsControl>(window,"TargetsList") is {} list && list.ItemsSource is List<RemovalRow> rows)
                {
                    foreach(var row in rows) { if(action=="SelectApps_Click") row.RemoveApplication=true; else if(action=="SelectData_Click") row.RemoveData=row.RemoveApplication; else { row.RemoveApplication=false; row.RemoveData=false; } }
                    list.Items.Refresh();
                }
                goto case "SelectionChanged";
            case "BetaChanged": case "SelectionChanged":
                if (confirmation is not null) { confirmation.IsChecked=false; confirmation.IsEnabled=false; }
                if (Find<Button>(window,"ApplyButton") is {} button) button.IsEnabled=false;
                Text(window,"StatusText","Synthetic selection changed. Review again before continuing."); return true;
            default: return false;
        }
    }
    public static void CycleProgress()
    {
        foreach(var window in Windows.ToArray()) if(window is MaintenanceWindow or EuroScopeInstallWindow or FreshSoftwareInstallWindow) CycleProgress(window);
    }
    private static void CycleProgress(Window window)
    {
        if(!Steps.ContainsKey(window)) return;
        var step = Steps[window] = (Steps[window]+1)%4;
        if(Find<ProgressBar>(window,"Activity") is {} bar) { bar.Visibility=step is 1 or 2?Visibility.Visible:Visibility.Collapsed; bar.IsIndeterminate=step==2; bar.Value=43; }
        if(Find<Button>(window,"CancelOperationButton") is {} cancel) cancel.Visibility=step==1?Visibility.Visible:Visibility.Collapsed;
        Text(window,"StatusText",step switch { 1=>"Synthetic download: 43% — no network activity.", 2=>"Synthetic applying phase — no installer or file changes.", 3=>"Synthetic failure: package verification failed. Recovery export: "+FakePath("Recovery exports/2026-10-03-synthetic"), _=>"Synthetic complete. Application remains closed." });
        if(step==3 && Find<TextBox>(window,"RecoveryText") is {} recovery) { recovery.Text=FakePath("Recovery exports/2026-10-03-synthetic"); recovery.Visibility=Visibility.Visible; }
    }
    public sealed record RemovalTarget(string Name,string Description,bool CanRemoveApplication,bool CanRemoveData);
    public sealed class RemovalRow { public RemovalTarget Target {get;set;}=new("","",false,false); public bool RemoveApplication {get;set;} public bool RemoveData {get;set;} public string Details {get;set;}=""; }
    public sealed record DiscoveryCandidate(string AppName,string SupportSummary,string Scope);
    public sealed class DiscoveryRow
    {
        public DiscoveryCandidate Candidate {get;set;}=new("","","");
        public string Path {get;set;}="";
        public bool Selected {get;set;}
        public bool CanSelect {get;set;}=true;
        public string VerificationText {get;set;}="";
        public Brush VerificationBrush {get;set;}=Brushes.Black;
        public string LocationKind {get;set;}="";
        public string ScopeLabel {get;set;}="";
        public string SupportText => Candidate.SupportSummary;
    }
    private static void Show(Window child,Window owner) { child.Owner=owner; child.Show(); }
    private static void TogglePassword(Window window,string passwordName,string textName)
    {
        var password=Find<PasswordBox>(window,passwordName)!; var text=Find<TextBox>(window,textName)!;
        bool show=text.Visibility!=Visibility.Visible; text.Text=password.Password; text.Visibility=show?Visibility.Visible:Visibility.Collapsed; password.Visibility=show?Visibility.Collapsed:Visibility.Visible;
    }
    private static T? Find<T>(Window window,string name) where T:class => window.FindName(name) as T;
    private static void Text(Window window,string name,string value) { if(Find<TextBox>(window,name) is {} box) box.Text=value; else if(Find<TextBlock>(window,name) is {} block) block.Text=value; }
    private static string FakePath(string name) => Path.Combine(Root,name.Replace('/',Path.DirectorySeparatorChar));
    internal static string SampleExecutable() => FakeFile("VACS.exe");
    private static string FakeFolder(string name) { var path=FakePath(name); Directory.CreateDirectory(path); return path; }
    private static string FakeFile(string name) { var path=FakePath(name); Directory.CreateDirectory(Root); if(!File.Exists(path)) File.WriteAllText(path,"INERT UI FIXTURE — NOT AN EXECUTABLE"); return path; }
}
