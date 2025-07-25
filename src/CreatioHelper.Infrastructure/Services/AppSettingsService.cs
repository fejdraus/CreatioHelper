using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services
{
    public static class AppSettingsService
    {
        private const string SettingsFile = "settings.json";
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static AppSettings Load()
        {
            if (!File.Exists(SettingsFile))
                return new AppSettings();

            try
            {
                string json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch (Exception)
            {
                // Exception logging can be added here
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(SettingsFile, json);
            }
            catch (Exception)
            {
                // Exception logging can be added here
            }
        }

        public static bool SettingsFileExists() => File.Exists(SettingsFile);
    }
}