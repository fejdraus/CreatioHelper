# CreatioHelper

CreatioHelper is a collection of utilities designed to simplify everyday maintenance tasks when working with the Creatio platform. The repository contains several components built on top of the shared **CreatioHelper.Core** library:

- **CreatioHelper** – cross‑platform desktop application built with Avalonia.
- **CreatioHelper.Agent** – lightweight ASP.NET Core service that exposes monitoring and management APIs.

## Building

This solution targets **.NET 8**. Make sure the .NET 8 SDK is installed, then build everything using:

```bash
# Restore packages and compile all projects
 dotnet build CreatioHelper.sln
```

## Running

Each component can be executed directly with `dotnet run`:

```bash
# Launch the desktop GUI
 dotnet run --project CreatioHelper

# Start the agent service
 dotnet run --project CreatioHelper.Agent
```

Published binaries can be produced with `dotnet publish` if desired.

## Releases

Release builds for Windows and Linux are generated automatically. When a GitHub release is published, the _Build Release_ workflow publishes self-contained binaries for both platforms and uploads the resulting ZIP archives as build artifacts.
