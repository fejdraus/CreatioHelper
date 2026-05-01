namespace CreatioHelper.Application.Services.Updates;

public abstract record UpdateState
{
    public sealed record Disabled : UpdateState;

    public sealed record Idle(string? Error = null, bool NotAvailable = false) : UpdateState;

    public sealed record Checking : UpdateState;

    public sealed record Available(string Version, string ReleaseUrl, string AssetUrl, bool IsPrerelease) : UpdateState;

    public sealed record Downloading(string Version, double Percent) : UpdateState;

    public sealed record Ready(string Version) : UpdateState;
}
