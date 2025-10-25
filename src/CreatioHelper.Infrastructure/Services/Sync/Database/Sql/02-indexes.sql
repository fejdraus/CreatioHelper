-- CreatioHelper.Agent Database Indexes
-- Optimized for Syncthing-compatible queries and CreatioHelper-specific operations

-- ======================================================================
-- FILES TABLE INDEXES (Syncthing compatible)
-- ======================================================================

-- There can be only one file per folder, device, and remote sequence number
CREATE UNIQUE INDEX IF NOT EXISTS idx_files_remote_sequence 
ON files (device_idx, folder_id, remote_sequence)
WHERE remote_sequence IS NOT NULL;

-- There can be only one file per folder, device, and name
CREATE UNIQUE INDEX IF NOT EXISTS idx_files_device_name 
ON files (device_idx, folder_id, name);

-- Look up & iterate files based on just folder and name
CREATE INDEX IF NOT EXISTS idx_files_name_only 
ON files (folder_id, name);

-- Look up & iterate files based on blocks hash
CREATE INDEX IF NOT EXISTS idx_files_blocklist_hash 
ON files (blocklist_hash, device_idx) 
WHERE blocklist_hash IS NOT NULL;

-- Index for sequence-based queries (important for sync)
CREATE INDEX IF NOT EXISTS idx_files_sequence 
ON files (folder_id, device_idx, sequence);

-- Index for modified time queries
CREATE INDEX IF NOT EXISTS idx_files_modified 
ON files (folder_id, modified) 
WHERE deleted = 0;

-- Index for deleted files cleanup
CREATE INDEX IF NOT EXISTS idx_files_deleted 
ON files (folder_id, deleted, updated_at);

-- Index for need flags (files that need to be synced)
CREATE INDEX IF NOT EXISTS idx_files_local_flags 
ON files (folder_id, local_flags) 
WHERE local_flags > 0;

-- ======================================================================
-- DEVICES TABLE INDEXES
-- ======================================================================

-- Index for device lookups by name
CREATE INDEX IF NOT EXISTS idx_devices_name 
ON devices (device_name);

-- Index for last seen queries
CREATE INDEX IF NOT EXISTS idx_devices_last_seen 
ON devices (last_seen) 
WHERE last_seen IS NOT NULL;

-- ======================================================================
-- BLOCKS TABLE INDEXES
-- ======================================================================

-- Index for block hash lookups (deduplication)
CREATE INDEX IF NOT EXISTS idx_blocks_hash 
ON blocks (hash);

-- Index for blocklist cleanup
CREATE INDEX IF NOT EXISTS idx_blocks_blocklist 
ON blocks (blocklist_hash);

-- Index for weak hash lookups (rolling hash)
CREATE INDEX IF NOT EXISTS idx_blocks_weak_hash 
ON blocks (weak_hash) 
WHERE weak_hash IS NOT NULL;

-- ======================================================================
-- SYNC EVENTS INDEXES
-- ======================================================================

-- Index for event type queries
CREATE INDEX IF NOT EXISTS idx_sync_events_type 
ON sync_events (event_type, timestamp);

-- Index for device-specific events
CREATE INDEX IF NOT EXISTS idx_sync_events_device 
ON sync_events (device_idx, timestamp) 
WHERE device_idx IS NOT NULL;

-- Index for folder-specific events
CREATE INDEX IF NOT EXISTS idx_sync_events_folder 
ON sync_events (folder_id, timestamp) 
WHERE folder_id IS NOT NULL;

-- Index for file-specific events
CREATE INDEX IF NOT EXISTS idx_sync_events_file 
ON sync_events (folder_id, file_name, timestamp) 
WHERE file_name IS NOT NULL;

-- Index for cleanup old events
CREATE INDEX IF NOT EXISTS idx_sync_events_timestamp 
ON sync_events (timestamp);

-- ======================================================================
-- CONFLICT RESOLUTIONS INDEXES
-- ======================================================================

-- Index for conflict queries by folder
CREATE INDEX IF NOT EXISTS idx_conflicts_folder 
ON conflict_resolutions (folder_id, timestamp);

-- Index for conflict queries by file
CREATE INDEX IF NOT EXISTS idx_conflicts_file 
ON conflict_resolutions (folder_id, file_name, timestamp);

-- ======================================================================
-- PERFORMANCE METRICS INDEXES
-- ======================================================================

-- Index for metrics queries by name
CREATE INDEX IF NOT EXISTS idx_metrics_name 
ON performance_metrics (metric_name, timestamp);

-- Index for device-specific metrics
CREATE INDEX IF NOT EXISTS idx_metrics_device 
ON performance_metrics (device_idx, metric_name, timestamp) 
WHERE device_idx IS NOT NULL;

-- Index for folder-specific metrics
CREATE INDEX IF NOT EXISTS idx_metrics_folder 
ON performance_metrics (folder_id, metric_name, timestamp) 
WHERE folder_id IS NOT NULL;

-- ======================================================================
-- FOLDER DEVICES INDEXES
-- ======================================================================

-- Index for finding folders for a device
CREATE INDEX IF NOT EXISTS idx_folder_devices_device 
ON folder_devices (device_idx);

-- Index for finding devices for a folder (already covered by primary key)

-- ======================================================================
-- ADDITIONAL PERFORMANCE OPTIMIZATIONS
-- ======================================================================

-- Covering index for common file queries (includes most commonly accessed columns)
CREATE INDEX IF NOT EXISTS idx_files_covering 
ON files (folder_id, device_idx, name, type, deleted, modified, size, version);

-- Index for version vector queries
CREATE INDEX IF NOT EXISTS idx_files_version 
ON files (folder_id, version);

-- Index for block size optimization queries
CREATE INDEX IF NOT EXISTS idx_files_block_size 
ON files (folder_id, block_size) 
WHERE block_size IS NOT NULL;