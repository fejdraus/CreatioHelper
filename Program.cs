using System;
using System.IO;
using System.Threading.Tasks;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Versioning;
using Microsoft.Extensions.Logging;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Тест системы версионирования CreatioHelper ===");
        
        var loggerFactory = LoggerFactory.Create(builder => 
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        var logger = loggerFactory.CreateLogger<SimpleVersioner>();
        
        var folderPath = @"C:\VersionTest1";
        var config = new VersioningConfiguration
        {
            Type = "simple",
            Params = new Dictionary<string, string>
            {
                ["keep"] = "5",
                ["cleanoutDays"] = "0"
            },
            CleanupIntervalS = 30,
            FSPath = ".stversions",
            FSType = "basic"
        };
        
        Console.WriteLine($"Создаем Simple Versioner для папки: {folderPath}");
        var versioner = new SimpleVersioner(logger, folderPath, config);
        
        try
        {
            // Тест 1: Создание версий существующих файлов
            Console.WriteLine("\n--- Тест 1: Создание версий ---");
            
            var testFiles = new[]
            {
                "test_file.txt",
                "data.json", 
                "document.txt"
            };
            
            foreach (var file in testFiles)
            {
                var filePath = Path.Combine(folderPath, file);
                if (File.Exists(filePath))
                {
                    Console.WriteLine($"Создаем версию для файла: {file}");
                    await versioner.ArchiveAsync(file);
                    Console.WriteLine($"✅ Версия создана для: {file}");
                }
                else
                {
                    Console.WriteLine($"❌ Файл не найден: {file}");
                }
            }
            
            // Тест 2: Проверка созданных версий
            Console.WriteLine("\n--- Тест 2: Проверка созданных версий ---");
            var versions = await versioner.GetVersionsAsync();
            
            Console.WriteLine($"Найдено файлов с версиями: {versions.Count}");
            foreach (var kvp in versions)
            {
                Console.WriteLine($"📄 {kvp.Key}: {kvp.Value.Count} версий");
                foreach (var version in kvp.Value)
                {
                    Console.WriteLine($"   🔸 {version.VersionTime:yyyy-MM-dd HH:mm:ss} - {version.Size} байт - {Path.GetFileName(version.VersionPath)}");
                }
            }
            
            // Тест 3: Создание дополнительных версий
            Console.WriteLine("\n--- Тест 3: Создание дополнительных версий ---");
            
            // Изменяем файл и создаем еще версии
            var testFile = Path.Combine(folderPath, "test_file.txt");
            for (int i = 4; i <= 7; i++)
            {
                File.WriteAllText(testFile, $"Версия {i}: Тестовое содержимое файла\nВремя: {DateTime.Now}");
                await Task.Delay(1100); // Пауза для разных временных меток
                await versioner.ArchiveAsync("test_file.txt");
                Console.WriteLine($"✅ Создана версия {i}");
            }
            
            // Тест 4: Проверка ограничения количества версий
            Console.WriteLine("\n--- Тест 4: Проверка ограничения версий (keep=5) ---");
            versions = await versioner.GetVersionsAsync();
            
            if (versions.ContainsKey("test_file.txt"))
            {
                var fileVersions = versions["test_file.txt"];
                Console.WriteLine($"📄 test_file.txt: {fileVersions.Count} версий (должно быть не больше 5)");
                
                if (fileVersions.Count <= 5)
                {
                    Console.WriteLine("✅ Ограничение версий работает корректно");
                }
                else
                {
                    Console.WriteLine($"❌ Ограничение версий не работает: {fileVersions.Count} > 5");
                }
            }
            
            // Тест 5: Проверка директории версий
            Console.WriteLine("\n--- Тест 5: Проверка директории версий ---");
            var versionsPath = versioner.VersionsPath;
            Console.WriteLine($"Путь к версиям: {versionsPath}");
            
            if (Directory.Exists(versionsPath))
            {
                Console.WriteLine("✅ Директория версий создана");
                
                var versionFiles = Directory.GetFiles(versionsPath, "*", SearchOption.AllDirectories);
                Console.WriteLine($"📁 Найдено файлов версий: {versionFiles.Length}");
                
                long totalSize = 0;
                foreach (var vFile in versionFiles)
                {
                    var size = new FileInfo(vFile).Length;
                    totalSize += size;
                    var relativePath = Path.GetRelativePath(versionsPath, vFile);
                    Console.WriteLine($"   🔸 {relativePath} ({size} байт)");
                }
                
                Console.WriteLine($"💾 Общий размер versions: {totalSize} байт");
            }
            else
            {
                Console.WriteLine("❌ Директория версий не найдена");
            }
            
            // Тест 6: Очистка версий
            Console.WriteLine("\n--- Тест 6: Очистка версий ---");
            await versioner.CleanAsync();
            Console.WriteLine("✅ Очистка версий выполнена");
            
            // Финальная проверка
            versions = await versioner.GetVersionsAsync();
            Console.WriteLine($"После очистки осталось файлов с версиями: {versions.Count}");
            
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Ошибка: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        finally
        {
            versioner?.Dispose();
        }
        
        Console.WriteLine("\n=== Тест завершен ===");
    }
}