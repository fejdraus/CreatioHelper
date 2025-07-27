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
        string directPath = Path.Combine(sitePath, "Terrasoft.Common.dll");
        if (File.Exists(directPath))
        {
            return directPath;
        }

        if (OperatingSystem.IsWindows())
        {
            string nestedPath = Path.Combine(sitePath, "Terrasoft.WebApp", "bin", "Terrasoft.Common.dll");
            if (File.Exists(nestedPath))
            {
                return nestedPath;
            }
        }

        return null;
    }

}
