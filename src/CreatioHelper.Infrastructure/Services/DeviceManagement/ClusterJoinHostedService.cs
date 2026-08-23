using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

public class ClusterJoinHostedService : BackgroundService
{
    private readonly ILogger<ClusterJoinHostedService> _logger;
    private readonly IClusterMembershipService _membership;
    private readonly ClusterKeyConfiguration _config;

    public ClusterJoinHostedService(
        ILogger<ClusterJoinHostedService> logger,
        IClusterMembershipService membership,
        IOptions<ClusterKeyConfiguration> config)
    {
        _logger = logger;
        _membership = membership;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_membership.IsEnabled)
        {
            _logger.LogDebug("Cluster key is disabled, roster synchronization will not run");
            return;
        }

        if (_config.InitialJoinDelaySeconds > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.InitialJoinDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _membership.JoinClusterAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cluster roster synchronization pass failed");
            }

            if (_config.RosterSyncIntervalMinutes <= 0)
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_config.RosterSyncIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
