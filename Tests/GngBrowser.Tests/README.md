# GNG browser policy checks

Run `dotnet run --project Tests/GngBrowser.Tests/GngBrowser.Tests.csproj -c Release`. The synthetic DOM checks require Node.js 20 or later on `PATH`; no npm packages are used.

The dependency-free harness links the production URL and filename policy. It checks HTTPS navigation, Cloudflare's inline `about:srcdoc` frame requirement without allowing it as top-level navigation, sign-in-origin display, AeroNav download-host boundaries, package names and complete-package classification. It never creates a browser, contacts a website or accesses saved browser data.

The harness sends the exact production inspection/click scripts to Node.js and runs them against synthetic table and link objects. Cases cover explicit Full/Update-Only selection, AIRAC/revision/release ordering, disabled latest packages, conflicting rows, changed layouts, link origins, read-only inspection, changed selections before clicking and repeated download attempts. Request-state tests cover identity retention when the download precedes the click callback, stale navigation errors, cancellation, reference handoff and explicit retry. These checks do not establish compatibility with AeroNav's authenticated DOM or actual WebView2 events.

Authenticated sign-in, popup behavior, download events and browser-runtime failures require a separately authorized interactive test.

Navigation regressions use the production tracker and request state with synthetic WebView event sequences. They cover the gap between Navigate and NavigationStarting, overlapping page loads, late old-document callbacks, redirects with the same navigation ID, cancelled requests, bounded inspection retries, and both stages of a paired download. No browser is initialized by these tests.
