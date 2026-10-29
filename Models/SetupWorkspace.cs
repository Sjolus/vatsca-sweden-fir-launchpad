namespace VatscaUpdateChecker.Models;

public sealed record SetupWorkspaceApplication(string Key, string Name, string Path, string Status,
    string ManageText, bool NotUsed = false, SetupPluginOption? Plugin = null,
    SetupPluginFeedback? Feedback = null, bool CanManage = true)
{
    public string LocationText => string.IsNullOrWhiteSpace(Path) ? "No location chosen" : Path;
    public string LocationAction => string.IsNullOrWhiteSpace(Path) ? "Use existing…" : "Location…";
    public string Purpose => ApplicationDescriptions.For(Key);
    public string UsageAction => NotUsed ? "Use this app" : "I don't use this";
}

public sealed record SetupPluginOption(bool Enabled, string Label, string Help, bool CanChange = true,
    string? BlockReason = null, bool NeedsGng = false);
public sealed record SetupPluginFeedback(string Message, bool Warning = false, bool Busy = false,
    double? Percent = null, bool CanCancel = false, bool CanRetrySave = false, bool RuntimeRequired = false);

public enum SetupWorkspaceAction { Discover, ChangeLocation, Manage, ControllerProfile, Saaman, SaamanGuide, Fonts, Gng, Cleanup, TogglePlugin, PluginCancel, PluginRetrySave, PluginRuntime, GuideNext, GuideLater, GngImport, GngFresh, GngCheck, GngCancel }
public sealed class SetupWorkspaceRequest(SetupWorkspaceAction action, string? application = null, bool? enabled = null) : EventArgs
{
    public SetupWorkspaceAction Action { get; } = action;
    public string? Application { get; } = application;
    public bool? Enabled { get; } = enabled;
}
