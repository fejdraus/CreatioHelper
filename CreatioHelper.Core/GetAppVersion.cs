using System;
using System.IO;
using System.Reflection;

namespace CreatioHelper.Core;

public static class GetAppAssembly
{
    public static Version GetAppVersion(string appPath)
    {
        var appDll = Path.Combine(appPath, "bin", "Terrasoft.Common.dll");
        if (!File.Exists(appDll)) return new Version();
        var assemblyName = AssemblyName.GetAssemblyName(appDll);
        return assemblyName.Version ?? new Version();
    }
}