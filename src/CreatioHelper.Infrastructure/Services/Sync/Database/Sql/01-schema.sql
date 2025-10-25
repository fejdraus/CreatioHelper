-- CreatioHelper.Agent Database Schema
-- Based on Syncthing's SQLite schema with CreatioHelper-specific optimizations
-- Compatible with Syncthing's database structure for interoperability

-- ======================================================================
-- SCHEMA MIGRATIONS
-- ======================================================================

-- Schema migrations hold the list of historical migrations applied
CREATE TABLE IF NOT EXISTS schema_migrations (
    schema_version INTEGER NOT NULL,
    applied_at INTEGER NOT NULL, -- unix nanos
    agent_version TEXT NOT NULL COLLATE BINARY,
    PRIMARY KEY(schema_version)
) STRICT;

-- ======================================================================
-- KEY-VALUE STORE
-- ======================================================================

-- Simple KV store for miscellaneous configuration data
CREATE TABLE IF NOT EXISTS kv (
    key TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
    value BLOB NOT NULL
) STRICT;

-- ======================================================================
-- DEVICES
-- ======================================================================

-- Devices map device IDs to database device indexes (Syncthing compatible)
CREATE TABLE IF NOT EXISTS devices (
    idx INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    device_id TEXT NOT NULL UNIQUE COLLATE BINARY,
    device_name TEXT COLLATE BINARY,
    created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
    last_seen INTEGER,
    connection_type TEXT,
    last_address TEXT
) STRICT;

-- ======================================================================
-- FOLDERS 
-- ======================================================================

-- Folder configurations
CREATE TABLE IF NOT EXISTS folders (
    folder_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
    label TEXT NOT NULL COLLATE BINARY,
    path TEXT NOT NULL COLLATE BINARY,
    folder_type TEXT NOT NULL DEFAULT 'sendreceive',
    created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
    paused INTEGER NOT NULL DEFAULT 0, -- boolean
    rescan_interval INTEGER NOT NULL DEFAULT 3600,
    fs_watcher_enabled INTEGER NOT NULL DEFAULT 1, -- boolean
    ignore_permissions INTEGER NOT NULL DEFAULT 0, -- boolean
    versioning_type TEXT,
    versioning_params TEXT -- JSON
) STRICT;

-- Folder-device relationships
CREATE TABLE IF NOT EXISTS folder_devices (
    folder_id TEXT NOT NULL COLLATE BINARY,
    device_idx INTEGER NOT NULL,
    PRIMARY KEY(folder_id, device_idx),
    FOREIGN KEY(folder_id) REFERENCES folders(folder_id) ON DELETE CASCADE,
    FOREIGN KEY(device_idx) REFERENCES devices(idx) ON DELETE CASCADE
) STRICT, WITHOUT ROWID;

-- ======================================================================
-- FILES (Based on Syncthing schema)
-- ======================================================================

-- Files table contains all files announced by any device
-- Structure closely matches Syncthing's files table for compatibility
CREATE TABLE IF NOT EXISTS files (
    device_idx INTEGER NOT NULL,
    folder_id TEXT NOT NULL COLLATE BINARY,
    sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    remote_sequence INTEGER,
    name TEXT NOT NULL COLLATE BINARY,
    type INTEGER NOT NULL, -- protocol.FileInfoType (0=file, 1=directory, 4=symlink)
    modified INTEGER NOT NULL, -- Unix nanos
    size INTEGER NOT NULL,
    version TEXT NOT NULL COLLATE BINARY, -- Vector clock as JSON
    deleted INTEGER NOT NULL DEFAULT 0, -- boolean
    invalid INTEGER NOT NULL DEFAULT 0, -- boolean
    local_flags INTEGER NOT NULL DEFAULT 0,
    permissions INTEGER,
    block_size INTEGER,
    blocklist_hash BLOB,
    symlink_target TEXT,
    platform_data BLOB, -- Serialized PlatformData protobuf
    created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
    updated_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
    FOREIGN KEY(device_idx) REFERENCES devices(idx) ON DELETE CASCADE,
    FOREIGN KEY(folder_id) REFERENCES folders(folder_id) ON DELETE CASCADE
) STRICT;

-- FileInfos store the actual protobuf object separately for efficiency
CREATE TABLE IF NOT EXISTS file_infos (
    sequence INTEGER NOT NULL PRIMARY KEY,
    protobuf_data BLOB NOT NULL,
    FOREIGN KEY(sequence) REFERENCES files(sequence) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
) STRICT;

