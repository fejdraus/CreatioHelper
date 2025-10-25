-- CreatioHelper.Agent Database Triggers
-- Maintain data integrity and statistics automatically (Syncthing compatible)

-- ======================================================================
-- INDEX IDS TRIGGERS (Syncthing compatible)
-- ======================================================================

-- Automatically update index IDs when files are inserted
CREATE TRIGGER IF NOT EXISTS trigger_index_ids_insert AFTER INSERT ON files
BEGIN
    INSERT INTO index_ids (device_idx, folder_id, index_id, sequence)
        VALUES (NEW.device_idx, NEW.folder_id, "", COALESCE(NEW.remote_sequence, NEW.sequence))
        ON CONFLICT DO UPDATE SET sequence = MAX(sequence, COALESCE(NEW.remote_sequence, NEW.sequence));
END;

-- Update index IDs when files are updated with new sequences
CREATE TRIGGER IF NOT EXISTS trigger_index_ids_update AFTER UPDATE ON files
WHEN NEW.sequence > OLD.sequence OR NEW.remote_sequence != OLD.remote_sequence
BEGIN
    UPDATE index_ids 
    SET sequence = MAX(sequence, COALESCE(NEW.remote_sequence, NEW.sequence))
    WHERE device_idx = NEW.device_idx AND folder_id = NEW.folder_id;
END;

-- ======================================================================
-- COUNTS TRIGGERS (Syncthing compatible)
-- ======================================================================

-- Maintain counts when files are added
CREATE TRIGGER IF NOT EXISTS trigger_counts_insert AFTER INSERT ON files
BEGIN
    INSERT INTO counts (device_idx, folder_id, type, local_flags, deleted, count, size)
        VALUES (NEW.device_idx, NEW.folder_id, NEW.type, NEW.local_flags, NEW.deleted, 1, NEW.size)
        ON CONFLICT DO UPDATE SET count = count + 1, size = size + NEW.size;
END;

-- Maintain counts when files are removed
CREATE TRIGGER IF NOT EXISTS trigger_counts_delete AFTER DELETE ON files
BEGIN
    UPDATE counts 
    SET count = count - 1, size = size - OLD.size
    WHERE device_idx = OLD.device_idx 
      AND folder_id = OLD.folder_id
      AND type = OLD.type 
      AND local_flags = OLD.local_flags 
      AND deleted = OLD.deleted;
      
    -- Clean up zero counts to keep the table small
    DELETE FROM counts 
    WHERE count = 0 
      AND device_idx = OLD.device_idx 
      AND folder_id = OLD.folder_id
      AND type = OLD.type 
      AND local_flags = OLD.local_flags 
      AND deleted = OLD.deleted;
END;

-- Maintain counts when local_flags are updated
CREATE TRIGGER IF NOT EXISTS trigger_counts_update_flags AFTER UPDATE OF local_flags ON files
WHEN NEW.local_flags != OLD.local_flags
BEGIN
    -- Add to new flags category
    INSERT INTO counts (device_idx, folder_id, type, local_flags, deleted, count, size)
        VALUES (NEW.device_idx, NEW.folder_id, NEW.type, NEW.local_flags, NEW.deleted, 1, NEW.size)
        ON CONFLICT DO UPDATE SET count = count + 1, size = size + NEW.size;
        
    -- Remove from old flags category
    UPDATE counts 
    SET count = count - 1, size = size - OLD.size
    WHERE device_idx = OLD.device_idx 
      AND folder_id = OLD.folder_id
      AND type = OLD.type 
      AND local_flags = OLD.local_flags 
      AND deleted = OLD.deleted;
      
    -- Clean up zero counts
    DELETE FROM counts 
    WHERE count = 0 
      AND device_idx = OLD.device_idx 
      AND folder_id = OLD.folder_id
      AND type = OLD.type 
      AND local_flags = OLD.local_flags 
      AND deleted = OLD.deleted;
END;

