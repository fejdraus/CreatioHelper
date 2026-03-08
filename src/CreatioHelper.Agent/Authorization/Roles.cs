namespace CreatioHelper.Agent.Authorization;

public static class Roles
{
    /// <summary>
    /// Administrator role - full access to all operations
    /// </summary>
    public const string Admin = "admin";
    
    /// <summary>
    /// Regular user role - can read and write, but cannot manage users
    /// </summary>
    public const string User = "user";

    /// <summary>
    /// Read-only role - can only view information, cannot modify
    /// </summary>
    public const string ReadOnly = "readonly";

    /// <summary>
    /// Monitoring role - can view metrics and monitoring data
    /// </summary>
    public const string Monitor = "monitor";

    /// <summary>
    /// All roles that can read all data (config + monitoring)
    /// </summary>
    public const string ReadRoles = $"{Admin},{User},{ReadOnly}";

    /// <summary>
    /// All roles that can view monitoring data (status, metrics, events, logs, diagnostics)
    /// </summary>
    public const string MonitorRoles = $"{Admin},{User},{ReadOnly},{Monitor}";

    /// <summary>
    /// All roles that can write/modify data
    /// </summary>
    public const string WriteRoles = $"{Admin},{User}";
}