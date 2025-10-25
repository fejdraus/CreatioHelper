using CreatioHelper.Application.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

/// <summary>
/// Main SQLite database implementation for sync operations - similar to Syncthing's leveldb
/// </summary>
public class SyncDatabase : ISyncDatabase
{
    private readonly ILogger<SyncDatabase> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
    
    // Repository instances
    private IFileMetadataRepository? _fileMetadata;
    private IBlockInfoRepository? _blockInfo;
    private IDeviceInfoRepository? _deviceInfo;
    private IFolderConfigRepository? _folderConfig;
    private IGlobalStateRepository? _globalState;

    public SyncDatabase(ILogger<SyncDatabase> logger, ILoggerFactory loggerFactory, IOptions<SyncDatabaseOptions> options)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        var dbPath = Path.GetFullPath(options.Value.DatabasePath);
        var directory = Path.GetDirectoryName(dbPath);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory!);
        }
        
        // Syncthing-compatible SQLite performance settings
        _connectionString = $"Data Source={dbPath};Cache=Shared;Pooling=true;Journal Mode=WAL;Synchronous=NORMAL;Temp Store=MEMORY;Busy Timeout=30000;";
    }

    public IFileMetadataRepository FileMetadata => 
        _fileMetadata ??= new FileMetadataRepository(GetConnection, _logger);

    public IBlockInfoRepository BlockInfo => 
        _blockInfo ??= new SqliteBlockInfoRepository(_loggerFactory.CreateLogger<SqliteBlockInfoRepository>(), _connectionString);

    public IDeviceInfoRepository DeviceInfo => 
        _deviceInfo ??= new DeviceInfoRepository(GetConnection, _logger);

    public IFolderConfigRepository FolderConfig => 
        _folderConfig ??= new FolderConfigRepository(GetConnection, _logger);

    public IGlobalStateRepository GlobalState => 
        _globalState ??= new GlobalStateRepository(GetConnection, _logger);

    public async Task InitializeAsync()
    {
        await _connectionSemaphore.WaitAsync();
        try
        {
            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync();
            
            // Syncthing-compatible database performance settings
            await ExecutePragmaAsync("journal_mode=WAL");     // Write-Ahead Logging for concurrency
            await ExecutePragmaAsync("foreign_keys=ON");      // Referential integrity
            await ExecutePragmaAsync("synchronous=NORMAL");   // Performance vs durability balance
            await ExecutePragmaAsync("cache_size=10000");     // 10MB cache for better performance
            await ExecutePragmaAsync("temp_store=MEMORY");    // Use memory for temp tables
            await ExecutePragmaAsync("mmap_size=268435456");  // 256MB memory mapping
            await ExecutePragmaAsync("optimize");             // Gather statistics for query optimization
            
            await CreateTablesAsync();
            await RunMigrationsAsync();
            
            _logger.LogInformation("SyncDatabase initialized successfully");
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    public async Task<ISyncTransaction> BeginTransactionAsync()
    {
        var connection = GetConnection();
        var transaction = await connection.BeginTransactionAsync();
        return new SyncTransaction((SqliteTransaction)transaction);
    }

    public async Task CompactAsync()
    {
        using var command = GetConnection().CreateCommand();
        command.CommandText = "VACUUM;";
        await command.ExecuteNonQueryAsync();
        
        _logger.LogInformation("Database compacted successfully");
    }

    private SqliteConnection GetConnection()
    {
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException("Database not initialized. Call InitializeAsync first.");
        }
        return _connection;
    }

    private async Task ExecutePragmaAsync(string pragma)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateTablesAsync()
    {
        var sql = @"
            CREATE TABLE IF NOT EXISTS file_metadata (
                folder_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                size INTEGER NOT NULL,
                modified_time TEXT NOT NULL,
                permissions INTEGER NOT NULL,
                is_deleted BOOLEAN NOT NULL,
                is_invalid BOOLEAN NOT NULL,
                is_no_permissions BOOLEAN NOT NULL,
                is_symlink BOOLEAN NOT NULL,
                symlink_target TEXT,
                sequence INTEGER NOT NULL,
                version_vector TEXT NOT NULL,
                block_hashes TEXT,
                block_size INTEGER NOT NULL,
                hash TEXT NOT NULL,
                modified_by TEXT NOT NULL,
                locally_changed BOOLEAN NOT NULL,
                platform_data TEXT,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (folder_id, file_name)
            );

            CREATE TABLE IF NOT EXISTS block_info (
                hash TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                weak_hash INTEGER NOT NULL,
                reference_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                last_accessed TEXT NOT NULL,
                is_local BOOLEAN NOT NULL DEFAULT 1,
                compression_type TEXT NOT NULL DEFAULT 'none',
                compressed_size INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS device_info (
                device_id TEXT PRIMARY KEY,
                device_name TEXT NOT NULL,
                addresses TEXT,
                compression BOOLEAN NOT NULL DEFAULT 1,
                introducer BOOLEAN NOT NULL DEFAULT 0,
                skip_introduction_removals BOOLEAN NOT NULL DEFAULT 0,
                introduced_by TEXT,
                paused BOOLEAN NOT NULL DEFAULT 0,
                allowed_networks TEXT,
                auto_accept_folders BOOLEAN NOT NULL DEFAULT 0,
                max_send_kbps INTEGER NOT NULL DEFAULT 0,
                max_recv_kbps INTEGER NOT NULL DEFAULT 0,
                ignored_folders TEXT,
                pending_folders TEXT,
                max_request_kib INTEGER NOT NULL DEFAULT 0,
                untrusted BOOLEAN NOT NULL DEFAULT 0,
                remote_gui_port INTEGER NOT NULL DEFAULT 0,
                num_connections INTEGER NOT NULL DEFAULT 0,
                certificate_name TEXT,
                last_seen TEXT,
                bytes_received INTEGER NOT NULL DEFAULT 0,
                bytes_sent INTEGER NOT NULL DEFAULT 0,
                last_activity TEXT
            );

            CREATE TABLE IF NOT EXISTS folder_config (
                folder_id TEXT PRIMARY KEY,
                folder_label TEXT NOT NULL,
                filesystem_type TEXT NOT NULL DEFAULT 'basic',
                path TEXT NOT NULL,
                type TEXT NOT NULL DEFAULT 'sendreceive',
                devices TEXT,
                rescan_interval_s INTEGER NOT NULL DEFAULT 3600,
                fs_watcher_enabled BOOLEAN NOT NULL DEFAULT 1,
                fs_watcher_delay_s INTEGER NOT NULL DEFAULT 10,
                ignore_perms BOOLEAN NOT NULL DEFAULT 0,
                auto_normalize_unicode BOOLEAN NOT NULL DEFAULT 1,
                min_disk_free TEXT NOT NULL DEFAULT '1%',
                versioning TEXT,
                copy_ownership_from_parent BOOLEAN NOT NULL DEFAULT 0,
                mod_time_window_s INTEGER NOT NULL DEFAULT 0,
                max_conflicts INTEGER NOT NULL DEFAULT 10,
                disable_sparse_files BOOLEAN NOT NULL DEFAULT 0,
                disable_temp_indexes BOOL NOT NULL DEFAULT 0,
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
                last_scan TEXT
            );

            CREATE TABLE IF NOT EXISTS global_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            -- Syncthing-compatible performance indexes for file metadata
            CREATE INDEX IF NOT EXISTS idx_file_metadata_sequence ON file_metadata(folder_id, sequence);
            CREATE INDEX IF NOT EXISTS idx_file_metadata_modified_time ON file_metadata(folder_id, modified_time);
            CREATE INDEX IF NOT EXISTS idx_file_metadata_hash ON file_metadata(hash);
            CREATE INDEX IF NOT EXISTS idx_file_metadata_locally_changed ON file_metadata(locally_changed);
            
            -- Syncthing-compatible performance indexes for block info
            CREATE INDEX IF NOT EXISTS idx_block_info_hash ON block_info(hash);
            CREATE INDEX IF NOT EXISTS idx_block_info_weak_hash ON block_info(weak_hash);
            CREATE INDEX IF NOT EXISTS idx_block_info_size ON block_info(size);
            CREATE INDEX IF NOT EXISTS idx_block_info_reference_count ON block_info(reference_count);
            CREATE INDEX IF NOT EXISTS idx_block_info_last_accessed ON block_info(last_accessed);
            CREATE INDEX IF NOT EXISTS idx_block_info_is_local ON block_info(is_local);
            
            -- Device info performance indexes
            CREATE INDEX IF NOT EXISTS idx_device_info_last_seen ON device_info(last_seen);
            CREATE INDEX IF NOT EXISTS idx_device_info_paused ON device_info(paused);
            
            -- Folder config performance indexes  
            CREATE INDEX IF NOT EXISTS idx_folder_config_type ON folder_config(type);
            CREATE INDEX IF NOT EXISTS idx_folder_config_paused ON folder_config(paused);
            CREATE INDEX IF NOT EXISTS idx_folder_config_last_scan ON folder_config(last_scan);
        ";

        using var command = GetConnection().CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task RunMigrationsAsync()
    {
        var currentVersion = await GlobalState.GetSchemaVersionAsync();
        const int latestVersion = 3;

        if (currentVersion < latestVersion)
        {
            _logger.LogInformation($"Running database migrations from version {currentVersion} to {latestVersion}");
            
            // Migration v1 -> v2: Add performance indexes
            if (currentVersion < 2)
            {
                await ApplyMigrationV2Async();
                _logger.LogInformation("Applied migration v2: Performance indexes");
            }
            
            // Migration v2 -> v3: Add Syncthing compatibility enhancements
            if (currentVersion < 3)
            {
                await ApplyMigrationV3Async();
                _logger.LogInformation("Applied migration v3: Syncthing compatibility enhancements");
            }
            
            await GlobalState.SetSchemaVersionAsync(latestVersion);
            _logger.LogInformation($"Database migrated to version {latestVersion}");
        }
    }

    private async Task ApplyMigrationV2Async()
    {
        var sql = @"
            -- Add missing performance indexes for v2
            CREATE INDEX IF NOT EXISTS idx_file_metadata_version_vector ON file_metadata(version_vector);
            CREATE INDEX IF NOT EXISTS idx_file_metadata_is_deleted ON file_metadata(is_deleted);
            CREATE INDEX IF NOT EXISTS idx_block_info_compression_type ON block_info(compression_type);
        ";
        
        using var command = GetConnection().CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task ApplyMigrationV3Async()
    {
        var sql = @"
            -- Add Syncthing-compatible blocklists table
            CREATE TABLE IF NOT EXISTS blocklists (
                blocklist_hash TEXT PRIMARY KEY,
                blprotobuf BLOB NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            
            -- Add blocks table with Syncthing structure
            CREATE TABLE IF NOT EXISTS blocks (
                hash TEXT NOT NULL,
                blocklist_hash TEXT NOT NULL,
                idx INTEGER NOT NULL,
                offset INTEGER NOT NULL,
                size INTEGER NOT NULL,
                PRIMARY KEY (hash, blocklist_hash, idx),
                FOREIGN KEY(blocklist_hash) REFERENCES blocklists(blocklist_hash) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
            );
            
            -- Add performance indexes for new tables
            CREATE INDEX IF NOT EXISTS idx_blocklists_created_at ON blocklists(created_at);
            CREATE INDEX IF NOT EXISTS idx_blocks_hash ON blocks(hash);
            CREATE INDEX IF NOT EXISTS idx_blocks_blocklist_hash ON blocks(blocklist_hash);
        ";
        
        using var command = GetConnection().CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connectionSemaphore?.Dispose();
    }
}

/// <summary>
/// Configuration options for SyncDatabase
/// </summary>
public class SyncDatabaseOptions
{
    public string DatabasePath { get; set; } = "sync.db";
}