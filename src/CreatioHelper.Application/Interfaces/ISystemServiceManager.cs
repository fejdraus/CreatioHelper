namespace CreatioHelper.Application.Interfaces;

public interface ISystemServiceManager
{
    Task<bool> StartServiceAsync(string serviceName);
    Task<bool> StopServiceAsync(string serviceName);
    Task<string?> GetServiceStateAsync(string serviceName);
}
