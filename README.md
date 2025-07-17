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
