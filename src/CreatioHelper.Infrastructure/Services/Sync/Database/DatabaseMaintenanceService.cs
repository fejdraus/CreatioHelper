using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

/// <summary>
/// Background service for SQLite database maintenance.
/// Performs cleanup, optimization, and compaction tasks.
/// </summary>
public class DatabaseMaintenanceService : BackgroundService, IDatabaseMaintenanceService
{
    private readonly ILogger<DatabaseMaintenanceService> _logger;
    private readonly string _connectionString;
    private Timer? _timer;
    private bool _isRunning;

    /// <summary>
    /// Interval between automatic maintenance runs. Default is 1 hour.
    /// </summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Retention period for deleted file records. Default is 24 hours.
    /// </summary>
    public TimeSpan DeletedFileRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Retention period for old events. Default is 7 days.
    /// </summary>
    public TimeSpan EventRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Timestamp of the last maintenance run.
    /// </summary>
    public DateTime? LastMaintenanceRun { get; private set; }

    /// <summary>
    /// Whether the service is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    public DatabaseMaintenanceService(
        ILogger<DatabaseMaintenanceService> logger,
        string connectionString)
    {
        _logger = logger;
        _connectionString = connectionString;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _isRunning = true;
        _logger.LogInformation("Database maintenance service started with interval {Interval}", MaintenanceInterval);

        // Run initial maintenance after a short delay
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunMaintenanceNowAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scheduled database maintenance");
            }

            try
            {
                await Task.Delay(MaintenanceInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _isRunning = false;
        _logger.LogInformation("Database maintenance service stopped");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        await base.StopAsync(cancellationToken);
    }

    Task IDatabaseMaintenanceService.StartAsync(CancellationToken cancellationToken)
    {
        return StartAsync(cancellationToken);
    }

    Task IDatabaseMaintenanceService.StopAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    public async Task RunMaintenanceNowAsync(CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogInformation("Starting database maintenance...");

        using var maintenanceTimer = DatabaseMetrics.StartMaintenance();

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // 1. Clean up deleted file records older than retention period
            var deletedFilesCount = await CleanupDeletedFilesAsync(connection, cancellationToken);
            _logger.LogDebug("Cleaned up {Count} deleted file records", deletedFilesCount);
            DatabaseMetrics.RecordRecordsCleaned("deleted_files", deletedFilesCount);

            // 2. Clean up orphaned records (files without folders)
            var orphanedCount = await CleanupOrphanedRecordsAsync(connection, cancellationToken);
            _logger.LogDebug("Cleaned up {Count} orphaned records", orphanedCount);
            DatabaseMetrics.RecordRecordsCleaned("orphaned_records", orphanedCount);

            // 3. Clean up old events
            var eventsCount = await CleanupOldEventsAsync(connection, cancellationToken);
            _logger.LogDebug("Cleaned up {Count} old events", eventsCount);
            DatabaseMetrics.RecordRecordsCleaned("old_events", eventsCount);

            // 4. WAL checkpoint (flush write-ahead log to main database)
            await CheckpointAsync(connection, cancellationToken);
            _logger.LogDebug("WAL checkpoint completed");

            // 5. Optimize query planner and update statistics
            await OptimizeAsync(connection, cancellationToken);
            _logger.LogDebug("Database optimization completed");

            // 6. Incremental vacuum to reclaim space
            await IncrementalVacuumAsync(connection, cancellationToken);
            _logger.LogDebug("Incremental vacuum completed");

            LastMaintenanceRun = DateTime.UtcNow;
            var duration = DateTime.UtcNow - startTime;

            // Record metrics
            DatabaseMetrics.RecordMaintenanceRun();

            // Update database size metrics
            var stats = await GetStatisticsAsync(cancellationToken);
            DatabaseMetrics.SetDatabaseSize(stats.DatabaseSizeBytes);
            DatabaseMetrics.SetFreeSpace(stats.FreeSpaceBytes);
            DatabaseMetrics.SetFileMetadataCount(stats.FileMetadataCount);
            DatabaseMetrics.SetEventLogCount(stats.EventCount);

            _logger.LogInformation(
                "Database maintenance completed in {Duration}ms. Cleaned: {DeletedFiles} deleted files, {Orphaned} orphaned records, {Events} old events",
                duration.TotalMilliseconds, deletedFilesCount, orphanedCount, eventsCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database maintenance failed");
            throw;
        }
    }

    private async Task<int> CleanupDeletedFilesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - DeletedFileRetention;

        using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM file_metadata
            WHERE is_deleted = 1 AND modified_time < @cutoff";
        command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> CleanupOrphanedRecordsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM file_metadata
            WHERE folder_id NOT IN (SELECT folder_id FROM folder_config)";

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> CleanupOldEventsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - EventRetention;

        using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM sync_events
            WHERE timestamp < @cutoff";
        command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CheckpointAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task OptimizeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // PRAGMA optimize runs the ANALYZE command on tables that would benefit from it
        using var optimizeCommand = connection.CreateCommand();
        optimizeCommand.CommandText = "PRAGMA optimize";
        await optimizeCommand.ExecuteNonQueryAsync(cancellationToken);

        // ANALYZE updates the statistics used by the query planner
        using var analyzeCommand = connection.CreateCommand();
        analyzeCommand.CommandText = "ANALYZE";
        await analyzeCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task IncrementalVacuumAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // First ensure auto_vacuum is set to incremental (2)
        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "PRAGMA auto_vacuum";
        var result = await checkCommand.ExecuteScalarAsync(cancellationToken);

        if (result is long autoVacuumMode && autoVacuumMode == 2)
        {
            // Run incremental vacuum to reclaim some free pages
            using var vacuumCommand = connection.CreateCommand();
            vacuumCommand.CommandText = "PRAGMA incremental_vacuum(100)"; // Vacuum up to 100 pages
            await vacuumCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Executes a raw SQL command. For advanced maintenance operations.
    /// </summary>
    public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        if (param != null)
        {
            var properties = param.GetType().GetProperties();
            foreach (var prop in properties)
            {
                var value = prop.GetValue(param);
                command.Parameters.AddWithValue($"@{prop.Name}", value ?? DBNull.Value);
            }
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Gets database statistics for monitoring.
    /// </summary>
    public async Task<DatabaseStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var stats = new DatabaseStatistics();

        // Get page count
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA page_count";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            stats.PageCount = result is long pages ? pages : 0;
        }

        // Get page size
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA page_size";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            stats.PageSize = result is long size ? size : 4096;
        }

        // Get freelist count
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA freelist_count";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            stats.FreelistCount = result is long freelist ? freelist : 0;
        }

        // Get file count
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM file_metadata";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            stats.FileMetadataCount = result is long count ? count : 0;
        }

        // Get event count
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sync_events";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            stats.EventCount = result is long count ? count : 0;
        }

        stats.DatabaseSizeBytes = stats.PageCount * stats.PageSize;
        stats.FreeSpaceBytes = stats.FreelistCount * stats.PageSize;

        return stats;
    }
}

/// <summary>
/// Database statistics for monitoring.
/// </summary>
public class DatabaseStatistics
{
    public long PageCount { get; set; }
    public long PageSize { get; set; }
    public long FreelistCount { get; set; }
    public long DatabaseSizeBytes { get; set; }
    public long FreeSpaceBytes { get; set; }
    public long FileMetadataCount { get; set; }
    public long EventCount { get; set; }
}
