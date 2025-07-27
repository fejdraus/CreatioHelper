using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services;

public class CachedSettingsService : ISettingsService, IDisposable
{
    private readonly ISettingsService _innerService;
    private readonly ICacheService _cache;
    private readonly IMetricsService _metrics;
    private readonly ILogger<CachedSettingsService> _logger;
    private readonly FileSystemWatcher _fileWatcher;
    
    private const string SettingsCacheKey = "app_settings";
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(30);

    public CachedSettingsService(
        ISettingsService innerService,
        ICacheService cache,
        IMetricsService metrics,
        ILogger<CachedSettingsService> logger)
    {
        _innerService = innerService ?? throw new ArgumentNullException(nameof(innerService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fileWatcher = new FileSystemWatcher(Directory.GetCurrentDirectory(), "settings.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        
        _fileWatcher.Changed += OnSettingsFileChanged;
        _fileWatcher.Created += OnSettingsFileChanged;
        _fileWatcher.Deleted += OnSettingsFileChanged;
    }

    public AppSettings Load()
    {
        return _metrics.MeasureAsync("settings_load", async () =>
        {
            var cached = await _cache.GetAsync<AppSettings>(SettingsCacheKey);
            if (cached != null)
            {
                _metrics.IncrementCounter("settings_cache_hit");
                _logger.LogDebug("Settings loaded from cache");
                return cached;
            }
            var settings = _innerService.Load();
            await _cache.SetAsync(SettingsCacheKey, settings, CacheExpiration);
            
            _metrics.IncrementCounter("settings_cache_miss");
            _logger.LogDebug("Settings loaded from file and cached");
            
            return settings;
        }).GetAwaiter().GetResult();
    }

    public void Save(AppSettings settings)
    {
        _metrics.MeasureAsync("settings_save", async () =>
        {
            _innerService.Save(settings);
            await _cache.SetAsync(SettingsCacheKey, settings, CacheExpiration);
            _metrics.IncrementCounter("settings_saved");
            _logger.LogDebug("Settings saved and cache updated");
            
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
    }

    private async void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            await Task.Delay(100);
            
            await _cache.RemoveAsync(SettingsCacheKey);
            _metrics.IncrementCounter("settings_cache_invalidated");
            _logger.LogInformation("Settings cache invalidated due to file change: {ChangeType}", e.ChangeType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling settings file change");
        }
    }

    public void Dispose()
    {
        _fileWatcher.Dispose();
        if (_innerService is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
