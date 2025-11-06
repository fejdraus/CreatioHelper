# CreatioHelper

**CreatioHelper** is a comprehensive cross-platform tool for managing Terrasoft Creatio installations. It streamlines development workflows through automation of routine operations and provides both GUI and API interfaces.

## Key Features

### Desktop Application
- **Package Management**: Install, update, and remove Creatio packages with automated workflows
- **Schema Rebuild**: Regenerate and compile schema sources via WorkspaceConsole integration
- **IIS Management**: Automatic start/stop of IIS sites and application pools during operations
- **Redis Integration**: Automatic cache clearing after deployments
- **Multi-Server Synchronization**: Synchronize changes across multiple Creatio instances
  - Traditional file copy synchronization
  - **External Syncthing Integration**: Connect to external Syncthing instance via REST API
    - Real-time synchronization monitoring via Events API
    - Multi-folder support (e.g., separate folders for Terrasoft.WebApp and bin)
    - Pause/Resume folders during operations
    - Direct link to Syncthing Web UI
  - Automatic remote server management (IIS sites/pools)

### Agent Service
- **HTTP API**: Remote control and monitoring of Creatio instances
- **Automation**: Scriptable deployments and operations
- **Built-in Sync** *(in development)*: Native Syncthing-inspired sync protocol implementation ([planned features](./SYNC_README.md))

For detailed usage instructions, see the [User Guide](./USER_GUIDE.md).

## Project Structure

All source projects are located in `src/`, and test projects in `tests/`. Main projects:

### 🧠 Business Logic and Models

- `CreatioHelper.Domain`: domain entities and enums, independent of other layers.
- `CreatioHelper.Application`: use-cases (commands and handlers via MediatR), logic interfaces.

### 🧱 Infrastructure and Shared Utilities

- `CreatioHelper.Infrastructure`: implementations of interfaces, interaction with IIS, file system, etc.
- `CreatioHelper.Shared`: utilities for file operations, logging, and configuration.
- `CreatioHelper.Contracts`: DTO classes for data exchange between Agent and other parts.

### 🖥️ UI and Services

- `CreatioHelper.Desktop`: Avalonia-based GUI application.
- `CreatioHelper.Agent`: minimal ASP.NET Core web service providing remote control API.

## Build and Run

### Requirements:

- .NET 8 SDK ([https://dotnet.microsoft.com](https://dotnet.microsoft.com))
- Git
- Windows with IIS (for IIS management features, can also work in Folder Mode without IIS)
- Redis (optional, for cache management)
- [Syncthing](https://syncthing.net/) (optional, for real-time distributed file synchronization)

### Build the solution:

```bash
dotnet build CreatioHelper.sln
```

### Run GUI (Desktop):

```bash
dotnet run --project src/CreatioHelper.Desktop
```

### Run API (Agent):

```bash
dotnet run --project src/CreatioHelper.Agent
```

## CI/CD

Build and test pipelines are configured with GitHub Actions:

- `dotnet-build.yml` — CI pipeline: build, test, multiplatform (Windows and Linux)
- `release-build.yml` — CD pipeline: package release builds (`.zip`) for Windows and Linux, upload to GitHub Releases

The Desktop application is built with Avalonia, providing true cross-platform support for Windows, Linux, and macOS.

## Testing

Testing is done using `xUnit`, `Moq`, and `coverlet`:

- `tests/CreatioHelper.Tests`: unit tests for business logic, utilities, and infrastructure.
- `tests/CreatioHelper.Agent.Tests`: unit tests for Agent API controllers.

Run tests:

```bash
dotnet test
```

## Architecture

The project follows Clean Architecture principles:

- `Domain` — models and business logic without dependencies.
- `Application` — use-case interfaces and commands.
- `Infrastructure` — implementation of external integrations.
- `Desktop` and `Agent` — UI and API clients.

## Documentation

- **[User Guide](./USER_GUIDE.md)** - Complete guide for using CreatioHelper Desktop application
- **[Built-in Sync Documentation](./SYNC_README.md)** - Planned native Syncthing-inspired synchronization for Agent (in development)

## Acknowledgements

Special thanks to the members of the **PeaceTeam** from **Banza** for their invaluable contributions in development and testing:

- **Oleksandr**
- **Anna**
- **Roman**
- **Oleksandr**
- **Viacheslav**
- **Dasha**
- **Anton**
- **Vadym**
- **Roma**
- **Vitia**
- **Olena**
- **Vitalii**
- **Dmytro**
- **KotikSmerit**
- **Olena**

---

## 🇺🇦 💙💛 Support the Armed Forces of Ukraine (ZSU)

We stand with Ukraine in its fight against Russian aggression.
All funds raised via the links below will be **fully transferred to support the Armed Forces of Ukraine (Zbroini Syly Ukrainy, ZSU)** and defenders on the front line.

[![Support on Patreon](https://img.shields.io/badge/Support%20on-Patreon-orange)](https://www.patreon.com/yourpatreonlink)
[![Support on Buy Me a Coffee](https://img.shields.io/badge/Support%20via-Buy%20Me%20a%20Coffee-yellow)](https://www.buymeacoffee.com/yourcoffeelink)

<!-- [![Support on Ko-fi](https://img.shields.io/badge/Support%20via-Ko--fi-red)](https://ko-fi.com/H2H31J97O4) -->

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/H2H31J97O4)

> 💙💛 **All contributions will be sent directly to support ZSU defenders.**

**Glory to Ukraine! Glory to the Heroes!** 🇺🇦 💙💛
