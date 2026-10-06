# Settings persistence regression checks

Run `dotnet run --project Tests/Settings.Tests/Settings.Tests.csproj -c Release`.

The harness links the production settings service and model, using an injected in-memory writer. It checks successful saving, I/O/access/security failures, retained session values and retrying with later edits. Filesystem persistence and WPF error presentation need separate testing.
