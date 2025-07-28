using System;
using System.IO;
using CreatioHelper.Services;

namespace CreatioHelper.Tests;

public class FileLogServiceTests
{
    [Fact]
    public void AppendLine_Writes_WhenEnabled()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            FileLogService.LogFilePath = path;
            FileLogService.Enabled = true;
            FileLogService.Clear();
            FileLogService.AppendLine("test");

            var content = File.ReadAllText(path);
            Assert.Contains("test", content);
        }
        finally
        {
            FileLogService.Enabled = false;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void AppendLine_DoesNotWrite_WhenDisabled()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            FileLogService.LogFilePath = path;
            FileLogService.Enabled = false;
            FileLogService.AppendLine("test");

            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
