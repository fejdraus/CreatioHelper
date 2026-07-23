using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Operations;

public interface IDeploymentOrchestrator
{
    Task<DeploymentResult> RunAsync(
        DeploymentOptions options,
        IDeploymentUiCallbacks? ui = null,
        CancellationToken cancellationToken = default);

    Task<DeploymentResult> RestoreConfigurationAsync(
        RestoreConfigurationOptions options,
        IDeploymentUiCallbacks? ui = null,
        CancellationToken cancellationToken = default);

    Task StartAllIisAsync(IEnumerable<ServerInfo> servers, CancellationToken cancellationToken = default);
    Task StopAllIisAsync(IEnumerable<ServerInfo> servers, CancellationToken cancellationToken = default);
    Task RestartAllIisAsync(IEnumerable<ServerInfo> servers, IDeploymentUiCallbacks? ui = null, CancellationToken cancellationToken = default);
}
