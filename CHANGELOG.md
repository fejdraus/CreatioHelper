# Changelog

## Desktop v1.2.0 · CLI v1.1.0

### Common (Desktop + CLI)

**Compile All now matches the web *Compile all* button; new *Extra Compile* mode**

- **Compile All** now compiles through the same web facade the Creatio *Compile all* button uses: `Rebuild` + `BuildConfiguration -force=True`, a full rebuild of all schemas. Requires **Creatio 8.0.10 or later** (the `Rebuild` operation does not exist in earlier versions); it falls back to *Extra Compile* on older versions.
- **Extra Compile** is the previous full-rebuild chain, now a mode of its own: `RegenerateSchemaSources` + `RebuildWorkspace` + `BuildConfiguration -force=True`. It regenerates every schema source from metadata (self-healing) and works on all versions.
- On **Creatio 8.0.10+** all four modes are available (Compile, Fast Compile, Compile All, Extra Compile); on older versions only the two documented modes are available (Compile, Extra Compile).
- Desktop: new **Start (Compile All)** item in the Start dropdown; the former full-rebuild item is renamed **Start (Extra Compile)**. The main Start click is unchanged.
- CLI: new `--compile extra` value; `--compile full` now maps to the web *Compile all*.

## v1.0.32

### Common (Desktop + CLI)

**Fast Compile**

- New compile mode that matches what the Creatio web *Compile* button does: it compiles only the changed schemas instead of regenerating every schema source.
  - **Fast Compile** = `Build` + `BuildConfiguration -force=False`
- Measured on a large configuration: **11 min** versus **30 min** for *Compile* and **40 min** for *Compile All*.
- Requires **Creatio 8.0.10 or later** (the `Build` operation does not exist in earlier versions). On older versions the option is disabled in Desktop, and any other entry point falls back to the regular *Compile* with a warning.
- Desktop: new **Start (Fast Compile)** item in the Start dropdown.
- CLI: new `--compile fast` value.

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
