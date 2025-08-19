# CreatioHelper Agent Sync

## Overview

This implementation adds Syncthing-inspired file synchronization capabilities to the CreatioHelper Agent project. It provides secure, distributed file synchronization between multiple agent instances.

**Inspired by Syncthing** - This implementation is based on concepts and algorithms from the Syncthing project (https://syncthing.net/), adapted for C# and the CreatioHelper architecture.

## Features

- **Secure Device-to-Device Sync**: TLS-encrypted connections with certificate-based device identification
- **Real-time File Monitoring**: Automatic detection of file changes using file system watchers
- **Conflict Resolution**: Intelligent handling of concurrent file modifications
- **Device Discovery**: Local and global device discovery mechanisms
- **Web API**: RESTful API for sync management
- **Real-time Updates**: SignalR integration for live sync status updates
- **Multiple Sync Types**: Send-receive, send-only, receive-only, and encrypted receive modes

## Architecture

### Core Components

1. **SyncEngine** - Main orchestration service
2. **SyncProtocol** - Block Exchange Protocol (BEP) implementation
3. **DeviceDiscovery** - Local and global device discovery
4. **FileWatcher** - File system monitoring and change detection
5. **ConflictResolver** - Automatic conflict resolution
6. **SyncHub** - SignalR hub for real-time updates

### Key Concepts

- **Device**: A trusted endpoint in the sync network (identified by certificate)
- **Folder**: A directory tree shared between devices
- **Block**: Fixed-size chunks of file data for efficient transfer
- **Vector Clock**: Logical timestamps for conflict detection

## API Endpoints

### System Status
```
GET /api/sync/status
```

### Device Management
```
GET /api/sync/devices
POST /api/sync/devices
GET /api/sync/devices/{deviceId}
POST /api/sync/devices/{deviceId}/pause
POST /api/sync/devices/{deviceId}/resume
```

### Folder Management
```
GET /api/sync/folders
POST /api/sync/folders
GET /api/sync/folders/{folderId}
POST /api/sync/folders/{folderId}/pause
POST /api/sync/folders/{folderId}/resume
POST /api/sync/folders/{folderId}/scan
POST /api/sync/folders/{folderId}/share
POST /api/sync/folders/{folderId}/unshare
```

### Statistics
```
GET /api/sync/statistics
```

## Usage Examples

### Adding a Device

```bash
curl -X POST http://localhost:5000/api/sync/devices \
  -H "Authorization: Bearer {token}" \
  -H "Content-Type: application/json" \
  -d '{
    "deviceId": "ABC123-DEF456-GHI789",
    "name": "Remote Server",
    "certificateFingerprint": "sha256:1234567890abcdef...",
    "addresses": ["tcp://192.168.1.100:22000"]
  }'
```

### Adding a Folder

```bash
curl -X POST http://localhost:5000/api/sync/folders \
  -H "Authorization: Bearer {token}" \
  -H "Content-Type: application/json" \
  -d '{
    "folderId": "websites",
    "label": "Website Files",
    "path": "/var/www/html",
    "type": "SendReceive"
  }'
```

### Sharing a Folder with a Device

```bash
curl -X POST http://localhost:5000/api/sync/folders/websites/share \
  -H "Authorization: Bearer {token}" \
  -H "Content-Type: application/json" \
  -d '{
    "deviceId": "ABC123-DEF456-GHI789"
  }'
```

## SignalR Integration

Connect to `/syncHub` for real-time sync events:

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/syncHub", {
        accessTokenFactory: () => localStorage.getItem("authToken")
    })
    .build();

// Join sync events group
await connection.invoke("JoinGroup", "sync-events");

// Listen for sync events
connection.on("SyncEvent", (event) => {
    console.log("Sync event:", event);
});
```

## Configuration

The sync engine can be configured via `appsettings.json`:

```json
{
  "Sync": {
    "Port": 22000,
    "DeviceName": "Agent-1",
    "GlobalDiscoveryEnabled": true,
    "LocalDiscoveryEnabled": true,
    "RelaysEnabled": true,
    "MaxSendKbps": 0,
    "MaxRecvKbps": 0
  }
}
```

## Security Considerations

- All connections use TLS encryption
- Device identity is verified via X.509 certificates
- Each device generates a unique certificate on first start
- Device IDs are derived from certificate SHA-256 hashes
- Only explicitly trusted devices can sync

## Folder Types

1. **SendReceive**: Full bidirectional sync (default)
2. **SendOnly**: Local changes are sent, remote changes ignored
3. **ReceiveOnly**: Remote changes accepted, local changes ignored
4. **ReceiveEncrypted**: Receive encrypted files for backup

## Conflict Resolution

When concurrent modifications occur:

1. **Vector Clocks**: Used to determine causality
2. **Automatic Resolution**: Creates `.sync-conflict` files
3. **Configurable Strategies**: Prefer local, remote, newer, or larger files
4. **Manual Resolution**: Conflicts can be resolved via API

## Installation

1. The sync services are automatically registered when you add:
   ```csharp
   builder.Services.AddSyncServices();
   ```

2. The sync engine starts as a hosted service

3. Access the API at `/api/sync/*` endpoints

## Development Notes

### Based on Syncthing Concepts

This implementation draws inspiration from:
- **Block Exchange Protocol (BEP)**: Core sync protocol
- **Device Discovery**: Local and global discovery mechanisms  
- **Conflict Resolution**: Syncthing's conflict handling strategies
- **File Watching**: Real-time change detection
- **Vector Clocks**: Logical time for distributed systems

### Key Differences from Syncthing

- Written in C# instead of Go
- Integrated with existing CreatioHelper architecture
- Uses SignalR for real-time updates
- RESTful API instead of just web GUI
- Designed for website/file serving scenarios

### Testing

Run the sync engine tests:

```bash
dotnet test CreatioHelper.Tests --filter "Category=Sync"
```

## License Compliance

This implementation is inspired by Syncthing's algorithms and concepts. Syncthing is licensed under MPLv2. This C# implementation:

- Is a new implementation, not a modification of Syncthing code
- Acknowledges Syncthing as the inspiration for the sync algorithms
- Follows similar architectural patterns adapted for C#/.NET

Original Syncthing project: https://github.com/syncthing/syncthing

## Troubleshooting

### Device Not Connecting
- Check firewall settings (port 22000)
- Verify certificate fingerprints match
- Ensure network connectivity between devices

### Files Not Syncing
- Check folder permissions
- Verify folder is not paused
- Review ignore patterns
- Check available disk space

### Performance Issues
- Monitor bandwidth limits
- Check for conflicting antivirus software
- Review file system performance
- Consider adjusting block sizes

## Future Enhancements

- Database persistence for configuration
- Web UI for management
- File versioning system
- Bandwidth monitoring and limits
- Advanced ignore patterns
- Relay server support
- Mobile device support