# Changelog

## v1.0.31 — changes since v1.0.24

### Common (Desktop + CLI)

**Compilation**

- **Compile now builds the server assembly, not just static content.** Previously *Compile* ran only `BuildConfiguration -force=False`, which regenerates client static content but never compiles the server C# assembly (`conf/bin`). *Compile* now runs `RegenerateSchemaSources` first, so the server assembly is actually produced:
  - **Compile** = `RegenerateSchemaSources` + `BuildConfiguration -force=False`
  - **Compile All** = `RegenerateSchemaSources` + `RebuildWorkspace` + `BuildConfiguration -force=True` (unchanged)
- One compile per package stage, with depth (incremental vs full) taken from the button.

**Configuration rollback**

- Read the configuration backup Creatio writes automatically on package install (`conf/backup`).
- Roll back to the state before the last package installation.
- Exposed in both Desktop (button) and CLI (`restore` command).

**Core / build**

- Run shell commands through one runner; fixed three deadlocks.
- Resolve the Creatio site layout in one place.
- Bumped NuGet packages to 10.0.10 / latest; hardened the updater version parse.
- Component-scoped release pipeline: Desktop, CLI and Agent are released separately with `desktop-v` / `cli-v` / `agent-v` tags.

### Desktop

- Redis section made readable instead of a raw attribute dump.
- Advanced Redis settings block simplified.
- Exposed the Redis `web.config` settings required by Redis Cluster.

### CLI

- Support `--key=value` syntax and stop losing option values.

### Agent

- Report `config.xml` paths from a single source of truth.
- Hardened authentication defaults.
- Authorize the WebSocket upgrade for `/syncHub` via the `access_token` query parameter.
- Web server management: per-site web server type, IIS site detection, application pool control and site discovery, with graceful IIS permission handling.
- Sync: closed data-loss and resource windows; moved the vector clock to the Domain layer with causal conflict resolution; corrected the device-ID Luhn checksum and self-heal on startup; bounded background folder scanning; removed the dead `SyncDatabase` implementation.
- WebUI: web server management UI, agent folder browser, Russian pluralization / byte-unit localization; removed the PWA service worker and manifest; fixed deep-link redirect, reconnect toast spam and language-selector mismatch.
