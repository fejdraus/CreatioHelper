using System.Runtime.InteropServices;

namespace CreatioHelper.Infrastructure.Services.FileSystem;

public static class ZoneIdentifierRemover
{
    private const string StreamSuffix = ":Zone.Identifier";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "DeleteFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileNative(string fileName);

    public static int RemoveFrom(string directory)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            if (DeleteFileNative(file + StreamSuffix))
            {
                removed++;
            }
        }

        return removed;
    }
}
