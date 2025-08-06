# CreatioHelper

**CreatioHelper** is a tool for managing Terrasoft Creatio installations via UI or API. It provides:

- A graphical application for managing Creatio installations (Desktop).
- A background service (Agent) with HTTP API for automation.
- A clean architecture with separated business logic, infrastructure, and domain model.

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

- `dotnet-build.yml` — CI pipeline: build, test, multiplatform (Windows and Linux).
- `release-build.yml` — CD pipeline: package release builds (`.zip`) for Windows and Linux, upload to GitHub Releases.

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

## Wiki

For more information, please refer to the [Read the User Guide](./USER_GUIDE.md).

## Acknowledgements

Special thanks to the members of the **PeaceTeam** from **Banza** for their invaluable contributions in development and testing:

- **Oleksandr Sarnatskyi**
- **Lavreniuk Anna**
- **Roman Bezgubenko**
- **Alexandr Onatskiy**
- **Viacheslav Medliakovskiy**
- **Dasha**
- **Anton Slobodenyuk**
- **Vadim Konstantinov**
- **Roma Nogachevskiy**
- **Vitya Orlov**
- **Alena Hoptar**
- **Vitaliy Polyakov**
- **Dmitry Zusko**
- **Anthony Kukushkin**
- **Olena Toporets**

---

## 🇺🇦 Support the Armed Forces of Ukraine (ZSU)

We stand with Ukraine in its fight against Russian aggression.
All funds raised via the links below will be **fully transferred to support the Armed Forces of Ukraine (Zbroini Syly Ukrainy, ZSU)** and defenders on the front line.

[![Support on Patreon](https://img.shields.io/badge/Support%20on-Patreon-orange)](https://www.patreon.com/yourpatreonlink)
[![Support on Buy Me a Coffee](https://img.shields.io/badge/Support%20via-Buy%20Me%20a%20Coffee-yellow)](https://www.buymeacoffee.com/yourcoffeelink)

<!-- [![Support on Ko-fi](https://img.shields.io/badge/Support%20via-Ko--fi-red)](https://ko-fi.com/H2H31J97O4) -->

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/H2H31J97O4)

> 💙💛 **All contributions will be sent directly to support ZSU defenders.**

**Glory to Ukraine! Glory to the Heroes!** 🇺🇦
