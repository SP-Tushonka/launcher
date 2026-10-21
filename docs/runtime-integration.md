# Optional runtime integration for SPT 5

This contribution targets Windows x64 and EFT 1.1.0.1.46911. It preserves upstream profiles, server-defined mod pages and bundle downloads.

## Features

- Persistent automatic local-server startup (enabled by default), or a saved remote hostname/port.
- Explicit certificate fingerprint trust for a selected server; requests remain on that server origin.
- Optional BepInXSPT loading and per-game Windows Filtering Platform connection protection.
- Profile event/weather/progression controls, runtime status monitoring, startup recovery and normal shutdown.
- Errors remain visible until dismissed; ordinary informational notices remain timed.

## External runtime requirement

The launcher uses a separate `runtime/eft-runtime.exe` beside the launcher. Its request/reply contract is defined in `SPTushonka.Core/Runtime/OfflineRuntime.cs`. The matching runtime must implement backend startup/attachment, game preparation, launch, stop and release, and advertise the optional capabilities it supports. This PR does not provide native game patches, a runtime executable, BepInXSPT, game assets, player profiles or certificates. A launcher-only build is not a complete playable installation.

Server process ownership limits which process may be stopped; it does not prevent using a compatible externally started server. External servers are left running. The selected endpoint is carried through runtime transport and protection requests.

## Build and checks

Use the upstream .NET 10 prerequisites. Restore the pinned formatter with `dotnet tool restore`, then run `dotnet csharpier format .`. Build with `dotnet build SPTushonka.Launcher/SPTushonka.Launcher.csproj -c Release`.

Self-contained regression projects under `tests/` cover runtime IPC, recovery, saved settings, remote endpoint validation, HTTP session handling, notifications, managed-host selection and connection protection. Run a fixture with `dotnet run --project tests/RuntimeFixtures/RuntimeFixtures.csproj -c Release`; repeat for the other fixture projects. These are synthetic checks and do not establish game compatibility. `EnvironmentAcceptance` is an explicit integration test and must not be run against an active session.

`tools/package-product-release.py` builds or assembles a separate Windows release from explicitly supplied launcher/runtime inputs and manifests. Product publishing requires a fresh absolute output directory and does not clear an existing build. It does not start the launcher, server or game.
