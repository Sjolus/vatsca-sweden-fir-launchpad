# WPF layout checks

This fixture uses production XAML, themes and presentation models with synthetic settings
and inert button handlers. It covers the main window, Settings, Controller profile,
maintenance, discovery, EuroScope management, fresh client setup and the setup wizard.

## Run the layout checks

Build and run on Windows:

```powershell
dotnet build Tests/UiReview/UiReview.csproj -c Release
dotnet Tests/UiReview/bin/Release/net9.0-windows/Launchpad.UiReview.dll --check
```

The check measures content without showing windows and returns a nonzero exit code for
layout or binding failures. Results go to `layout-validation.txt` and errors to
`fixture-errors.log` beside the built executable. `--validate-layouts` is an alias for
`--check` and is used in CI.

Add `--snapshots artifacts/ui-review` to render the measured main-window layouts as PNGs.
Keep generated files in ignored output folders.

## Coverage

The matrix checks light/dark themes, minimum/default sizes, compact/expanded rows,
configured/missing paths, wizard steps and scaled content. Assertions cover:

- Equal row heights, reachable actions and the selected application's details panel.
- Progress bindings, long diagnostics, restart notices and status changes.
- Scrolling, fixed footers, review positioning and keyboard-focus styling.
- Text/control contrast, resolved resources and WPF binding errors.
- Setup summaries, application descriptions and optional guide links.

Checks measure each content root and realize its row templates; measuring an unopened
`Window` alone skips the content. The fixture reserves space for native frames and scales
content within fixed bounds to stress clipping. These are layout simulations. Actual DPI,
screen readers and installed workflows require desktop testing.

## Interactive preview

Open `Tests/UiReview/bin/Release/net9.0-windows/Launchpad.UiReview.exe` from the repository
root. The **UI review — synthetic** window lets you select a screen, theme, size, configured
or blank paths, and 100%/150%/200% content scaling. Controls simulate progress, errors,
review steps and restart notices.

The fixture does not link production startup, persistence, credentials or installer
services. Buttons change in-memory state rather than launching applications, opening
links or changing the machine. Synthetic files used for path presentation stay under
the fixture's ignored output directory.
