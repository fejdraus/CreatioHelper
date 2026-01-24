# Syncthing Interoperability Testing Guide

This document provides step-by-step procedures for manually testing CreatioHelper's interoperability with real Syncthing clients.

**Last Updated:** 2026-01-24
**Target Syncthing Version:** v1.27.x and later

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Test Environment Setup](#2-test-environment-setup)
3. [Test Cases](#3-test-cases)
   - [3.1 Local Discovery](#31-local-discovery)
   - [3.2 Global Discovery](#32-global-discovery)
   - [3.3 BEP Protocol Handshake](#33-bep-protocol-handshake)
   - [3.4 File Synchronization](#34-file-synchronization)
   - [3.5 Relay Protocol](#35-relay-protocol)
   - [3.6 Conflict Resolution](#36-conflict-resolution)
4. [Expected Outcomes](#4-expected-outcomes)
5. [Troubleshooting](#5-troubleshooting)
6. [Protocol Verification Checklist](#6-protocol-verification-checklist)

---

## 1. Prerequisites

### 1.1 Software Requirements

| Component | Version | Download/Install |
|-----------|---------|------------------|
| Syncthing | v1.27.0+ | https://syncthing.net/downloads/ |
| .NET SDK | 10.0+ | https://dotnet.microsoft.com/download |
| CreatioHelper | Current branch | Build from source |

### 1.2 Network Requirements

- Both machines (or instances) on the same LAN for Local Discovery tests
- Internet access for Global Discovery and Relay tests
- Firewall rules allowing:
  - TCP port 22000 (BEP protocol)
  - UDP port 21027 (Local Discovery)
  - TCP port 22067 (Relay protocol)

### 1.3 Build CreatioHelper

```bash
# Clone and build
cd W:\GitHub\CreatioHelper
dotnet build src/CreatioHelper.Infrastructure/CreatioHelper.Infrastructure.csproj
dotnet build src/CreatioHelper.Agent/CreatioHelper.Agent.csproj
```

---

## 2. Test Environment Setup

### 2.1 Setting Up Syncthing

1. **Install Syncthing**
   ```bash
   # Windows (with Chocolatey)
   choco install syncthing

   # Or download binary from https://syncthing.net/downloads/
   ```

2. **Start Syncthing**
   ```bash
   syncthing
   ```

3. **Access Web UI**
   - Open browser to `http://127.0.0.1:8384`
   - Note the Device ID (Actions > Show ID)

4. **Record Syncthing Device ID**
   - Format: `XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX`
   - This will be needed to configure CreatioHelper

### 2.2 Setting Up CreatioHelper

1. **Generate Device Certificate**
   - CreatioHelper auto-generates on first run
   - Certificate stored in configured path (default: `~/.config/creatiohelper/`)

2. **Record CreatioHelper Device ID**
   - Device ID = SHA-256 hash of X.509 certificate
   - Format: `XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX`

3. **Configure Shared Folder**
   ```json
   {
     "folders": [
       {
         "id": "test-folder",
         "label": "Test Folder",
         "path": "C:\\SyncTest\\CreatioHelper"
       }
     ]
   }
   ```

4. **Start CreatioHelper**
   ```bash
   dotnet run --project src/CreatioHelper.Agent/CreatioHelper.Agent.csproj
   ```

### 2.3 Adding Devices in Syncthing

1. Open Syncthing Web UI (`http://127.0.0.1:8384`)
2. Click **Add Remote Device**
3. Enter CreatioHelper's Device ID
4. Click **Save**

---

## 3. Test Cases

### 3.1 Local Discovery

**Objective:** Verify that CreatioHelper and Syncthing discover each other on the local network.

#### Prerequisites
- Both instances on the same LAN subnet
- UDP port 21027 open on firewall
- No network isolation (e.g., VMs in bridged mode)

#### Test Steps

1. **Start CreatioHelper** (ensure Local Discovery is enabled)
   ```bash
   dotnet run --project src/CreatioHelper.Agent/CreatioHelper.Agent.csproj
   ```

2. **Start Syncthing**
   ```bash
   syncthing
   ```

3. **Wait for Discovery** (max 30 seconds)

4. **Check Syncthing Web UI**
   - Navigate to `http://127.0.0.1:8384`
   - Look for CreatioHelper device in "Remote Devices"

5. **Verify Discovery in CreatioHelper logs**
   - Look for: `Discovered device: <device-id> at <address>`

#### Expected Outcome

| Check | Expected Result | Status |
|-------|-----------------|--------|
| Discovery Time | < 30 seconds | |
| Syncthing shows CreatioHelper | Device appears in Remote Devices | |
| CreatioHelper logs show Syncthing | `Discovered device` message | |
| Magic number validation | No "invalid magic" errors | |

#### Verification Commands

```bash
# Monitor UDP traffic on discovery port
netstat -an | findstr 21027

# Check for broadcast packets (Windows)
netsh trace start capture=yes tracefile=discovery.etl
# ... run discovery ...
netsh trace stop
```

---

### 3.2 Global Discovery

**Objective:** Verify that CreatioHelper can register with and query from `discovery.syncthing.net`.

#### Prerequisites
- Internet access
- Valid TLS certificate
- Global Discovery enabled in configuration

#### Test Steps

1. **Configure Global Discovery Server**
   - Default: `https://discovery.syncthing.net/v2/`
   - IPv4 only: `https://discovery-v4.syncthing.net/v2/`
   - IPv6 only: `https://discovery-v6.syncthing.net/v2/`

2. **Start CreatioHelper with Global Discovery**
   ```bash
   dotnet run --project src/CreatioHelper.Agent/CreatioHelper.Agent.csproj
   ```

3. **Wait for Registration** (up to 30 minutes for re-announcement)

4. **Verify Announcement**
   - Check logs for: `Successfully announced to discovery server`
   - Note any rate limiting (429 responses)

5. **Query Device from Another Machine**
   ```bash
   # Using curl to query Global Discovery
   curl "https://discovery.syncthing.net/v2/?device=<DEVICE-ID>"
   ```

#### Expected Outcome

| Check | Expected Result | Status |
|-------|-----------------|--------|
| Announcement HTTP status | 200 or 204 | |
| Reannounce-After header | Parsed and honored | |
| Query returns addresses | JSON with `addresses` array | |
| 429 rate limiting | Retry-After honored | |

#### Verification API Calls

```bash
# Announce (requires client certificate)
curl -X POST https://discovery.syncthing.net/v2/ \
  --cert device.crt --key device.key \
  -H "Content-Type: application/json" \
  -d '{"addresses": ["tcp://192.168.1.100:22000"]}'

# Lookup (no certificate needed)
curl "https://discovery.syncthing.net/v2/?device=XXXXXXX-XXXXXXX-..."
```

---

### 3.3 BEP Protocol Handshake

**Objective:** Verify that CreatioHelper can establish a BEP connection with Syncthing.

#### Prerequisites
- Devices added to each other
- TCP port 22000 accessible
- TLS certificates valid

#### Test Steps

1. **Add CreatioHelper to Syncthing**
   - Web UI > Add Remote Device
   - Enter Device ID
   - Save

2. **Add Syncthing to CreatioHelper**
   - Configure device in settings
   - Set addresses (or rely on discovery)

3. **Monitor Connection**
   - Check Syncthing Web UI for connection status
   - Check CreatioHelper logs

4. **Verify Protocol Exchange**
   - Hello message exchange
   - ClusterConfig sent as first message
   - Index exchange

#### Expected Outcome

| Check | Expected Result | Status |
|-------|-----------------|--------|
| TLS Handshake | Successful (TLS 1.2 or 1.3) | |
| Hello Magic | `0x2EA7D90B` validated | |
| Device ID from certificate | SHA-256 hash matches | |
| ClusterConfig ordering | Sent before Index/IndexUpdate | |
| Connection status | "Connected" in Syncthing UI | |

#### Log Patterns to Verify

```
# CreatioHelper logs - successful handshake
[INFO] TLS handshake completed with <device-id>
[INFO] Received Hello from <device-name>
[INFO] Sent ClusterConfig
[INFO] Received ClusterConfig with X folders

# Error patterns to watch for
[ERROR] Invalid magic number: expected 0x2EA7D90B
[ERROR] Protocol state violation: Index sent before ClusterConfig
[ERROR] TLS certificate verification failed
```

---

### 3.4 File Synchronization

**Objective:** Verify bidirectional file sync between CreatioHelper and Syncthing.

#### Prerequisites
- Successful BEP connection established
- Shared folder configured on both sides
- Same folder ID on both devices

#### Test Steps

##### 3.4.1 Syncthing to CreatioHelper

1. **Create Test File in Syncthing folder**
   ```bash
   echo "Test content from Syncthing" > C:\SyncTest\Syncthing\test-s2c.txt
   ```

2. **Wait for Sync** (max 60 seconds)

3. **Verify File in CreatioHelper folder**
   ```bash
   type C:\SyncTest\CreatioHelper\test-s2c.txt
   ```

##### 3.4.2 CreatioHelper to Syncthing

1. **Create Test File in CreatioHelper folder**
   ```bash
   echo "Test content from CreatioHelper" > C:\SyncTest\CreatioHelper\test-c2s.txt
   ```

2. **Wait for Sync** (max 60 seconds)

3. **Verify File in Syncthing folder**
   ```bash
   type C:\SyncTest\Syncthing\test-c2s.txt
   ```

##### 3.4.3 Large File Transfer

1. **Create 10MB test file**
   ```bash
   # Windows PowerShell
   $bytes = New-Object byte[] 10485760
   (New-Object Random).NextBytes($bytes)
   [IO.File]::WriteAllBytes("C:\SyncTest\Syncthing\large-file.bin", $bytes)
   ```

2. **Compute SHA-256 hash**
   ```bash
   certutil -hashfile C:\SyncTest\Syncthing\large-file.bin SHA256
   ```

3. **Wait for Sync** (may take longer for large files)

4. **Verify Hash Matches**
   ```bash
   certutil -hashfile C:\SyncTest\CreatioHelper\large-file.bin SHA256
   ```

#### Expected Outcome

| Check | Expected Result | Status |
|-------|-----------------|--------|
| Small file sync time | < 10 seconds | |
| Large file sync time | < 60 seconds (10MB) | |
| File content integrity | SHA-256 hashes match | |
| File permissions | Preserved (if supported) | |
| Modification times | Within 1 second accuracy | |

---

### 3.5 Relay Protocol

**Objective:** Verify that CreatioHelper can connect via Syncthing relay servers.

#### Prerequisites
- Direct connection blocked (firewall rule)
- Relay protocol enabled
- Internet access to relay pool

#### Test Steps

1. **Block Direct Connection**
   ```bash
   # Windows Firewall - block direct port
   netsh advfirewall firewall add rule name="Block Syncthing Direct" `
     dir=in action=block protocol=tcp localport=22000
   ```

2. **Configure Relay in Syncthing**
   - Settings > Connections > Enable Relaying
   - Default pool: `https://relays.syncthing.net/endpoint`

3. **Start Both Services**

4. **Wait for Relay Connection**
   - May take 1-2 minutes for relay discovery

5. **Verify Connection via Relay**
   - Syncthing UI shows "Relay" connection type
   - CreatioHelper logs show relay address

6. **Test File Transfer via Relay**
   - Create small test file
   - Verify sync works (may be slower)

7. **Remove Firewall Rule**
   ```bash
   netsh advfirewall firewall delete rule name="Block Syncthing Direct"
   ```

#### Expected Outcome

| Check | Expected Result | Status |
|-------|-----------------|--------|
| Relay discovery | Finds available relays | |
| ALPN negotiation | "bep-relay" protocol | |
| JoinRelayRequest | Response code 0 (success) | |
| Session invitation | Received and processed | |
| File sync via relay | Works (slower than direct) | |
| RelayFull handling | Tries alternative relay | |

---

### 3.6 Conflict Resolution

**Objective:** Verify that file conflicts are handled correctly.

#### Prerequisites
- Bidirectional sync working
- Both devices can modify files

#### Test Steps

1. **Prepare Test File**
   ```bash
   echo "Original content" > C:\SyncTest\Syncthing\conflict-test.txt
   ```

2. **Wait for Initial Sync**

3. **Disconnect Devices** (disable network or stop one service)

4. **Modify File on Both Sides Simultaneously**
   ```bash
   # On Syncthing machine
   echo "Modified by Syncthing" > C:\SyncTest\Syncthing\conflict-test.txt

   # On CreatioHelper machine
   echo "Modified by CreatioHelper" > C:\SyncTest\CreatioHelper\conflict-test.txt
   ```

5. **Reconnect Devices**

6. **Wait for Conflict Detection**

7. **Verify Conflict File Created**
   - Look for file with `.sync-conflict-<date>-<time>.<ext>` pattern

#### Expected Outcome

| Check | Expected Result | Status |
|-------|-----------------|--------|
| Conflict detected | Yes | |
| Conflict file created | `conflict-test.sync-conflict-*.txt` | |
| Original file preserved | Latest modification wins | |
| No data loss | Both versions available | |

---

## 4. Expected Outcomes

### 4.1 Success Criteria Summary

| Test Area | Success Threshold |
|-----------|-------------------|
| Local Discovery | Device found within 30 seconds |
| Global Discovery | Registration successful, queryable |
| BEP Handshake | Connection established, ClusterConfig exchanged |
| File Sync (small) | Complete within 10 seconds |
| File Sync (large) | Complete within 60 seconds |
| Relay Connection | Established when direct fails |
| Conflict Resolution | Conflict file created, no data loss |

### 4.2 Performance Benchmarks

| Metric | Target | Notes |
|--------|--------|-------|
| Discovery latency | < 2 seconds (LAN) | After broadcast received |
| Connection setup | < 5 seconds | From discovery to ready |
| Small file transfer | < 1 second | < 1 KB |
| Large file transfer | 10+ MB/s | Direct connection |
| Relay overhead | < 2x direct latency | Expected slower |

---

## 5. Troubleshooting

### 5.1 Local Discovery Issues

| Symptom | Possible Cause | Solution |
|---------|----------------|----------|
| No discovery | UDP 21027 blocked | Check firewall rules |
| Discovery timeout | Network isolation | Verify same subnet |
| Invalid magic errors | Protocol mismatch | Check magic number `0x2EA7D90B` |
| Device ID mismatch | Encoding error | Verify 32 raw bytes encoding |

### 5.2 Global Discovery Issues

| Symptom | Possible Cause | Solution |
|---------|----------------|----------|
| 429 rate limited | Too many requests | Honor Retry-After header |
| Certificate error | Invalid/expired cert | Regenerate device certificate |
| 404 not found | Device not announced | Wait for reannouncement |
| Network timeout | Firewall/proxy | Check HTTPS access |

### 5.3 BEP Connection Issues

| Symptom | Possible Cause | Solution |
|---------|----------------|----------|
| TLS handshake fails | Certificate mismatch | Verify device IDs |
| Invalid magic | Wrong protocol | Check magic `0x2EA7D90B` |
| Protocol state error | ClusterConfig ordering | Ensure ClusterConfig sent first |
| Compression error | LZ4 format mismatch | Use raw block format |

### 5.4 Sync Issues

| Symptom | Possible Cause | Solution |
|---------|----------------|----------|
| Files not syncing | Folder not shared | Verify folder ID matches |
| Partial sync | Block request failed | Check request/response |
| Corruption | Hash mismatch | Verify block hashes |
| Permissions lost | Platform differences | Check platform data handling |

### 5.5 Relay Issues

| Symptom | Possible Cause | Solution |
|---------|----------------|----------|
| No relay found | Pool unreachable | Check internet access |
| RelayFull | Server capacity | Try alternative relays |
| Session timeout | Keepalive failure | Check ping/pong handling |
| Wrong token | URI parsing error | Verify token extraction |

---

## 6. Protocol Verification Checklist

### 6.1 BEP Protocol Checklist

- [ ] Hello magic: `0x2EA7D90B` (big-endian)
- [ ] Hello format: `[4B magic][2B length][protobuf]`
- [ ] Message format: `[2B header len][header][4B msg len][message]`
- [ ] All lengths are big-endian
- [ ] ClusterConfig sent before Index/IndexUpdate
- [ ] LZ4 compression uses raw block format
- [ ] Compression threshold: 128 bytes minimum
- [ ] Compression savings: 3.125% minimum
- [ ] Device ID: SHA-256 of X.509 certificate (32 bytes)

### 6.2 Local Discovery Checklist

- [ ] Magic number: `0x2EA7D90B`
- [ ] Legacy magic rejected: `0x7D79BC40`
- [ ] Packet format: `[4B magic][Announce protobuf]`
- [ ] Device ID: 32 raw bytes (not Base32 string)
- [ ] Instance ID changes on restart
- [ ] Cache lifetime: 90 seconds
- [ ] Broadcast interval: 30 seconds
- [ ] IPv4 broadcast: 255.255.255.255:21027
- [ ] IPv6 multicast: ff12::8384:21027

### 6.3 Global Discovery Checklist

- [ ] API endpoint: `/v2/`
- [ ] Announce: POST with JSON `{"addresses": [...]}`
- [ ] Lookup: GET with `?device=<id>`
- [ ] Client certificate required for announce
- [ ] No certificate for lookup
- [ ] Reannounce-After header honored
- [ ] Retry-After header honored (429 responses)
- [ ] HTTP/2 used

### 6.4 Relay Protocol Checklist

- [ ] Magic number: `0x9E79BC40`
- [ ] ALPN protocol: "bep-relay"
- [ ] Message format: XDR (not Protobuf)
- [ ] JoinRelayRequest handled
- [ ] SessionInvitation processed
- [ ] ConnectRequest for outgoing
- [ ] Ping/Pong keepalive working
- [ ] RelayFull handled gracefully
- [ ] Token parameter extracted from URI

---

## Appendix A: Test Data Templates

### A.1 Sample Configuration (CreatioHelper)

```json
{
  "device": {
    "name": "CreatioHelper-Test",
    "clientName": "creatiohelper",
    "clientVersion": "1.0.0"
  },
  "folders": [
    {
      "id": "test-folder-001",
      "label": "Test Folder",
      "path": "C:\\SyncTest\\CreatioHelper",
      "type": "sendreceive"
    }
  ],
  "discovery": {
    "local": {
      "enabled": true,
      "port": 21027
    },
    "global": {
      "enabled": true,
      "servers": [
        "https://discovery.syncthing.net/v2/"
      ]
    }
  },
  "relay": {
    "enabled": true,
    "pool": "https://relays.syncthing.net/endpoint"
  }
}
```

### A.2 Test File Checksums

When testing file transfers, use these known test files:

| File | Size | SHA-256 |
|------|------|---------|
| `empty.txt` | 0 bytes | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| `hello.txt` | 12 bytes | `b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9` (content: "Hello World\n") |

---

## Appendix B: Wireshark Filters

### B.1 Local Discovery

```
udp.port == 21027
```

### B.2 BEP Protocol

```
tcp.port == 22000
```

### B.3 Relay Protocol

```
tcp.port == 22067
```

---

## Appendix C: Log Analysis

### C.1 Key Log Messages

| Log Level | Message Pattern | Meaning |
|-----------|-----------------|---------|
| INFO | `Discovered device: X at Y` | Local discovery working |
| INFO | `TLS handshake completed` | Connection established |
| INFO | `Received ClusterConfig` | Protocol exchange working |
| INFO | `Index exchange complete` | Ready for sync |
| WARN | `Rate limited, retry after X` | Global discovery throttled |
| ERROR | `Invalid magic number` | Protocol mismatch |
| ERROR | `Protocol state violation` | Message ordering issue |

### C.2 Enabling Debug Logging

```bash
# CreatioHelper
dotnet run --project src/CreatioHelper.Agent/CreatioHelper.Agent.csproj -- --log-level Debug

# Syncthing
syncthing -verbose
```

---

*Document generated as part of Syncthing Protocol Compatibility implementation*
