# Repository Guidelines

## Project Overview

`CreatioHelper` is a .NET 10 / C# multi-host management platform for Terrasoft Creatio (an enterprise low-code platform). It ships five hosts sharing one Clean Architecture core:

- **Agent** — ASP.NET Core Web API + Kestrel + SignalR, the long-running control plane.
- **CLI** — single-file console tool for headless site operations (`deploy`, `redis-clear`, `iis`, `lic`, `restore`).
- **WebUI** — Blazor WebAssembly SPA served by the Agent, MudBlazor UI.
- **Desktop** — Avalonia 12.x cross-platform UI.
- **Domain / Application / Infrastructure / Shared / Contracts** — reusable core (no framework deps in Domain).

Purpose: automate Creatio site lifecycle — compile, deploy, sync, IIS/Redis management, Syncthing cluster coordination, certificate management, observability.

## Architecture & Data Flow

```
Domain  (entities, value objects; no framework refs)
   ↑
Application  (60+ IFoo contracts, DTOs, DeploymentOrchestrator)
   ↑
Infrastructure  (SyncEngine, BepProtocol, ConfigXmlService,
                 IIS/Redis/Cert managers, FileWatcher,
                 ClusterMembership, FluentMigrator SQLite store)
   ↑                 ↑
   ├── Agent (ASP.NET Core host)
   ├── CLI  (Console host)
   ├── WebUI (Blazor WASM, served by Agent)
   └── Desktop (Avalonia)
```

**Data flow:** Agent receives REST / SignalR calls → controllers invoke Application interfaces → Infrastructure services execute (sync engine, file watcher, gRPC to remote nodes, SQL to Creatio DB, IIS/Redis/Cert Win32 calls). State held in `ConcurrentDictionary` instances inside `SyncEngine`. Persisted config in SQLite via FluentMigrator.

**In-process state:** `SyncEngine` keeps per-site runtime state in `ConcurrentDictionary`. Mutations go through typed methods on `ISyncEngine`; never reach into the dictionary directly.

## Key Directories

| Path | Purpose |
|------|---------|
| `src/CreatioHelper.Domain` | Entities + value objects only. Zero framework refs. |
| `src/CreatioHelper.Application` | Interfaces (`IFoo`), DTOs, `DeploymentOrchestrator`. |
| `src/CreatioHelper.Infrastructure` | Implementations — sync, IIS, Redis, certs, SQLite. |
| `src/CreatioHelper.Agent` | Web API host, SignalR hubs, JWT + X-API-Key auth, Serilog. |
| `src/CreatioHelper.Cli` | `Program.cs` dispatches commands; `CliArgs.cs` parses args. |
| `src/CreatioHelper.WebUI` | Blazor WASM pages (Index, Login, Settings, Folders, Devices, Monitoring, Diagnostics); localization `en/ru/uk`. |
| `src/CreatioHelper.Desktop` | Avalonia MVVM (`MainWindow`, `SettingsWindow`, ViewModels). |
| `src/CreatioHelper.Shared` | `IOutputWriter` abstraction, logging utilities. |
| `src/CreatioHelper.Contracts` | API response DTOs (`SyncResponses.cs`). |
| `tests/CreatioHelper.UnitTests` | Broad xUnit coverage of Domain / Infrastructure. |
| `tests/CreatioHelper.Agent.Tests` | Controller + integration tests for Agent (uses `AspNetCore.Mvc.Testing`). |
| `docs/plans/` | Investigation write-ups (compile chain, security audit, etc.). |
| `.github/workflows/` | `dotnet-build.yml` (CI), `_build.yml` (reusable), `release*.yml`, `beta.yml`. |
| `winget/manifests/` | WinGet manifest for the Desktop installer. |

## Development Commands

All commands run from the repo root unless noted.