-- Maintain counts when deleted flag changes
CREATE TRIGGER IF NOT EXISTS trigger_counts_update_deleted AFTER UPDATE OF deleted ON files
WHEN NEW.deleted != OLD.deleted
BEGIN
    -- Add to new deleted category
    INSERT INTO counts (device_idx, folder_id, type, local_flags, deleted, count, size)
        VALUES (NEW.device_idx, NEW.folder_id, NEW.type, NEW.local_flags, NEW.deleted, 1, NEW.size)
        ON CONFLICT DO UPDATE SET count = count + 1, size = size + NEW.size;
        
    -- Remove from old deleted category
    UPDATE counts 
    SET count = count - 1, size = size - OLD.size
    WHERE device_idx = OLD.device_idx 
      AND folder_id = OLD.folder_id
      AND type = OLD.type 
      AND local_flags = OLD.local_flags 
      AND deleted = OLD.deleted;
      
    -- Clean up zero counts
    DELETE FROM counts 
    WHERE count = 0 
      AND device_idx = OLD.device_idx 
      AND folder_id = OLD.folder_id
      AND type = OLD.type 
      AND local_flags = OLD.local_flags 
      AND deleted = OLD.deleted;
END;

-- ======================================================================
-- UPDATED_AT TRIGGERS
-- ======================================================================

-- Automatically update updated_at timestamp for files
CREATE TRIGGER IF NOT EXISTS trigger_files_updated_at AFTER UPDATE ON files
BEGIN
    UPDATE files 
    SET updated_at = strftime('%s', 'now')
    WHERE sequence = NEW.sequence;
END;

-- ======================================================================
-- DEVICE ACTIVITY TRIGGERS
-- ======================================================================

-- Update device last_seen when files are modified
CREATE TRIGGER IF NOT EXISTS trigger_devices_last_seen AFTER INSERT ON files
WHEN NEW.device_idx != (SELECT idx FROM devices WHERE device_id = 'LOCAL_DEVICE_ID')
BEGIN
    UPDATE devices 
    SET last_seen = strftime('%s', 'now')
    WHERE idx = NEW.device_idx;
END;

-- ======================================================================
-- SYNC EVENTS TRIGGERS
-- ======================================================================

-- Log sync events for file operations
CREATE TRIGGER IF NOT EXISTS trigger_sync_events_insert AFTER INSERT ON files
WHEN NEW.device_idx != (SELECT idx FROM devices WHERE device_id = 'LOCAL_DEVICE_ID')
BEGIN
    INSERT INTO sync_events (event_type, device_idx, folder_id, file_name, event_data)
    VALUES ('FileReceived', NEW.device_idx, NEW.folder_id, NEW.name, 
            json_object('size', NEW.size, 'type', NEW.type, 'sequence', NEW.sequence));
END;

CREATE TRIGGER IF NOT EXISTS trigger_sync_events_delete AFTER UPDATE ON files
WHEN NEW.deleted = 1 AND OLD.deleted = 0
BEGIN
    INSERT INTO sync_events (event_type, device_idx, folder_id, file_name, event_data)
    VALUES ('FileDeleted', NEW.device_idx, NEW.folder_id, NEW.name, 
            json_object('size', OLD.size, 'sequence', NEW.sequence));
END;

-- ======================================================================
-- BLOCK LIST CLEANUP TRIGGERS
-- ======================================================================

-- Clean up unused block lists when files are deleted
CREATE TRIGGER IF NOT EXISTS trigger_cleanup_blocklists AFTER DELETE ON files
WHEN OLD.blocklist_hash IS NOT NULL
BEGIN
    -- Only delete block list if no other files reference it
    DELETE FROM block_lists 
    WHERE blocklist_hash = OLD.blocklist_hash 
      AND NOT EXISTS (
          SELECT 1 FROM files WHERE blocklist_hash = OLD.blocklist_hash
      );
END;

-- ======================================================================
-- PERFORMANCE OPTIMIZATION TRIGGERS
-- ======================================================================

-- Automatically clean up old sync events (keep last 10000 events per folder)
CREATE TRIGGER IF NOT EXISTS trigger_cleanup_old_events AFTER INSERT ON sync_events
WHEN NEW.id % 1000 = 0  -- Only run every 1000th insert for performance
BEGIN
    DELETE FROM sync_events 
    WHERE folder_id = NEW.folder_id 
      AND id NOT IN (
          SELECT id FROM sync_events 
          WHERE folder_id = NEW.folder_id 
          ORDER BY timestamp DESC 
          LIMIT 10000
      );
END;

-- Clean up old performance metrics (keep last 30 days)
CREATE TRIGGER IF NOT EXISTS trigger_cleanup_old_metrics AFTER INSERT ON performance_metrics
WHEN NEW.id % 1000 = 0  -- Only run every 1000th insert for performance
BEGIN
    DELETE FROM performance_metrics 
    WHERE timestamp < strftime('%s', 'now', '-30 days');
END;