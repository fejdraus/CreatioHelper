using System.Collections.Generic;
namespace CreatioHelper.Core.Models;

public class AgentConfig
{
    public string ApiKey { get; set; } = "default-api-key";
    public int Port { get; set; } = 8080;
    public bool EnableSwagger { get; set; } = false;
    public int MonitoringIntervalSeconds { get; set; } = 10;
    public List<string> AllowedHosts { get; set; } = new() { "*" };
}