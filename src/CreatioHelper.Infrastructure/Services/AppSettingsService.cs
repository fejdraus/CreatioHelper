using System.Text.Encodings.Web;
using System.Text.Json;

using CreatioHelper.Domain.Entities;
using Mapster;

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

        public static async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (!File.Exists(SettingsFile))
                return new AppSettings();

            try
            {
                string json = await File.ReadAllTextAsync(SettingsFile, cancellationToken).ConfigureAwait(false);
                var dtoAppSettings = JsonSerializer.Deserialize<DtoAppSettings>(json) ?? new DtoAppSettings();
                return dtoAppSettings.Adapt<AppSettings>();
            }
            catch (Exception)
            {
                // Exception logging can be added here
                return new AppSettings();
            }
        }

        public static AppSettings Load()
        {
            if (!File.Exists(SettingsFile))
                return new AppSettings();

            try
            {
                string json = File.ReadAllText(SettingsFile);
                var dtoAppSettings = JsonSerializer.Deserialize<DtoAppSettings>(json) ?? new DtoAppSettings();
                return dtoAppSettings.Adapt<AppSettings>();
            }
            catch (Exception)
            {
                // Exception logging can be added here
                return new AppSettings();
            }
        }

        public static async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            try
            {
                var newSettings = settings.Adapt<DtoAppSettings>();
                string json = JsonSerializer.Serialize(newSettings, JsonOptions);
                await File.WriteAllTextAsync(SettingsFile, json, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Exception logging can be added here
            }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                var newSettings = settings.Adapt<DtoAppSettings>();
                string json = JsonSerializer.Serialize(newSettings, JsonOptions);
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