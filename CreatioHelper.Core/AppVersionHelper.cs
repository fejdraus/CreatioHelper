using System;
using System.IO;
using System.Reflection;

namespace CreatioHelper.Core;

/// <summary>
/// Provides helper methods for reading the application version
/// from a Creatio installation.
/// </summary>
public static class AppVersionHelper
{
    public static Version GetAppVersion(string appPath)
    {
        if (string.IsNullOrWhiteSpace(appPath))
        {
            throw new ArgumentNullException(nameof(appPath));
        }
        string dllPath = Path.Combine(appPath, "Terrasoft.Common.dll");
        bool isFramework = Directory.Exists(Path.Combine(appPath, "Terrasoft.WebApp"));
        if (isFramework && OperatingSystem.IsWindows())
        {
            dllPath = isFramework
                ? Path.Combine(appPath, "Terrasoft.WebApp" ,"bin", "Terrasoft.Common.dll")
                : Path.Combine(appPath, "Terrasoft.Common.dll");
        }

        if (!File.Exists(dllPath))
        {
            return new Version();
        }

        var assemblyName = AssemblyName.GetAssemblyName(dllPath);
        return assemblyName.Version ?? new Version();
    }
}
