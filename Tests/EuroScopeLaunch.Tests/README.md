# EuroScope launch checks

Run `dotnet run --project Tests/EuroScopeLaunch.Tests/EuroScopeLaunch.Tests.csproj -c Release`.

The harness inspects the production `ProcessStartInfo` using temporary folders and profile files. It covers configured and fallback working directories, selected/no-profile arguments, spaces and Swedish characters, missing/relative paths, and preservation of the global working directory. It checks launch configuration without starting EuroScope.
