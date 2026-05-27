using System;

namespace CreatioHelper.Converters;

public static class PlatformInfo
{
    public static bool IsWindows { get; } = OperatingSystem.IsWindows();
}
