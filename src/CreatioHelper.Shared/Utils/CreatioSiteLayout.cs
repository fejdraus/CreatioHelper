namespace CreatioHelper.Shared.Utils;

public static class CreatioSiteLayout
{
    public const string CoreConfigFileName = "Terrasoft.WebHost.dll.config";
    public const string FrameworkConfigFileName = "Web.config";
    public const string WebAppDirectoryName = "Terrasoft.WebApp";
    public const string ApplicationDllName = "Terrasoft.Common.dll";
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
        public static string? FindExistingRootConfigPath(string sitePath)
    {
        var configPath = GetRootConfigPath(sitePath);
        return File.Exists(configPath) ? configPath : null;
    }
}