# Syncthing AutoStop Feature

## Overview

The Syncthing AutoStop feature automatically stops and starts application services (IIS, systemd, launchd) when Syncthing synchronization is detected. This prevents file conflicts and ensures data integrity during synchronization.

## How It Works

```
┌─────────────────────────────────────────┐
│   1. Monitor Syncthing folders          │
│      - Use /rest/events API             │
│      - Event-driven (92% fewer reqs)    │
└──────────────┬──────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────┐
│   2. Stop services when sync starts     │
│      - Windows: IIS Pool/Site/Service   │
│      - Linux: systemd service           │
│      - MacOS: launchd service           │
└──────────────┬──────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────┐
│   3. Wait for sync completion           │
│      - Monitor needBytes, needFiles     │
│      - Wait for idle timeout (30s)      │
│      - Require stable checks (2x)       │
└──────────────┬──────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────┐
│   4. Start services when sync complete  │
│      - Restore stopped services         │
│      - Application ready to use         │
└─────────────────────────────────────────┘
```

## Configuration

### Windows (IIS)

```json
{
  "SyncthingAutoStop": {
    "Enabled": true,
    "SyncthingApiUrl": "http://127.0.0.1:8384",
    "SyncthingApiKey": "your-api-key-here",
    "MonitoredFolders": ["default", "creatio-site"],
    "IdleTimeoutSeconds": 30,
    "CompletionCheckIntervalSeconds": 5,
    "RequiredStableChecks": 2,
    "Windows": {
      "AppPoolName": "CreatioAppPool",
      "SiteName": "CreatioSite",
      "ServiceName": ""
    }
  }
}
```

### Windows (Service)

```json
{
  "SyncthingAutoStop": {
    "Enabled": true,
    "SyncthingApiUrl": "http://127.0.0.1:8384",
    "SyncthingApiKey": "your-api-key-here",
    "MonitoredFolders": ["default"],
    "Windows": {
      "AppPoolName": "",
      "SiteName": "",
      "ServiceName": "CreatioService"
    }
  }
}
```

### Linux (systemd)

```json
{
  "SyncthingAutoStop": {
    "Enabled": true,
    "SyncthingApiUrl": "http://127.0.0.1:8384",
    "SyncthingApiKey": "your-api-key-here",
    "MonitoredFolders": ["default"],
    "Linux": {
      "ServiceName": "creatio.service"
    }
  }
}
```

### MacOS (launchd)

```json
{
  "SyncthingAutoStop": {
    "Enabled": true,
    "SyncthingApiUrl": "http://127.0.0.1:8384",
    "SyncthingApiKey": "your-api-key-here",
    "MonitoredFolders": ["default"],
    "MacOS": {
      "ServiceName": "com.creatio.app"
    }
  }
}
```

## Configuration Parameters

### Core Settings

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `Enabled` | bool | false | Enable/disable AutoStop feature |
| `SyncthingApiUrl` | string | http://127.0.0.1:8384 | Local Syncthing API URL |
| `SyncthingApiKey` | string | "" | Syncthing API key (get from Syncthing settings) |
| `MonitoredFolders` | string[] | [] | List of Syncthing folder IDs to monitor |

### Timing Settings

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `IdleTimeoutSeconds` | int | 30 | Seconds to wait after last file change before starting services |
| `CompletionCheckIntervalSeconds` | int | 5 | How often to check sync status |
| `RequiredStableChecks` | int | 2 | Number of consecutive stable checks required |

### Platform-Specific Settings

#### Windows
- `AppPoolName`: IIS Application Pool name
- `SiteName`: IIS Website name
- `ServiceName`: Windows Service name (alternative to IIS)

#### Linux
- `ServiceName`: systemd service name (e.g., "creatio.service")

#### MacOS
- `ServiceName`: launchd service name (e.g., "com.creatio.app")

## Getting Syncthing Configuration

### 1. Get API Key

Open Syncthing Web UI → Settings → GUI → API Key

### 2. Get Folder IDs

