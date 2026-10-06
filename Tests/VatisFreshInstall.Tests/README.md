# vATIS fresh installation regression checks

Run `dotnet run --project Tests/VatisFreshInstall.Tests/VatisFreshInstall.Tests.csproj -c Release` on Windows.

The harness uses synthetic ZIP/Setup bundles, temporary files and injected network, identity and process probes. Coverage includes package and embedded-payload identity, publisher checks, download limits, existing-data protection, cancellation, concurrent operations and post-install verification.

Add `-- --inspect-setup <absolute-path-to-official-vATIS-Setup.exe>` to inspect a downloaded package against the production size/hash pin, Authenticode publisher, product version and embedded-package digest. Neither mode executes an installer. Signature verification may contact certificate revocation services.

The supported Setup contains Velopack **0.0.1251**, shipped with vATIS **4.1.0-beta.19**. Its [parser](https://github.com/velopack/velopack/blob/0.0.1251/src/bins/src/setup.rs) accepts `--silent --installto`; silent setup suppresses normal first run but still invokes the install hook. Native hook behavior and Windows registration require integration testing.

Fresh Setup must reject any existing root, including an empty or data-only folder. New Setup versions require adapter review before changing the package pin.
