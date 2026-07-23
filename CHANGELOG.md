# Changelog

## v1.0.31 — changes since v1.0.24

### Compilation

- **Compile now builds the server assembly, not just static content.** Previously *Compile* ran only `BuildConfiguration -force=False`, which regenerates client static content but never compiles the server C# assembly (`conf/bin`). *Compile* now runs `RegenerateSchemaSources` first, so the server assembly is actually produced:
  - **Compile** = `RegenerateSchemaSources` + `BuildConfiguration -force=False`
  - **Compile All** = `RegenerateSchemaSources` + `RebuildWorkspace` + `BuildConfiguration -force=True` (unchanged)
- One compile per package stage, with depth (incremental vs full) taken from the button.

### Configuration rollback

- Read the configuration backup Creatio writes automatically on package install (`conf/backup`).
- Roll back to the state before the last package installation.
- Rollback exposed in both Desktop (button) and CLI (`restore` command).

### Web server / IIS

- Per-site web server type with graceful IIS permission handling.
- IIS site detection, application pool control and site discovery.
- IIS state polling routed through one helper; fixed the site-name lookup.

### Desktop

- Redis section made readable instead of a raw attribute dump.
- Advanced Redis settings block simplified.
- Exposed the Redis `web.config` settings required by Redis Cluster.

### WebUI

- Web server management by configured sites, agent folder browser, Russian pluralization / byte-unit localization, folder-list delete and scan-safe expand toggle.
- Removed the PWA service worker and manifest (fixes stale assets on redeploy).
- Fixed deep-link redirect, reconnect toast spam and language-selector mismatch.

### Agent

- Report `config.xml` paths from a single source of truth.
- Hardened authentication defaults.
- Authorize the WebSocket upgrade for `/syncHub` via the `access_token` query parameter.

### Sync / Syncthing

- Closed data-loss and resource windows in the sync infrastructure.
- Moved the vector clock to the Domain layer with causal conflict resolution; fixed `block_size` null and folder-label persistence on update.
- Corrected the device-ID Luhn checksum to match discovery and self-heal an invalid stored ID on startup.
- Await the Syncthing completion callback and deduplicate REST access.
- Moved folder scans to a bounded background scheduler.
- Removed the dead `SyncDatabase` implementation.

### CLI

- Support `--key=value` syntax and stop losing option values.

### Core / build / CI

- Run shell commands through one runner; fixed three deadlocks.
- Resolve the Creatio site layout in one place.
- Disposed leaked `CancellationTokenSource` instances.
- Bumped NuGet packages to 10.0.10 / latest; hardened the updater version parse.
- Release each component (Desktop, CLI, Agent) separately; tag releases with the `desktop-v` prefix the updater expects.
