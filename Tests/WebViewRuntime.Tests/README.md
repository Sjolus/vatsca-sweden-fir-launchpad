# WebView2 runtime facade checks

Run `dotnet run --project Tests/WebViewRuntime.Tests/WebViewRuntime.Tests.csproj -c Release`.

The harness links the production runtime facade and supplies synthetic version readers and a prerequisite-service double. It checks unavailable-runtime handling and forwards the shared WebView2 route, progress, cancellation, failures and restart notifications. It never probes the installed runtime, creates a browser, downloads a package or starts setup.

`Tests/SoftwarePrerequisite.Tests` covers the production Microsoft adapter, including download validation, installer verification and restart handling. Browser compatibility and actual runtime installation still need a separately authorized interactive test.
