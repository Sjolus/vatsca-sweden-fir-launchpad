# WPF layout checks

This fixture uses production XAML, themes and presentation models with synthetic settings
and inert button handlers. It covers the main window, Settings, Controller profile,
maintenance, discovery, recent activity, EuroScope management, fresh client setup, the setup wizard and
GNG cleanup, GNG setup/update, reporting choices, last-sent summaries and manual report previews/receipts. Reporting views use synthetic IDs and inert handlers, without linking storage or transport. GNG file filtering and asynchronous preview navigation use the production code with a synthetic text provider. Installation,
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

`--check-application-controls` runs the focused main-window matrix, including
stopped, running, mixed, busy, restart-required and browser-without-backend states. Add `--snapshots`
with an ignored output directory to render those controls without showing windows.
All process actions remain inert; normal-close confirmation and process identity
belong to the application-control service tests.

`--check-workspaces` covers the five embedded Setup sections, including preference
changes and save failures with synthetic settings. Add `--snapshots` to capture the
Apps and Setup pages in both themes. Preference events are observed in memory;
the fixture never saves settings or opens reporting windows.
First-run captures include completely blank and partly configured installations, with
inline guidance, prerequisite links and the same embedded sections as normal use.

`--check-gng` runs the focused GNG matrix, including a 150-file investigation and
asynchronous navigation. Add `--snapshots` to capture the embedded summary, Files
workspace and compact diff without showing windows.

Add `--snapshots artifacts/ui-review` to render the measured application and workflow layouts
as PNGs. Cleanup and GNG fresh/existing start, sign-in waiting, package/reference download progress, preparation between downloads, cancellation, review,
completion and runtime-fallback states capture both themes at default and minimum size,
without showing windows or initializing a browser. The website area is a labelled inert
placeholder; these captures do not establish authenticated browser behavior.
Keep generated files in ignored output folders.

GNG review checks also link the production EuroScope-blocking presentation with synthetic process states. They verify the visible reason and disabled install/restore actions, without enumerating or starting processes. Clearing the block never starts installation.

## Coverage

The matrix checks light/dark themes, minimum/default sizes, compact/expanded rows,
configured/missing paths, wizard steps and scaled content. Text-size cases use the same
100%/125%/150% presentation resources as the app, independently of whole-content scaling.
Assertions cover:

- Equal row heights and direct daily actions. Status text stays selectable, with full
  diagnostic tooltips, accessibility descriptions and inert Ctrl+C command routing.
- Separate Show/Close controls and reviewed bulk actions remain readable at each
  text size, with keyboard access, operation gating and no controls for folders or Launchpad itself.
- Text-size changes retain density and selected EuroScope profile. Apps/Setup tabs and
  primary actions remain reachable at minimum size; compact layout and keep-on-top choices live in Preferences.
- Applications, Swedish GNG, Controller profile, Preferences and Activity remain inside
  Setup with its section navigation visible. Neither application list uses row overflow menus.
- First-setup guidance stays inline. Next/Later requests open no windows, unfinished
  configuration remains optional, and guided navigation leaves preference errors visible.
- Preference population emits no change event. Individual edits preserve unrelated
  settings; failed saves keep the chosen values and expose a reachable retry action.
- The embedded controller form retains its synthetic draft through Setup navigation
  and refresh. Plugin choices live in Applications; preview/save handlers are inert, and their
  fixed footer stays reachable while the fields scroll.
- VatEFS and SAAMAN switches emit requests only on a click, with no writes on population
  or refresh. Inline plugin feedback covers blocked, downloading, applying, save-retry and
  missing-runtime states. Cancel stays reachable during an operation; other mutations remain locked.
- SAAMAN uses direct install/update and profile actions. The shared production action
  guards disable changes during work, restart requirements or a detected EuroScope
  process. Restoring an earlier DLL retains its separate review and confirmation.
- Recent activity has a reachable Close action, scrollable details, selectable recovery
  locations and separate GNG, vendor-recovery and no-backup examples. Its handlers are
  inert; recovery eligibility, filesystem safety and persistence belong to service tests.
- Progress bindings, long diagnostics, restart notices and status changes.
- Scrolling, fixed footers, review positioning and keyboard-focus styling.
- Text/control contrast, resolved resources and WPF binding errors.
- Setup summaries, application descriptions and optional guide links.
- The shared profile preview keeps synthetic credentials masked until explicitly revealed,
  reuses its prepared preview, and retains both footer actions at larger text sizes.
- All six setup pages, the explicit GNG step and its restart guard; advancing the guide
  never implies a download or installation.
- Compact GNG ready, attention and EuroScope-blocked summaries, with the install action
  beside the summary. View details exposes destinations, file changes and list settings;
  failed rechecks clear stale comparisons while keeping correction options reachable.
- Explicit Download Swedish GNG and retry/stop controls, a readable origin, download
  progress and cancellation reachability; simulated states never perform downloads.
- Matching saved-package lookup and cached review remain inert and require an explicit install action.
  Download fresh copies stays in download options and explicitly bypasses reuse;
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
- Production GNG review filtering: path/action search, changed profiles, changed plugins
  and all files, with honest empty-result messages. The 150-file case checks equal row
  heights, virtualization and a file list without an outer scrollbar. Options and
  Technical notes have their own tabs.
- Production asynchronous file navigation with a synthetic provider: filtered/sorted
  order, Previous/Next boundaries, Back retaining search/selection/scroll, delayed
  completions, cancellation and superseded plans. The interactive host has a direct
  **GNG: investigate 150 files** entry.
- The production file-preview control, preview model and pure text-diff helper use
  synthetic sanitized text: Changes and Before / After tabs, exact proposed text,
  selectable monospace panes, highlighted additions/removals, Next change navigation,
  binary/personal/large summaries and minimum-size scrolling.
- At 150% text and 600 × 400 content, Next change must visibly show the changed line;
  selecting an off-screen line alone does not pass.
- Synthetic Lists.txt and Plugin.txt previews keep `m_X`, `m_Y` and `m_Visible`
  while showing changed package columns. File rows stay one line, with full path/action
  tooltips and a reachable View button at minimum size. This checks
  presentation of the expected merged result; service tests verify the merge itself.
- List-layout preservation starts checked and is available before preview and after
  a synthetic preservation failure. The fixture checks disabled busy states, opting
  out to package defaults and rebuilding the plan when the preview changes.
- Metadata-only package choice notices before download: Update Only plus a Full cleanup
  reference for a synthetic existing installation; Full for a fresh/incomplete setup.

Checks measure each content root and realize its row templates; measuring an unopened
`Window` alone skips the content. The fixture reserves space for native frames and scales
content within fixed bounds to stress clipping. These are layout simulations. Actual DPI,
screen readers and installed workflows require desktop testing.

## Interactive preview

Open `Tests/UiReview/bin/Release/net9.0-windows/Launchpad.UiReview.exe` from the repository
root. The **UI review — synthetic** window lets you select a screen, theme, size, configured
or blank paths, 100%/125%/150% text size and a separate 100%/150%/200% layout simulation.
Text size uses production resources; layout simulation scales the whole content within
fixed bounds. Controls simulate progress, errors, review steps and restart notices.

The fixture does not link production startup, persistence, credentials or installer
services. Buttons change in-memory state rather than launching applications, opening
links or changing the machine. Synthetic files used for path presentation stay under
the fixture's ignored output directory.
