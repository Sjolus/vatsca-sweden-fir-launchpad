using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal enum LaunchSetFixtureState { Sets, NoSets }

internal static partial class UiFixture
{
    internal static readonly Guid LaunchSetActiveId = new("0d6f3a52-77a4-4f0b-9a0e-6f2a4c1d9b11");
    internal static readonly Guid LaunchSetEmptyId = new("7a1c2e84-3b5d-4c6e-8f90-1a2b3c4d5e60");

    // Synthetic paths only; the editor never opens profiles or inspects processes.
    internal static EuroScopeLaunchSetWindow CreateLaunchSetEditor(LaunchSetFixtureState state, bool minimum)
    {
        const string data = @"C:\Synthetic\EuroScope";
        var profiles = new ProfileOption[]
        {
            new("— No profile —", null),
            new("ESAA TWR Ö", data + @"\ESAA TWR Ö.prf"),
            new("ESAA APP ACC", data + @"\ESAA APP ACC.prf"),
            new("ESAA TOPSKY — Stockholm approach long profile name", data + @"\ESAA TOPSKY — Stockholm approach long profile name.prf")
        };
        EuroScopeLaunchSetDefinition[] sets = state == LaunchSetFixtureState.NoSets ? [] :
        [
            new(LaunchSetActiveId, "Evening — Stockholm & Göteborg", [
                new(Guid.NewGuid(), profiles[1].FilePath!, 30, true),
                new(Guid.NewGuid(), profiles[3].FilePath!, 12, true),
                new(Guid.NewGuid(), @"C:\Synthetic\Moved\ESAA OBS.prf", 5, false)
            ]),
            new(LaunchSetEmptyId, "Empty set", []),
            new(Guid.NewGuid(), "Weekend", [new(Guid.NewGuid(), profiles[2].FilePath!, 0, true)])
        ];
        var window = new EuroScopeLaunchSetWindow(profiles, sets, state == LaunchSetFixtureState.NoSets ? null : LaunchSetActiveId, profiles[1]);
        window.Title += " — synthetic";
        if (minimum) { window.Width = window.MinWidth; window.Height = window.MinHeight; }
        return window;
    }
}
