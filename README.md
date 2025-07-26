# CreatioHelper

CreatioHelper is a graphical user interface (GUI) for the Creatio WorkspaceConsole. It simplifies and automates the process of managing packages, builds, and deployments within the Creatio platform:

- **CreatioHelper** – cross-platform graphical application for managing WorkspaceConsole, developed with AvaloniaUI.
- **CreatioHelper.Agent** – lightweight background service that provides APIs for monitoring and managing the Creatio environment. This agent can run on a server in the background and accept commands via HTTP.

Projects are organized under the `src` folder while tests live under `tests`:

```
src/CreatioHelper.Desktop/      # GUI application
src/CreatioHelper.Agent/        # Background web API
src/CreatioHelper.Domain/       # Domain models
src/CreatioHelper.Application/  # Use cases and interfaces
src/CreatioHelper.Infrastructure/ # Infrastructure services
```

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
 dotnet run --project src/CreatioHelper.Desktop

# Start the agent service
 dotnet run --project src/CreatioHelper.Agent
```

Published binaries can be produced with `dotnet publish` if desired.

## Architecture

- **Domain** – core entities and enums shared across the solution
- **Application** – interfaces, Mediator handlers and use cases
- **Infrastructure** – implementations of external integrations (IIS, file system, configuration)
- **Desktop** – Avalonia UI that depends only on the Application layer
- **Agent** – background web API using Application and Infrastructure
- **Contracts** – DTOs for API requests and responses
- **Shared** – common helpers

Dependency injection is configured via `AddApplication()` and `AddInfrastructure()` extension methods. Commands and queries are executed through a simple Mediator.
Tests reside under `tests/CreatioHelper.UnitTests` using xUnit and Moq.

## Wiki

For more information, please refer to the [Read the User Guide](./USER_GUIDE.md).

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

## 🇺🇦 Support Ukraine Armed Forces (ЗСУ)

We stand with Ukraine in its fight against Russian aggression.
All funds raised via the links below will be **fully transferred to support the Ukrainian Armed Forces (Збройні Сили України, ЗСУ)** and defenders on the front line.

[![Support on Patreon](https://img.shields.io/badge/Support%20on-Patreon-orange)](https://www.patreon.com/yourpatreonlink)
[![Support on Buy Me a Coffee](https://img.shields.io/badge/Support%20via-Buy%20Me%20a%20Coffee-yellow)](https://www.buymeacoffee.com/yourcoffeelink)
[![Support on Ko-fi](https://img.shields.io/badge/Support%20via-Ko--fi-red)](https://ko-fi.com/yourkofilink)

> 💙💛 **All contributions will be sent directly to support ZSU defenders.**

**Слава Україні! Героям слава!** 🇺🇦