-- ======================================================================
-- BLOCK STORAGE (Syncthing compatible)
-- ======================================================================

-- Block lists are extracted from FileInfos and stored separately
-- Reduces database size by reusing the same block list for multiple devices
CREATE TABLE IF NOT EXISTS block_lists (
    blocklist_hash BLOB NOT NULL PRIMARY KEY,
    protobuf_data BLOB NOT NULL,
    created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now'))
) STRICT;

-- Individual blocks for quick lookup
-- A given block can exist in multiple blocklists and at multiple offsets
CREATE TABLE IF NOT EXISTS blocks (
    hash BLOB NOT NULL,
    blocklist_hash BLOB NOT NULL,
    idx INTEGER NOT NULL,
    offset INTEGER NOT NULL,
    size INTEGER NOT NULL,
    weak_hash INTEGER, -- For rolling hash deduplication
    created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
    PRIMARY KEY (hash, blocklist_hash, idx),
    FOREIGN KEY(blocklist_hash) REFERENCES block_lists(blocklist_hash) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
) STRICT;

-- ======================================================================
-- INDEX IDS (Syncthing compatible)
-- ======================================================================

-- Index IDs hold the index ID and maximum sequence for a given device and folder
CREATE TABLE IF NOT EXISTS index_ids (
    device_idx INTEGER NOT NULL,
    folder_id TEXT NOT NULL COLLATE BINARY,
    index_id TEXT NOT NULL COLLATE BINARY,
    sequence INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(device_idx, folder_id),
    FOREIGN KEY(device_idx) REFERENCES devices(idx) ON DELETE CASCADE,
    FOREIGN KEY(folder_id) REFERENCES folders(folder_id) ON DELETE CASCADE
) STRICT, WITHOUT ROWID;

-- ======================================================================
-- COUNTS & STATISTICS (Syncthing compatible)
-- ======================================================================

-- Counts and sizes are maintained for each device, folder, type, flag combination
CREATE TABLE IF NOT EXISTS counts (
    device_idx INTEGER NOT NULL,
    folder_id TEXT NOT NULL COLLATE BINARY,
    type INTEGER NOT NULL,
    local_flags INTEGER NOT NULL,
    deleted INTEGER NOT NULL, -- boolean
    count INTEGER NOT NULL DEFAULT 0,
    size INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(device_idx, folder_id, type, local_flags, deleted),
    FOREIGN KEY(device_idx) REFERENCES devices(idx) ON DELETE CASCADE,
    FOREIGN KEY(folder_id) REFERENCES folders(folder_id) ON DELETE CASCADE
) STRICT, WITHOUT ROWID;

-- ======================================================================
-- CREATIOHELPER-SPECIFIC EXTENSIONS
-- ======================================================================

-- Sync events for monitoring and debugging
CREATE TABLE IF NOT EXISTS sync_events (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    event_type TEXT NOT NULL COLLATE BINARY,
    device_idx INTEGER,
    folder_id TEXT COLLATE BINARY,
    file_name TEXT COLLATE BINARY,
    event_data TEXT, -- JSON
    timestamp INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
    FOREIGN KEY(device_idx) REFERENCES devices(idx) ON DELETE SET NULL,
    FOREIGN KEY(folder_id) REFERENCES folders(folder_id) ON DELETE SET NULL
) STRICT;

-- Conflict resolution history
CREATE TABLE IF NOT EXISTS conflict_resolutions (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    folder_id TEXT NOT NULL COLLATE BINARY,
    file_name TEXT NOT NULL COLLATE BINARY,
    resolution_type TEXT NOT NULL COLLATE BINARY,
    local_version TEXT COLLATE BINARY,
    remote_version TEXT COLLATE BINARY,
    winner_device_idx INTEGER,
    timestamp INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
    FOREIGN KEY(folder_id) REFERENCES folders(folder_id) ON DELETE CASCADE,
    FOREIGN KEY(winner_device_idx) REFERENCES devices(idx) ON DELETE SET NULL
) STRICT;

-- Performance metrics
CREATE TABLE IF NOT EXISTS performance_metrics (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    metric_name TEXT NOT NULL COLLATE BINARY,
    metric_value REAL NOT NULL,
    device_idx INTEGER,
    folder_id TEXT COLLATE BINARY,
    timestamp INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
    FOREIGN KEY(device_idx) REFERENCES devices(idx) ON DELETE CASCADE,
    FOREIGN KEY(folder_id) REFERENCES folders(folder_id) ON DELETE CASCADE
) STRICT;