Open Syncthing Web UI → click on folder → copy "Folder ID" from "Edit Folder" dialog

Or via API:
```bash
curl -H "X-API-Key: YOUR-API-KEY" http://127.0.0.1:8384/rest/config/folders | jq '.[].id'
```

### 3. Test Connection

```bash
curl -H "X-API-Key: YOUR-API-KEY" http://127.0.0.1:8384/rest/system/version
```

## Logging

The service logs all important operations with clear formatting for easy tracking.

**Logs are written to:**
- 📺 **Console** - for real-time monitoring
- 📄 **File** - `logs/agent-YYYY-MM-DD.log` (rotated daily, kept 30 days)

**File location:** `logs/agent-2026-01-15.log` (relative to agent working directory)

**Log file settings:**
- Daily rotation
- Max file size: 10 MB (creates new file if exceeded)
- Retention: 30 days
- Format: `YYYY-MM-DD HH:mm:ss.fff zzz [LEVEL] Message`

The service logs all important operations with clear formatting for easy tracking:

### Startup Logs

```
╔════════════════════════════════════════════════════════════╗
║  SyncthingAutoStop Service Started                        ║
╠════════════════════════════════════════════════════════════╣
║  Monitored folders: 2                                      ║
║  Folders: default, creatio-site                            ║
║  Idle timeout: 30s                                         ║
║  Using Events API (event-driven, low overhead)            ║
╚════════════════════════════════════════════════════════════╝
```

### Sync Detection Logs

```
┌────────────────────────────────────────────────────────────┐
│ 🔄 INCOMING SYNCHRONIZATION DETECTED                       │
├────────────────────────────────────────────────────────────┤
│ Folder: creatio-site                                       │
│ File: Packages/CustomPackage/Schemas/Contact.js           │
│ Action: update                                             │
└────────────────────────────────────────────────────────────┘
✅ Services stopped successfully
```

### Completion Logs

```
┌────────────────────────────────────────────────────────────┐
│ ⏳ WAITING FOR SYNCHRONIZATION TO COMPLETE                 │
├────────────────────────────────────────────────────────────┤
│ Idle timeout: 30s                                          │
│ Required stable checks: 2                                  │
└────────────────────────────────────────────────────────────┘
[INFO] Sync stable check 1/2 (idle for 31s)
[INFO] Sync stable check 2/2 (idle for 36s)
┌────────────────────────────────────────────────────────────┐
│ ✅ SYNCHRONIZATION COMPLETED AND STABLE                    │
└────────────────────────────────────────────────────────────┘
🚀 Services started successfully
```

### What Gets Logged

**✅ Always logged (Information level):**
- Service startup with configuration
- Incoming synchronization detection
- Services stopped/started
- Sync completion and stability checks
- Errors and warnings

**❌ NOT logged:**
- Continuous event polling (no spam)
- Events for non-monitored folders
- Routine status checks

Log levels:
- `Information`: Normal operations (startup, sync events, service state changes)
- `Warning`: Failed operations or missing configuration
- `Error`: Exceptions and HTTP errors
- `Debug`: Detailed event processing (disabled by default)

## Troubleshooting

### Services not stopping

1. Check that `Enabled: true` in configuration
2. Verify `MonitoredFolders` contains valid folder IDs
3. Check Syncthing API key is correct
4. Review logs for errors

### Services not starting after sync

1. Check `IdleTimeoutSeconds` - increase if sync is slow
2. Verify `RequiredStableChecks` - decrease if too strict
3. Check service permissions (agent must have rights to start/stop)

### API Connection Issues

```
HTTP error connecting to Syncthing API at http://127.0.0.1:8384
```

Solutions:
1. Verify Syncthing is running
2. Check API URL is correct
3. Verify API key is valid
4. Check firewall settings

## Best Practices

1. **Test First**: Start with `Enabled: false`, verify configuration, then enable
2. **Monitor Logs**: Watch logs during first sync to ensure proper operation
3. **Adjust Timeouts**: Tune `IdleTimeoutSeconds` based on your sync speed
4. **Use Specific Folders**: Only monitor folders that contain application files
5. **Permissions**: Ensure agent has permissions to stop/start services

