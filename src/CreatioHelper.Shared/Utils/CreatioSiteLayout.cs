namespace CreatioHelper.Shared.Utils;

/// <summary>
/// Resolves the on-disk layout of a Creatio installation.
/// The .NET Framework edition keeps the application under Terrasoft.WebApp and is configured
/// through Web.config; the .NET Core edition has no nested application and uses
/// Terrasoft.WebHost.dll.config.
/// </summary>
public static class CreatioSiteLayout
{
    public const string CoreConfigFileName = "Terrasoft.WebHost.dll.config";
    public const string FrameworkConfigFileName = "Web.config";
    public const string WebAppDirectoryName = "Terrasoft.WebApp";

    public const string ApplicationDllName = "Terrasoft.Common.dll";

    /// <summary>
    /// Markers are checked from the most to the least reliable: only the .NET Core edition ships
    /// Terrasoft.WebHost.dll.config, only the .NET Framework edition keeps the application assembly
    /// under Terrasoft.WebApp/bin, and only the .NET Core edition keeps it in the site root.
    /// The Terrasoft.WebApp folder alone is the weakest marker, since it can survive an upgrade.
    /// </summary>
    public static bool IsDotNetFramework(string sitePath)
    {
        if (File.Exists(Path.Combine(sitePath, CoreConfigFileName)))
        {
            return false;
        }
        if (File.Exists(Path.Combine(sitePath, WebAppDirectoryName, "bin", ApplicationDllName)))
        {
            return true;
        }
        if (File.Exists(Path.Combine(sitePath, ApplicationDllName)))
        {
            return false;
        }
        return Directory.Exists(Path.Combine(sitePath, WebAppDirectoryName));
    }

    /// <summary>
    /// Path to Terrasoft.Common.dll, which carries the Creatio version.
    /// </summary>
    public static string GetApplicationDllPath(string sitePath) => IsDotNetFramework(sitePath)
        ? Path.Combine(sitePath, WebAppDirectoryName, "bin", ApplicationDllName)
        : Path.Combine(sitePath, ApplicationDllName);

    public static bool IsDotNetCore(string sitePath) => !IsDotNetFramework(sitePath);

    public static string GetRootConfigPath(string sitePath) => IsDotNetFramework(sitePath)
        ? Path.Combine(sitePath, FrameworkConfigFileName)
        : Path.Combine(sitePath, CoreConfigFileName);

    public static string GetWebAppPath(string sitePath) => IsDotNetFramework(sitePath)
        ? Path.Combine(sitePath, WebAppDirectoryName)
        : sitePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string GetConfigurationPath(string sitePath) =>
        Path.Combine(GetWebAppPath(sitePath), "Terrasoft.Configuration");

    /// <summary>
    /// Returns the Terrasoft configuration file of this edition when it exists on disk.
    /// The editions are never mixed: the .NET Core edition also ships a Web.config, but that one
    /// only configures the ASP.NET Core module and holds no Terrasoft settings.
    /// </summary>
    public static string? FindExistingRootConfigPath(string sitePath)
    {
        var configPath = GetRootConfigPath(sitePath);
        return File.Exists(configPath) ? configPath : null;
    }
}
