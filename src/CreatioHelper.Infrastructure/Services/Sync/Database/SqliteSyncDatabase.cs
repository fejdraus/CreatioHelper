using System.Data;
using System.Reflection;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

/// <summary>
/// SQLite-based implementation of ISyncDatabase compatible with Syncthing's database structure
/// Provides high-performance metadata storage with optimized indexing and query patterns
/// </summary>
public class SqliteSyncDatabase : ISyncDatabase
{
    private readonly ILogger<SqliteSyncDatabase> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly object _lock = new();
    private bool _disposed = false;

    // Repository implementations
    private IFileMetadataRepository? _fileMetadata;
    private IBlockInfoRepository? _blockInfo;
    private IDeviceInfoRepository? _deviceInfo;
    private IFolderConfigRepository? _folderConfig;
    private IGlobalStateRepository? _globalState;
    private IEventLogRepository? _eventLog;

    public SqliteSyncDatabase(ILogger<SqliteSyncDatabase> logger, ILoggerFactory loggerFactory, string databasePath)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _databasePath = databasePath;
        _connectionString = $"Data Source={databasePath};Cache=Shared;Foreign Keys=True;";
        
        _logger.LogInformation("Initialized SQLite sync database: {DatabasePath}", databasePath);
    }

    public IFileMetadataRepository FileMetadata => 
        _fileMetadata ??= new FileMetadataRepository(() => CreateConnection(), _logger);

    public IBlockInfoRepository BlockInfo => 
        _blockInfo ??= new SqliteBlockInfoRepository(_loggerFactory.CreateLogger<SqliteBlockInfoRepository>(), _connectionString);

    public IDeviceInfoRepository DeviceInfo => 
        _deviceInfo ??= new DeviceInfoRepository(() => CreateConnection(), _logger);

    public IFolderConfigRepository FolderConfig => 
        _folderConfig ??= new FolderConfigRepository(() => CreateConnection(), _logger);

    public IGlobalStateRepository GlobalState =>
        _globalState ??= new GlobalStateRepository(() => CreateConnection(), _logger);

    public IEventLogRepository EventLog =>
        _eventLog ??= new EventLogRepository(() => CreateConnection(), _logger);

    public async Task InitializeAsync()
    {
        try
        {
            _logger.LogInformation("Initializing sync database...");

            // Ensure database directory exists
            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                _logger.LogDebug("Created database directory: {Directory}", directory);
            }

            // Apply database schema
            await ApplySchemaAsync();

            // Run any pending migrations
            await RunMigrationsAsync();

            // Optimize database settings
            await OptimizeDatabaseAsync();

            _logger.LogInformation("Sync database initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize sync database");
            throw;
        }
    }

    public async Task<ISyncTransaction> BeginTransactionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        
        return new SqliteSyncTransaction(connection, (SqliteTransaction)transaction, _logger);
    }

    public async Task CompactAsync()
    {
        try
        {
            _logger.LogInformation("Starting database compaction...");

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // Run VACUUM to compact the database
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM;";
                await command.ExecuteNonQueryAsync();
            }

            // Update statistics for query optimization
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "ANALYZE;";
                await command.ExecuteNonQueryAsync();
            }

            // Get database size info
            var fileInfo = new FileInfo(_databasePath);
            var sizeKB = fileInfo.Length / 1024;

            _logger.LogInformation("Database compaction completed. Size: {SizeKB} KB", sizeKB);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database compaction failed");
            throw;
        }
    }

    private async Task ApplySchemaAsync()
    {
        _logger.LogDebug("Applying database schema...");

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Create all required tables for 100% Syncthing compatibility
        await ExecuteSqlCommandAsync(connection, CreateSyncthingCompatibleSchema());
        await ExecuteSqlCommandAsync(connection, CreateIndexes());
        await ExecuteSqlCommandAsync(connection, CreateTriggers());
    }

    private static string CreateSyncthingCompatibleSchema()
    {
        return @"
            -- Schema migrations tracking table
            CREATE TABLE IF NOT EXISTS schema_migrations (
                schema_version INTEGER PRIMARY KEY,
                applied_at INTEGER NOT NULL,
                agent_version TEXT NOT NULL
            );

            -- Global state key-value storage (similar to Syncthing's misc table)
            CREATE TABLE IF NOT EXISTS global_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            -- Device information (similar to Syncthing's device config)
            CREATE TABLE IF NOT EXISTS device_info (
                device_id TEXT PRIMARY KEY,
                device_name TEXT NOT NULL,
                addresses TEXT, -- JSON array of addresses
                compression TEXT NOT NULL DEFAULT 'metadata',
                introducer BOOLEAN NOT NULL DEFAULT 0,
                skip_introduction_removals BOOLEAN NOT NULL DEFAULT 0,
                introduced_by TEXT,
                paused BOOLEAN NOT NULL DEFAULT 0,
                allowed_networks TEXT, -- JSON array
                auto_accept_folders BOOLEAN NOT NULL DEFAULT 0,
                max_send_kbps INTEGER NOT NULL DEFAULT 0,
                max_recv_kbps INTEGER NOT NULL DEFAULT 0,
                ignored_folders TEXT, -- JSON array
                pending_folders TEXT, -- JSON array
                max_request_kib INTEGER NOT NULL DEFAULT 1024,
                untrusted BOOLEAN NOT NULL DEFAULT 0,
                remote_gui_port INTEGER NOT NULL DEFAULT 0,
                num_connections INTEGER NOT NULL DEFAULT 0,
                certificate_name TEXT,
                last_seen TEXT,
                bytes_received INTEGER NOT NULL DEFAULT 0,
                bytes_sent INTEGER NOT NULL DEFAULT 0,
                last_activity TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            -- Folder configuration (similar to Syncthing's folder config)
            CREATE TABLE IF NOT EXISTS folder_config (
                folder_id TEXT PRIMARY KEY,
                folder_label TEXT NOT NULL,
                filesystem_type TEXT NOT NULL DEFAULT 'basic',
                path TEXT NOT NULL,
                type TEXT NOT NULL DEFAULT 'sendreceive',
                devices TEXT, -- JSON array of device IDs
                rescan_interval_s INTEGER NOT NULL DEFAULT 3600,
                fs_watcher_enabled BOOLEAN NOT NULL DEFAULT 1,
                fs_watcher_delay_s INTEGER NOT NULL DEFAULT 10,
                ignore_perms BOOLEAN NOT NULL DEFAULT 0,
                auto_normalize_unicode BOOLEAN NOT NULL DEFAULT 1,
                min_disk_free TEXT NOT NULL DEFAULT '1%',
                versioning TEXT, -- JSON versioning config
                copy_ownership_from_parent BOOLEAN NOT NULL DEFAULT 0,
                mod_time_window_s INTEGER NOT NULL DEFAULT 0,
                max_conflicts INTEGER NOT NULL DEFAULT 10,
                disable_sparse_files BOOLEAN NOT NULL DEFAULT 0,
                disable_temp_indexes BOOLEAN NOT NULL DEFAULT 0,
                paused BOOLEAN NOT NULL DEFAULT 0,
                weak_hash_threshold_pct INTEGER NOT NULL DEFAULT 25,
                marker_name TEXT NOT NULL DEFAULT '.stfolder',
                copy_range_method TEXT NOT NULL DEFAULT 'standard',
                case_sensitive_fs BOOLEAN NOT NULL DEFAULT 1,
                junctioned_as_directory BOOLEAN NOT NULL DEFAULT 0,
                sync_ownership BOOLEAN NOT NULL DEFAULT 0,
                send_ownership BOOLEAN NOT NULL DEFAULT 0,
                sync_xattrs BOOLEAN NOT NULL DEFAULT 0,
                send_xattrs BOOLEAN NOT NULL DEFAULT 0,
                file_count INTEGER NOT NULL DEFAULT 0,
                total_size INTEGER NOT NULL DEFAULT 0,
                last_scan TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            -- File metadata (similar to Syncthing's files table)
            CREATE TABLE IF NOT EXISTS file_metadata (
                folder_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                size INTEGER NOT NULL DEFAULT 0,
                modified_time TEXT NOT NULL,
                permissions INTEGER,
                is_deleted BOOLEAN NOT NULL DEFAULT 0,
                is_invalid BOOLEAN NOT NULL DEFAULT 0,
                is_no_permissions BOOLEAN NOT NULL DEFAULT 0,
                is_symlink BOOLEAN NOT NULL DEFAULT 0,
                symlink_target TEXT,
                sequence INTEGER NOT NULL DEFAULT 0,
                version_vector TEXT NOT NULL DEFAULT '[]',
                block_hashes TEXT, -- JSON array of block hashes
                block_size INTEGER NOT NULL DEFAULT 0,
                hash TEXT, -- File hash
                modified_by TEXT NOT NULL DEFAULT '',
                locally_changed BOOLEAN NOT NULL DEFAULT 0,
                platform_data TEXT, -- JSON platform-specific data
                updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (folder_id, file_name)
            );

            -- Block information (similar to Syncthing's block storage metadata)
            CREATE TABLE IF NOT EXISTS block_info (
                hash TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                weak_hash INTEGER NOT NULL,
                compression TEXT,
                encrypted BOOLEAN NOT NULL DEFAULT 0,
                reference_count INTEGER NOT NULL DEFAULT 1,
                first_seen TEXT NOT NULL DEFAULT (datetime('now')),
                last_accessed TEXT NOT NULL DEFAULT (datetime('now')),
                storage_path TEXT
            );

            -- Folder-device relationships
            CREATE TABLE IF NOT EXISTS folder_devices (
                folder_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                introduced_by TEXT,
                encrypted_password TEXT,
                PRIMARY KEY (folder_id, device_id),
                FOREIGN KEY (folder_id) REFERENCES folder_config(folder_id) ON DELETE CASCADE,
                FOREIGN KEY (device_id) REFERENCES device_info(device_id) ON DELETE CASCADE
            );

            -- Sync statistics (for performance tracking)
            CREATE TABLE IF NOT EXISTS sync_statistics (
                metric_name TEXT NOT NULL,
                folder_id TEXT,
                device_id TEXT,
                value INTEGER NOT NULL DEFAULT 0,
                timestamp TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (metric_name, folder_id, device_id)
            );

            -- Event log (similar to Syncthing's events)
            CREATE TABLE IF NOT EXISTS sync_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type TEXT NOT NULL,
                folder_id TEXT,
                device_id TEXT,
                file_name TEXT,
                event_data TEXT, -- JSON event data
                timestamp TEXT NOT NULL DEFAULT (datetime('now'))
            );
        ";
    }

    private static string CreateIndexes()
    {
        return @"
            -- Performance indexes for common queries
            CREATE INDEX IF NOT EXISTS idx_file_metadata_sequence ON file_metadata(folder_id, sequence);
            CREATE INDEX IF NOT EXISTS idx_file_metadata_modified ON file_metadata(folder_id, modified_time);
            CREATE INDEX IF NOT EXISTS idx_file_metadata_deleted ON file_metadata(folder_id, is_deleted);
            CREATE INDEX IF NOT EXISTS idx_file_metadata_locally_changed ON file_metadata(folder_id, locally_changed);
            
            CREATE INDEX IF NOT EXISTS idx_block_info_weak_hash ON block_info(weak_hash);
            CREATE INDEX IF NOT EXISTS idx_block_info_size ON block_info(size);
            CREATE INDEX IF NOT EXISTS idx_block_info_last_accessed ON block_info(last_accessed);
            
            CREATE INDEX IF NOT EXISTS idx_device_info_last_seen ON device_info(last_seen);
            CREATE INDEX IF NOT EXISTS idx_device_info_paused ON device_info(paused);
            
            CREATE INDEX IF NOT EXISTS idx_folder_config_type ON folder_config(type);
            CREATE INDEX IF NOT EXISTS idx_folder_config_paused ON folder_config(paused);
            
            CREATE INDEX IF NOT EXISTS idx_sync_events_timestamp ON sync_events(timestamp);
            CREATE INDEX IF NOT EXISTS idx_sync_events_type ON sync_events(event_type);
            CREATE INDEX IF NOT EXISTS idx_sync_events_folder ON sync_events(folder_id);
        ";
    }

    private static string CreateTriggers()
    {
        return @"
            -- Auto-update timestamps
            CREATE TRIGGER IF NOT EXISTS device_info_updated 
                AFTER UPDATE ON device_info
                BEGIN
                    UPDATE device_info SET updated_at = datetime('now') WHERE device_id = NEW.device_id;
                END;

            CREATE TRIGGER IF NOT EXISTS folder_config_updated 
                AFTER UPDATE ON folder_config
                BEGIN
                    UPDATE folder_config SET updated_at = datetime('now') WHERE folder_id = NEW.folder_id;
                END;

            CREATE TRIGGER IF NOT EXISTS file_metadata_updated 
                AFTER UPDATE ON file_metadata
                BEGIN
                    UPDATE file_metadata SET updated_at = datetime('now') 
                    WHERE folder_id = NEW.folder_id AND file_name = NEW.file_name;
                END;

            -- Auto-increment sequences
            CREATE TRIGGER IF NOT EXISTS file_metadata_sequence 
                BEFORE INSERT ON file_metadata
                WHEN NEW.sequence = 0
                BEGIN
                    UPDATE NEW SET sequence = (
                        SELECT COALESCE(MAX(sequence), 0) + 1 
                        FROM file_metadata 
                        WHERE folder_id = NEW.folder_id
                    );
                END;
        ";
    }

    private async Task ExecuteSqlCommandAsync(SqliteConnection connection, string sql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute SQL command");
            throw;
        }
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private async Task ExecuteSchemaFileAsync(SqliteConnection connection, string fileName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"CreatioHelper.Infrastructure.Services.Sync.Database.Sql.{fileName}";

            await using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                // Try reading from file system
                var filePath = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                    "Services", "Sync", "Database", "Sql", fileName);

                if (File.Exists(filePath))
                {
                    var sql = await File.ReadAllTextAsync(filePath);
                    await ExecuteSqlAsync(connection, sql);
                    _logger.LogDebug("Applied schema file: {FileName}", fileName);
                    return;
                }

                _logger.LogWarning("Schema file not found: {FileName}", fileName);
                return;
            }

            using var reader = new StreamReader(stream);
            var sqlContent = await reader.ReadToEndAsync();
            await ExecuteSqlAsync(connection, sqlContent);
            
            _logger.LogDebug("Applied schema file: {FileName}", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute schema file: {FileName}", fileName);
            throw;
        }
    }

    private async Task ExecuteSqlAsync(SqliteConnection connection, string sql)
    {
        // Split SQL into individual statements (simple implementation)
        var statements = sql.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s) && !s.StartsWith("--"));

        foreach (var statement in statements)
        {
            if (string.IsNullOrWhiteSpace(statement)) continue;

            using var command = connection.CreateCommand();
            command.CommandText = statement + ";";
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task RunMigrationsAsync()
    {
        _logger.LogDebug("Checking for pending migrations...");

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Get current schema version
        var currentVersion = await GetSchemaVersionAsync(connection);
        var targetVersion = GetTargetSchemaVersion();

        if (currentVersion < targetVersion)
        {
            _logger.LogInformation("Running database migrations from version {Current} to {Target}", 
                currentVersion, targetVersion);

            // Run migrations (placeholder for future migration logic)
            await UpdateSchemaVersionAsync(connection, targetVersion);

            _logger.LogInformation("Database migrations completed");
        }
        else
        {
            _logger.LogDebug("Database schema is up to date (version {Version})", currentVersion);
        }
    }

    private async Task<int> GetSchemaVersionAsync(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(schema_version) FROM schema_migrations";
            
            var result = await command.ExecuteScalarAsync();
            return result == DBNull.Value || result == null ? 0 : Convert.ToInt32(result);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // Table doesn't exist
        {
            return 0;
        }
    }

    private int GetTargetSchemaVersion()
    {
        // Current schema version - increment this when adding new migrations
        return 1;
    }

    private async Task UpdateSchemaVersionAsync(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO schema_migrations (schema_version, applied_at, agent_version)
            VALUES (@version, @timestamp, @agentVersion)";
        
        command.Parameters.AddWithValue("@version", version);
        command.Parameters.AddWithValue("@timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@agentVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0");
        
        await command.ExecuteNonQueryAsync();
    }

    private async Task OptimizeDatabaseAsync()
    {
        _logger.LogDebug("Optimizing database settings...");

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Set optimal PRAGMA settings for sync workloads
        var pragmas = new Dictionary<string, string>
        {
            // Use WAL mode for better concurrency
            { "journal_mode", "WAL" },
            
            // Optimize for performance over safety (sync handles consistency)
            { "synchronous", "NORMAL" },
            
            // Enable foreign key constraints
            { "foreign_keys", "ON" },
            
            // Set reasonable cache size (64MB)
            { "cache_size", "-65536" },
            
            // Optimize page size for SSD storage
            { "page_size", "4096" },
            
            // Set busy timeout for better concurrency
            { "busy_timeout", "5000" },
            
            // Enable automatic index creation
            { "automatic_index", "ON" },
            
            // Optimize temp storage
            { "temp_store", "MEMORY" }
        };

        foreach (var pragma in pragmas)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA {pragma.Key} = {pragma.Value}";
            await command.ExecuteNonQueryAsync();
        }

        _logger.LogDebug("Database optimization completed");
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_disposed) return;

            try
            {
                _fileMetadata?.Dispose();
                _blockInfo?.Dispose();
                _deviceInfo?.Dispose();
                _folderConfig?.Dispose();
                _globalState?.Dispose();
                _eventLog?.Dispose();

                _logger.LogDebug("SQLite sync database disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing SQLite sync database");
            }

            _disposed = true;
        }
    }
}

/// <summary>
/// SQLite-specific transaction implementation
/// </summary>
internal class SqliteSyncTransaction : ISyncTransaction
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly ILogger _logger;
    private bool _disposed = false;

    public SqliteSyncTransaction(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        _connection = connection;
        _transaction = transaction;
        _logger = logger;
    }

    public async Task CommitAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SqliteSyncTransaction));
        
        try
        {
            await _transaction.CommitAsync();
            _logger.LogDebug("Transaction committed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to commit transaction");
            throw;
        }
    }

    public async Task RollbackAsync()
    {
        if (_disposed) return;
        
        try
        {
            await _transaction.RollbackAsync();
            _logger.LogDebug("Transaction rolled back");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rollback transaction");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _transaction?.Dispose();
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing transaction");
        }

        _disposed = true;
    }
}