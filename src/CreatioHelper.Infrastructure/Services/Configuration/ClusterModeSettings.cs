using Microsoft.Extensions.Configuration;

namespace CreatioHelper.Infrastructure.Services.Configuration;

public static class ClusterModeSettings
{
    public static bool IsClusterMode(IConfiguration configuration)
    {
        var mode = configuration["Cluster:Mode"];
        if (string.Equals(mode, "Classic", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (string.Equals(mode, "Cluster", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return configuration.GetValue<bool>("ClusterKey:Enabled");
    }
}
