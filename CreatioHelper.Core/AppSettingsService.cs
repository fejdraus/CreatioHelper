using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CreatioHelper.Core
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
                // Логирование исключения может быть добавлено здесь
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
                // Логирование исключения может быть добавлено здесь
            }
        }

        public static bool SettingsFileExists() => File.Exists(SettingsFile);
    }
}