namespace CreatioHelper.Application.Operations;

public class DeploymentResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string? ErrorMessage { get; init; }

    public static DeploymentResult Ok() => new() { Success = true };
    public static DeploymentResult Fail(string? message = null) => new() { Success = false, ErrorMessage = message };
    public static DeploymentResult CancelledResult() => new() { Success = false, Cancelled = true };
}