## Example Scenarios

### Scenario 1: Windows IIS with Multiple Folders

```json
{
  "SyncthingAutoStop": {
    "Enabled": true,
    "MonitoredFolders": ["creatio-core", "creatio-packages"],
    "IdleTimeoutSeconds": 60,
    "Windows": {
      "AppPoolName": "CreatioAppPool",
      "SiteName": "CreatioSite"
    }
  }
}
```

### Scenario 2: Linux systemd with Fast Sync

```json
{
  "SyncthingAutoStop": {
    "Enabled": true,
    "MonitoredFolders": ["app-data"],
    "IdleTimeoutSeconds": 15,
    "CompletionCheckIntervalSeconds": 3,
    "Linux": {
      "ServiceName": "myapp.service"
    }
  }
}
```

## Architecture

### Components

1. **SyncthingAutoStopService**: Background service (IHostedService) that monitors Syncthing
2. **SyncthingCompletionMonitor**: Checks when sync is complete and stable
3. **ServiceStateManager**: Platform-agnostic service stop/start manager
4. **SyncthingAutoStopSettings**: Configuration binding

### Flow

```
SyncthingAutoStopService (Background)
    │
    ├─► Monitor /rest/events (long-polling, 60s timeout)
    │       │
    │       ├─► Event: ItemStarted (incoming file)
    │       │       ├─► Log: folder, file, action
    │       │       └─► Trigger: ServiceStateManager.StopServicesAsync()
    │       │
    │       ├─► Event: ItemFinished
    │       │       └─► Check if sync complete
    │       │
    │       └─► Event: StateChanged (folder → idle)
    │               └─► Trigger completion check
    │
    └─► Wait for completion
            │
            ├─► Poll /rest/db/status until stable (short-duration, every 5s)
            │       └─► needBytes == 0 && needFiles == 0
            │           (Note: Only runs during sync wait, typically <1 min)
            │
            ├─► Wait idle timeout (30s)
            │
            ├─► Verify stable (2 checks)
            │
            └─► Trigger: ServiceStateManager.StartServicesAsync()
```

**Key Advantages:**
- Event-driven: Responds immediately to sync start (no 5-second delay)
- Low overhead: 92% fewer HTTP requests vs polling
- More reliable: Uses official Syncthing Events API
- Better logging: Knows exactly which file triggered sync

## Security Considerations

1. **API Key**: Store securely, don't commit to source control
2. **Permissions**: Agent requires elevated permissions to manage services
3. **Network**: Syncthing API should only be accessible on localhost
4. **Validation**: Service names are validated before execution

## Performance Impact

- CPU: **Minimal** (event-driven, no constant polling)
- Memory: ~5-10 MB additional
- Network: **Low** - uses Syncthing Events API with long-polling (60s timeout)
- HTTP Requests: Only when events occur (vs polling every 5 seconds)
- Downtime: Services stopped only during active sync (typically 30-60 seconds)

### Event-Driven Architecture

Instead of polling `/rest/db/status` every 5 seconds, this service uses the **Syncthing Events API** (`/rest/events`) with long-polling:

```
Traditional Polling:          Event-Driven (This Service):
┌─────────────────┐          ┌─────────────────┐
│ Every 5 seconds │          │ Long-poll 60s   │
│ Check status    │          │ Wait for events │
│ = 12 req/min    │          │ = 1 req/min     │
└─────────────────┘          └─────────────────┘
        ↓                            ↓
   High CPU Load              Minimal CPU Load
```

**Benefits:**
- ✅ 92% fewer HTTP requests
- ✅ Near-instant response to sync events
- ✅ Minimal CPU/network overhead
- ✅ More reliable than polling

## Compatibility

- Windows: IIS 7.0+, Windows Service
- Linux: systemd-based distributions
- MacOS: macOS 10.10+ (launchd)
- Syncthing: v1.0.0+
