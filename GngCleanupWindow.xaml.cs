using System.Windows;

namespace VatscaUpdateChecker;

public partial class GngCleanupWindow : Window
{
    public GngCleanupControl Cleanup { get; }
    public bool AttemptedChanges => Cleanup.AttemptedChanges;

    public GngCleanupWindow(string root, string? selectedProfile, IEnumerable<string> protectedPaths,
        string? oldZip = null, string? newZip = null, string? recoveryFolder = null)
    {
        InitializeComponent();
        Cleanup = new(root, selectedProfile, protectedPaths, oldZip, newZip, recoveryFolder);
        Content = Cleanup;
        NameScope.SetNameScope(this, NameScope.GetNameScope(Cleanup));
        Cleanup.CloseRequested += (_, _) => Close();
        Closing += (_, args) => args.Cancel = !Cleanup.CanDismiss;
    }
}
