namespace CreatioHelper.Agent.Authorization;

public static class Roles
{
    /// <summary>
    /// Administrator role - full access to all operations
    /// </summary>
    public const string Admin = "admin";
    
    /// <summary>
    /// Read-only role - can only view information, cannot modify
    /// </summary>
    public const string ReadOnly = "readonly";
    
    /// <summary>
    /// Monitoring role - can view metrics and monitoring data
    /// </summary>
    public const string Monitor = "monitor";
    
    /// <summary>
    /// All roles that can read data
    /// </summary>
    public const string ReadRoles = $"{Admin},{ReadOnly},{Monitor}";
    
    /// <summary>
    /// All roles that can write/modify data
    /// </summary>
    public const string WriteRoles = Admin;
}