using System.Reflection;

namespace CreatioHelper.Shared.Utils;

/// <summary>
/// Provides helper methods for reading the application version
/// from a Creatio installation.
/// </summary>
public static class AppVersionHelper
{
    public static Version GetAppVersion(string sitePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sitePath);

        string? dllPath = GetTerrasoftDllPath(sitePath);

        if (dllPath is null || !File.Exists(dllPath))
        {
            return new Version();
        }

        try
        {
            var assemblyName = AssemblyName.GetAssemblyName(dllPath);
            return assemblyName.Version ?? new Version();
        }
        catch
        {
            return new Version();
        }
    }

    private static string? GetTerrasoftDllPath(string sitePath)
    {
        var dllPath = CreatioSiteLayout.GetApplicationDllPath(sitePath);
        return File.Exists(dllPath) ? dllPath : null;
    }

}
