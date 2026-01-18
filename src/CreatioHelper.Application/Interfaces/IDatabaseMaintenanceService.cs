namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Service for database maintenance operations.
/// Provides background cleanup and optimization for SQLite database.
/// </summary>
public interface IDatabaseMaintenanceService
{
    /// <summary>
    /// Starts the maintenance service with periodic background tasks.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the maintenance service.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Runs all maintenance tasks immediately.
    /// </summary>
    Task RunMaintenanceNowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets or sets the interval between automatic maintenance runs.
    /// </summary>
    TimeSpan MaintenanceInterval { get; set; }

    /// <summary>
    /// Gets the timestamp of the last maintenance run.
    /// </summary>
    DateTime? LastMaintenanceRun { get; }

    /// <summary>
    /// Gets whether the service is currently running.
    /// </summary>
    bool IsRunning { get; }
}
