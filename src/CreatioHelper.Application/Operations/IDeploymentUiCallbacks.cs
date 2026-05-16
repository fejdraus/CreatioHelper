namespace CreatioHelper.Application.Operations;

public interface IDeploymentUiCallbacks
{
    void OnBusyChanged(bool isBusy);
    void OnStopButtonEnabledChanged(bool enabled);
    void OnStartButtonText(string text);
    void OnServerControlsEnabledChanged(bool enabled);
}

public sealed class NullDeploymentUiCallbacks : IDeploymentUiCallbacks
{
    public static readonly NullDeploymentUiCallbacks Instance = new();
    public void OnBusyChanged(bool isBusy) { }
    public void OnStopButtonEnabledChanged(bool enabled) { }
    public void OnStartButtonText(string text) { }
    public void OnServerControlsEnabledChanged(bool enabled) { }
}
