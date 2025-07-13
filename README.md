# CreatioHelper

CreatioHelper is a collection of utilities designed to simplify everyday maintenance tasks when working with the Creatio platform.  The repository contains several components built on top of the shared **CreatioHelper.Core** library:

* **CreatioHelper** – cross‑platform desktop application built with Avalonia.
* **CreatioHelper.Agent** – lightweight ASP.NET Core service that exposes monitoring and management APIs.
* **CreatioHelper.CLI** – console tool for headless operations.

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

# Use the command line interface
 dotnet run --project CreatioHelper.CLI --help
```

Published binaries can be produced with `dotnet publish` if desired.
