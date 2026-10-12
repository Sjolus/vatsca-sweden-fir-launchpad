# Presentation and process identity tests

Run `dotnet run --project Tests/Presentation.Tests/Presentation.Tests.csproj -c Release`.

Uses in-memory settings, synthetic display rectangles and process identities. Checks optional-tool preferences, compatibility with older settings, text-size bounds, window placement after a monitor change, and rejection of PID reuse or another installation. Does not enumerate processes, open windows, signal applications or access saved user settings. Native window placement and application interaction need separate desktop validation.
