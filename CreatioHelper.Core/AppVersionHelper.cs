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
        var appDll = Path.Combine(appPath, "bin", "Terrasoft.Common.dll");
        if (!File.Exists(appDll)) return new Version();
        var assemblyName = AssemblyName.GetAssemblyName(appDll);
        return assemblyName.Version ?? new Version();
    }
}
