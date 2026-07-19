using Microsoft.Extensions.Logging;

namespace CreatioHelper.Agent.Services;

public class WebServerAccessStatus
{
    private readonly object _gate = new();

    public bool RequiresElevation { get; private set; }
    public string? Message { get; private set; }
    public DateTime? SinceUtc { get; private set; }

    public bool ReportPermissionIssue(string operation, string? stderr, ILogger logger)
    {
        lock (_gate)
        {
            var alreadyFlagged = RequiresElevation;
            RequiresElevation = true;
            Message = "Web server management requires elevated privileges. Run the agent as Administrator to manage IIS.";
            SinceUtc ??= DateTime.UtcNow;

            if (!alreadyFlagged)
            {
                logger.LogWarning(
                    "Web server management is unavailable because the agent lacks elevated privileges (operation: {Operation}). Run the agent as Administrator to manage IIS. Details: {Details}",
                    operation,
                    (stderr ?? string.Empty).Trim());
                return true;
            }

            return false;
        }
    }

    public void ReportSuccess()
    {
        lock (_gate)
        {
            if (!RequiresElevation)
            {
                return;
            }

            RequiresElevation = false;
            Message = null;
            SinceUtc = null;
        }
    }
}
