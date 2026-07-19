# Per-site web server type + graceful IIS permission handling

## Problem
- Agent runs non-elevated. `MonitoringService.BroadcastWebServerOverview` calls
  `IWebServerService.GetAllSitesAsync/GetAllAppPoolsAsync` (IisManagerService) which run
  `Import-Module WebAdministration; Get-Website` via PowerShell. Reading IIS config requires
  elevation, so stderr is logged as `LogError("PowerShell error: ...")` every monitoring cycle.
- `WebServerServiceFactory` defaults to IIS on Windows unless `WebServer:PreferredType == "WindowsService"`.
- `WebSiteRegistryService.DiscoverIisSitesAsync` also runs `Get-Website` unconditionally on Windows.
- `WebServerController.SetWebServerType` writes to in-memory `_configuration` instead of persisting
  via `ConfigurationService.SetWebServerTypeAsync` -> selection is lost.

## Decisions (confirmed with user)
- Scope: **per-site** web server type (IIS / Service), platform-aware (no IIS on Linux/macOS).
- No-permission behavior: **log once as Warning** (no per-cycle spam) **+ surface a UI notification**
  explaining "run agent as Administrator".

## Status
- Stage 1 DONE (enum, WebSiteInfo.WebServerType, WebServerPermission helper).
- Stage 3 core DONE (warn-once via WebServerAccessStatus singleton, wired into IisManagerService +
  WebSiteRegistryService; access-status endpoint; RequiresElevation/AccessMessage in overview payload).
- SetWebServerType persistence bug FIXED.
- Build clean; 14 Agent tests pass (WebServerPermissionTests + WebServerControllerTests).
- Stage 2 DONE (per-site monitoring): WebSiteInfo.EffectiveKind; factory
  CreateWebServerServiceForSiteAsync; MonitoringService.BuildAndSendOverviewAsync partitions
  registered sites by EffectiveKind - IIS status/pool queries run only when an IIS site exists,
  Service sites go through the service manager (no IIS PowerShell). Also fixed PowerShell stderr
  encoding to UTF-8 in all three executors (was OEM-866 mojibake, which also broke signature match).
- Build clean; 199 Agent tests pass (added EffectiveKind + factory per-site routing tests).
- Verified live: 0 ERROR spam, 0 broadcast exceptions, warn-once, overview succeeds each cycle.
- Stage 4 DONE (WebUI): new page /monitoring/webserver (nav under Monitoring) with elevation
  banner (from GET /api/webserver/access-status) + per-site "Managed as" selector (Auto/IIS/Service).
  Backend: WebSiteRegistry.WebServerTypeOverrides map, WebSiteRegistryService.SetWebServerTypeAsync
  (applied in GetAllSitesAsync), PUT /api/website/{name}/webserver-type. IApiClient +
  WebServer DTOs; en/ru localization keys.
  Verified: solution builds 0/0; 199 Agent tests pass; live API smoke (access-status,
  register/set/list/invalid=400); browser - page renders, banner shows, selector persists
  (webServerType 1->2), success snackbar, no console errors.
- Minor follow-up: UnregisterWebSiteAsync does not remove a matching WebServerTypeOverrides entry
  (harmless orphan; only re-applies if a site of the same name reappears).

## Design

### Stage 1 - Domain + shared helpers
- Add `WebServerKind` enum { Auto, Iis, Service } (Domain).
- Add `WebSiteInfo.WebServerType` (default Auto) persisted in website-registry.json.
- Add `PowerShellPermission` helper: detect elevation/access-denied signatures in stderr
  ("повысить уровень процесса", "Access is denied", "Requested registry access", "elevat",
  "unauthorized"). Returns bool IsPermissionError(stderr).

### Stage 2 - Factory auto-detect + skip IIS for Service sites
- `WebServerServiceFactory`: on Auto/unset, auto-detect (Kestrel/Creatio Windows service present
  vs IIS site present) instead of blind IIS default.
- Per-site resolution: `CreateWebServerServiceForSiteAsync(WebSiteInfo)` picks manager by the
  site's WebServerType (Service -> WindowsServiceManager, Iis -> IisManagerService).
- Fix `SetWebServerType` to persist via ConfigurationService.

### Stage 3 - Monitoring per-site + warn-once + notification
- `IisManagerService` / `WebSiteRegistryService`: when stderr IsPermissionError -> log Warning ONCE
  (static/instance flag keyed by operation), set a status flag, do NOT LogError.
- Broadcast a SignalR notification (e.g. "IisPermissionRequired") + include a
  `RequiresElevation`/`AccessStatus` field on the web-server overview payload.
- `DiscoverIisSitesAsync`: skip when no IIS sites are expected / degrade quietly on permission error.

### Stage 4 - Contracts + WebUI
- Extend site DTOs with WebServerType + AccessStatus.
- Add web-server-type selector where sites are managed. NOTE: no site-management page exists in
  WebUI today; needs either a new page or an addition to an existing settings/monitoring page.
  -> Confirm UI home before building this stage.

## Test
- Unit: permission-signature detection; factory auto-detect; per-site manager selection;
  warn-once (only first permission error logs Warning).
- Run `dotnet test` for Agent + Infrastructure suites.

## Out of scope / follow-up
- Desktop local status bar uses Infrastructure WindowsIisManager separately; same warn-once pattern
  could be applied there later.