```bash
# Restore + build everything
dotnet restore CreatioHelper.sln
dotnet build   CreatioHelper.sln -c Release

# Run the Agent (http://localhost:5275, https://localhost:7033)
dotnet run --project src/CreatioHelper.Agent

# Run the CLI (example: deploy)
dotnet run --project src/CreatioHelper.Cli -- deploy --site <name>

# Run all tests
dotnet test CreatioHelper.sln

# Coverage (coverlet; integrates with ReportGenerator if installed)
dotnet test CreatioHelper.sln --collect:"XPlat Code Coverage"

# Pack for release locally (matches CI behaviour)
dotnet publish src/CreatioHelper.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
dotnet publish src/CreatioHelper.Cli      -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

CI: `.github/workflows/dotnet-build.yml` runs restore + Release build + `dotnet test` on both `ubuntu-latest` and `windows-latest` (`blame-hang-timeout: 60s`). Release flows in `release*.yml` / `beta.yml` call reusable `_build.yml`, which produces self-contained single-file zips (Desktop/CLI) or folders (Agent) for `win-x64` + `linux-x64`.

## Code Conventions & Common Patterns

- **Language:** C# 13, `net10.0` TFM on every project, nullable reference types ON, implicit usings ON.
- **Naming:** PascalCase types/methods/public members, `_camelCase` private fields, `IFoo` for interfaces, `Async` suffix on async methods. File name = type name.
- **Folders:** feature-first sub-folders inside each layer (e.g. `Infrastructure/Sync/`, `Agent/Services/`, `WebUI/Pages/`).
- **DI:** `Microsoft.Extensions.DependencyInjection` exclusively. Layer registration via extension methods (`AddApplication()`, `AddInfrastructureServices(IConfiguration)`, `AddSyncServices(SyncConfiguration?)`, `AddPlatformServices()`, `AddPerformanceServices()`, `AddSyncthingAutoStop(IConfiguration)`). New services register in the matching `Add*` method — do not call `services.Add*` directly from `Program.cs`.
- **Async:** `async`/`await` everywhere; no `Task.Wait` / `.Result`. Cancellation tokens flow through every public method.
- **Error handling:** exception-based. Agent uses `AddExceptionHandler<ApiExceptionHandler>()` (`CreatioHelper.Agent/Middleware/`) — domain code throws, the middleware shapes the response. CLI catches per-command and writes via `IOutputWriter`.
- **Logging:** Serilog structured logs (`Log.Information("...{Site}", site)`). Configuration in `src/CreatioHelper.Agent/appsettings.json`. Sinks: console, file, Graylog, SQLite.
- **Auth (Agent only):** dual scheme `JwtOrApiKey` — `AddPolicyScheme` routes to either `ApiKeyAuthenticationHandler` (Syncthing `X-API-Key`) or `AddJwtBearer`. New auth: extend the policy scheme, do not add a third handler at `MapControllers`.
- **State:** `ConcurrentDictionary` for in-memory state in `SyncEngine`. Persisted config in SQLite (`website-registry.json` for sites, `appsettings.json` for runtime, SQLite + FluentMigrator for internal store).
- **Configuration:** strongly typed via `IOptions<T>` (`AuthenticationSettings`, `JwtSettings`, `AgentConfig`, `SwaggerAuthSettings`). Read with `builder.Configuration.GetSection("…")`.

## Important Files

| File | Role |
|------|------|
| `CreatioHelper.sln` | Solution — 11 source + 2 test projects. |
| `src/CreatioHelper.Agent/Program.cs` | Agent bootstrap: Serilog, DI, auth, SignalR hubs, middleware pipeline. |
| `src/CreatioHelper.Cli/Program.cs` | CLI dispatch — `CliArgs.cs` + per-command handlers in same file. |
| `src/CreatioHelper.Agent/appsettings.json` | Runtime config — Serilog sinks, JWT, Graylog, SQLite, Prometheus, sync settings, CORS. |
| `src/CreatioHelper.Agent/Properties/launchSettings.json` | Local dev profiles — `http://localhost:5275`, `https://localhost:7033`. |
| `src/CreatioHelper.Agent/website-registry.example.json` | Template for per-site registry (IIS / Systemd site types). |
| `src/CreatioHelper.Cli/Properties/launchSettings.json` | CLI profile definitions (compile, install, IIS / Redis ops). |
| `tests/CreatioHelper.UnitTests/GlobalUsings.cs` | `global using Xunit;` — test discovery depends on this. |
| `.github/workflows/dotnet-build.yml` | CI truth source — matches `dotnet test` expectations. |
| `.github/workflows/_build.yml` | Reusable release workflow (called by release*.yml and beta.yml). |
| `winget/manifests/fejdraus.CreatioHelper.yaml` | WinGet manifest for Desktop installer. |
| `README.md` / `USER_GUIDE.md` / `CONTRIBUTING.md` / `CLA.md` / `SECURITY.md` | User-facing + contributor docs. |

## Runtime / Tooling Preferences

- **Runtime:** .NET 10 SDK. No Node, no Bun, no Python in the build.
- **Package manager:** NuGet via `dotnet` CLI. `winget` manifest for desktop distribution.
- **IDE:** JetBrains Rider / Visual Studio 2022 17.13+ (solution format). `.idea/` and `.vs/` are gitignored.
- **Required tooling:** `dotnet` 10 SDK; `dotnet-ef` / `dotnet-coverage` (coverlet is bundled).
- **Platforms:** CI matrix is `ubuntu-latest` + `windows-latest`. Some unit tests are Windows-only (gated by `Skip`/`SkippableFact`).
- **Do not commit:** `appsettings.*.local.json`, `appsettings.Production.json`, `cert.pem`, `key.pem`, `Data/`, `logs/`, `publish/`, `TestResults/`, `bin/`, `obj/`, `graphify-out/`, `.auto-claude/`.

## Testing & QA

- **Framework:** xUnit 2.9.3, Moq, coverlet, `SkippableFact`, `Microsoft.AspNetCore.Mvc.Testing` (Agent tests), `OpenTelemetry.Api`.
- **Projects:**
  - `tests/CreatioHelper.UnitTests` — Domain / Infrastructure unit tests. Mirrors source feature folders.
  - `tests/CreatioHelper.Agent.Tests` — Agent controller + integration tests via `WebApplicationFactory`.
- **Run:** `dotnet test CreatioHelper.sln`. CI runs both projects on both OSes.
- **Coverage:** `dotnet test --collect:"XPlat Code Coverage"` produces `coverage.cobertura.xml` per test project.
- **Patterns:** arrange-act-assert; mocks via Moq (`Mock<IFoo>`); pure logic tested without I/O; filesystem tests use temp dirs. Windows-only tests marked `[SkippableFact]` so Linux CI stays green.
- **Conventional Commits** required (per `CONTRIBUTING.md`). PR description should mention deployment verification when the change touches deploy / sync / IIS / Redis code paths.