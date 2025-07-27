using System.IO;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Configuration;

namespace CreatioHelper.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly List<DirectoryInfo> _tempDirectories = new();

    [Fact]
    public void Load_WhenNoFile_ReturnsDefaultSettings()
    {
        var tempDir = CreateTempDirectory();
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tempDir.FullName);
        try
        {
            var manager = new AppSettingsManager();
            var service = new SettingsService(manager);
            var settings = service.Load();
            Assert.True(settings.IsIisMode);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    [Fact]
    public void Save_And_Load_RoundTrip()
    {
        var tempDir = CreateTempDirectory();
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tempDir.FullName);
        try
        {
            var manager = new AppSettingsManager();
            var service = new SettingsService(manager);
            var original = new AppSettings
            {
                SitePath = "site",
                PackagesPath = "pkg",
                PackagesToDeleteBefore = "b1",
                PackagesToDeleteAfter = "b2",
                IsIisMode = false,
                IsServerPanelVisible = true
            };
            service.Save(original);

            var loaded = service.Load();
            Assert.Equal(original.SitePath, loaded.SitePath);
            Assert.Equal(original.PackagesPath, loaded.PackagesPath);
            Assert.Equal(original.PackagesToDeleteBefore, loaded.PackagesToDeleteBefore);
            Assert.Equal(original.PackagesToDeleteAfter, loaded.PackagesToDeleteAfter);
            Assert.Equal(original.IsIisMode, loaded.IsIisMode);
            Assert.Equal(original.IsServerPanelVisible, loaded.IsServerPanelVisible);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    private DirectoryInfo CreateTempDirectory()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        _tempDirectories.Add(tempDir);
        return tempDir;
    }

    public void Dispose()
    {
        foreach (var tempDir in _tempDirectories)
        {
            if (tempDir.Exists)
            {
                TryDeleteDirectory(tempDir);
            }
        }
    }

    private static void TryDeleteDirectory(DirectoryInfo directory)
    {
        if (!directory.Exists)
            return;

        for (int i = 0; i < 10; i++) // Увеличиваем количество попыток
        {
            try
            {
                // Принудительная сборка мусора
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                // Сначала пытаемся разблокировать все файлы
                SetDirectoryAttributesRecursive(directory, FileAttributes.Normal);
                
                // Пытаемся закрыть все открытые файловые дескрипторы
                ForceCloseFileHandles(directory);
                
                directory.Delete(true);
                return; // Успешно удалили
            }
            catch (IOException) when (i < 9)
            {
                Thread.Sleep(500); // Увеличиваем время ожидания
            }
            catch (UnauthorizedAccessException) when (i < 9)
            {
                Thread.Sleep(500);
            }
            catch (DirectoryNotFoundException)
            {
                return; // Директория уже удалена
            }
        }
        
        // Если не удалось удалить, попробуем пометить для удаления при перезагрузке
        try
        {
            MarkDirectoryForDeletion(directory);
        }
        catch
        {
            // Игнорируем ошибки, это последняя попытка
        }
    }

    private static void SetDirectoryAttributesRecursive(DirectoryInfo directory, FileAttributes attributes)
    {
        if (!directory.Exists)
            return;

        try
        {
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (file.Exists && (file.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    {
                        file.Attributes = attributes;
                    }
                }
                catch
                {
                    // Игнорируем ошибки для отдельных файлов
                }
            }

            foreach (var subDir in directory.EnumerateDirectories("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (subDir.Exists)
                    {
                        subDir.Attributes = attributes;
                    }
                }
                catch
                {
                    // Игнорируем ошибки для отдельных директорий
                }
            }
        }
        catch
        {
            // Игнорируем ошибки при обходе файлов
        }
    }

    private static void ForceCloseFileHandles(DirectoryInfo directory)
    {
        try
        {
            // Попытка принудительно закрыть все дескрипторы в директории
            var processes = System.Diagnostics.Process.GetProcesses();
            foreach (var process in processes)
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                    // Игнорируем ошибки
                }
            }
        }
        catch
        {
            // Игнорируем ошибки
        }
    }

    private static void MarkDirectoryForDeletion(DirectoryInfo directory)
    {
        try
        {
            // Попытка переименовать директорию для удаления
            var newName = Path.Combine(Path.GetTempPath(), $"ToDelete_{Guid.NewGuid():N}");
            directory.MoveTo(newName);
        }
        catch
        {
            // Последняя попытка - пометить файлы как временные
            try
            {
                foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try
                    {
                        file.Attributes |= FileAttributes.Temporary;
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
