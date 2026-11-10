# EuroScope launch-set and instance checks

Run `dotnet run --project Tests/EuroScopeInstances.Tests/EuroScopeInstances.Tests.csproj -c Release`.

The harness links the production `EuroScopeLaunchPlanner`, `EuroScopeInstanceTracker` and settings model. It covers ordering, disabled/already-running/skipped entries, first-start delay handling, duplicate-profile warnings, normalization bounds, receipt round-trips, pruning of exited processes, bounded and malformed receipts, full-identity matching of untracked copies, legacy launch-set migration, `AppSettings.Copy()` list independence and settings JSON compatibility.

Process liveness is injected and receipts live in a temporary folder. It never starts, inspects or closes EuroScope and does not read the user's settings or `EuroScope.instances.json`. The editor window's layout and validation are covered by `Tests/UiReview --check-application-controls`. Delayed sequencing, cancellation, per-instance Show/Close and a successful editor save need authorized desktop testing.
