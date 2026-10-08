# WPF layout checks

This fixture uses production XAML, themes and presentation models with synthetic settings
and inert button handlers. It covers the main window, Settings, Controller profile,
maintenance, discovery, EuroScope management, fresh client setup, the setup wizard and
GNG cleanup and GNG setup/update. Both GNG windows use inert file rows; installation,
archive and restore cannot touch files. The production GNG browser host is a lazy
`ContentControl` that remains empty here; WebView2 and download/runtime services are not linked.

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

Add `--snapshots artifacts/ui-review` to render the measured main-window and GNG layouts
as PNGs. Cleanup and GNG fresh/existing start, sign-in waiting, package/reference download progress, preparation between downloads, cancellation, review,
completion and runtime-fallback states capture both themes at default and minimum size,
without showing windows or initializing a browser. The website area is a labelled inert
placeholder; these captures do not establish authenticated browser behavior.
Keep generated files in ignored output folders.

GNG review checks also link the production EuroScope-blocking presentation with synthetic process states. They verify the visible reason, disabled install/restore actions and renewed confirmation after the block clears, without enumerating or starting processes.

## Coverage

The matrix checks light/dark themes, minimum/default sizes, compact/expanded rows,
configured/missing paths, wizard steps and scaled content. Assertions cover:

- Equal row heights, reachable actions and the selected application's details panel.
- Progress bindings, long diagnostics, restart notices and status changes.
- Scrolling, fixed footers, review positioning and keyboard-focus styling.
- Text/control contrast, resolved resources and WPF binding errors.
- Setup summaries, application descriptions and optional guide links.
- Optional GNG setup after Finish, including its unchecked default and restart guard.
- GNG destination/recovery readability, explicit confirmation, package-change reset,
  minimum-size file review, runtime fallback routes and guarded main-window actions.
- Explicit Download Swedish GNG and retry/stop controls, a readable origin, download
  progress and cancellation reachability; simulated states never perform downloads.
- Matching saved-package lookup and cached review remain inert and require confirmation.
  Download fresh copies fits beside the default action and explicitly bypasses reuse;
  both actions lock during lookup, pending requests and active work.
- Stop during reference preparation, retained-update reference retry/import, and Start
  over remain reachable without automatically enabling the installation action.
- Prominent operation status/progress above the browser and review, with nearby Stop
  or Cancel; long primary/reference errors wrap and remain selectable and scrollable.
- Linked production browser presentation keeps sign-in interactive, hides and disables
  the native browser surface during both transfers and preparation, and restores it
  after cancellation or failure. An opaque replacement panel and dimmed toolbar keep
  attention on progress and Stop/Cancel outside their bounds. The fixture uses an inert
  placeholder and never creates native browser content.
- Production GNG review filtering (linked shared partial and file model): changes-only
  by default, counts, optional unchanged/personal entries, reset for each plan and
  an explicit zero-change message. Snapshots include changes-only, all and zero-change reviews.
- The production file-preview dialog, preview model and pure text-diff helper use
  synthetic sanitized text: Changes and Before / After tabs, exact proposed text,
  selectable monospace panes, binary/personal/large summaries and minimum-size scrolling.
- Synthetic Lists.txt and Plugin.txt previews keep `m_X`, `m_Y` and `m_Visible`
  while showing changed package columns. The merged-list review row wraps its action
  and explanation, and its View button remains reachable at minimum size. This checks
  presentation of the expected merged result; service tests verify the merge itself.
- List-layout preservation starts checked and is available before preview and after
  a synthetic preservation failure. The fixture checks disabled busy states, opting
  out to package defaults and clearing confirmation when the preview changes.
- Metadata-only package choice notices before download: Update Only plus a Full cleanup
  reference for a synthetic existing installation; Full for a fresh/incomplete setup.

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
