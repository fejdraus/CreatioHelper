using System.Security.Cryptography.X509Certificates;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Sync;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace CreatioHelper.Infrastructure.Extensions;

/// <summary>
/// Configuration class for loading sync settings from appsettings.json
/// </summary>
public class SyncConfigurationFromFile
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int Port { get; set; } = 22000;
    public int DiscoveryPort { get; set; } = 21027;
    public List<SyncFolderConfig> Folders { get; set; } = new();

    public class SyncFolderConfig
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = "SendReceive";
        public string EncryptionPassword { get; set; } = string.Empty;
        public List<SyncDeviceConfig> Devices { get; set; } = new();

        public FolderType GetFolderType()
        {
            return Type switch
            {
                "SendOnly" => FolderType.SendOnly,
                "ReceiveOnly" => FolderType.ReceiveOnly,
                _ => FolderType.SendReceive
            };
        }
    }

    public class SyncDeviceConfig
    {
        public string DeviceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string CertificateFingerprint { get; set; } = string.Empty;
        public List<string> Addresses { get; set; } = new();
    }
}

/// <summary>
/// Service registration extensions for sync services
/// </summary>
public static class SyncServiceExtensions
{
    public static IServiceCollection AddSyncServices(this IServiceCollection services, SyncConfiguration? config = null)
    {
        // Generate or load device certificate
        var certificate = GenerateDeviceCertificate();
        
        // Use provided config or create default
        var syncConfig = config ?? new SyncConfiguration(
            GenerateDeviceId(certificate),
            Environment.MachineName);
        
        var port = syncConfig.Port;
        Console.WriteLine($"DEBUG: Using sync port={port}, deviceName={syncConfig.DeviceName}");
        
        syncConfig.SetListenAddresses(new List<string> { $"tcp://0.0.0.0:{port}" });

        services.AddSingleton(syncConfig);
        services.AddSingleton(certificate);

        // Register core sync services - using new BEP protocol implementation
        // Register compression and encryption engines
        services.AddSingleton<CompressionEngine>();
        services.AddSingleton<EncryptionEngine>();
        services.AddSingleton<KeyManager>(provider => 
            new KeyManager(
                provider.GetRequiredService<ILogger<KeyManager>>(), 
                syncConfig.DeviceId));
        
        services.AddSingleton<ISyncProtocol>(provider =>
            new BepProtocol(
                provider.GetRequiredService<ILogger<BepProtocol>>(),
                provider.GetRequiredService<CompressionEngine>(),
                provider.GetRequiredService<EncryptionEngine>(),
                provider.GetRequiredService<KeyManager>(),
                port,
                certificate,
                syncConfig.DeviceId));

        // Register Phase 3 services: Global Discovery + Relay
        services.AddHttpClient<GlobalDiscoveryService>();
        services.AddSingleton<IDeviceDiscovery, GlobalDiscoveryService>();
        services.AddSingleton<RelayService>();
        services.AddSingleton<AdaptiveBlockSizer>();
        services.AddSingleton<DeltaSyncEngine>();
        services.AddSingleton<FileWatcher>();
        services.AddSingleton<ConflictResolver>();
        services.AddSingleton<FileComparator>();
        services.AddSingleton<FileDownloader>();
        services.AddSingleton<BlockRequestHandler>();

        services.AddSingleton<ISyncEngine>(provider =>
            new SyncEngine(
                provider.GetRequiredService<ILogger<SyncEngine>>(),
                provider.GetRequiredService<ISyncProtocol>(),
                provider.GetRequiredService<IDeviceDiscovery>(),
                provider.GetRequiredService<FileWatcher>(),
                provider.GetRequiredService<ConflictResolver>(),
                provider.GetRequiredService<FileComparator>(),
                provider.GetRequiredService<FileDownloader>(),
                provider.GetRequiredService<BlockRequestHandler>(),
                provider.GetRequiredService<DeltaSyncEngine>(),
                syncConfig));

        // Register event broadcaster
        services.AddSingleton<SyncEventBroadcaster>();

        // Register sync engine as hosted service
        services.AddHostedService<SyncEngineHostedService>();

        return services;
    }

    private static X509Certificate2 GenerateDeviceCertificate()
    {
        // In a real implementation, this should load from storage or generate a new one
        // For now, create a simple self-signed certificate
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={Environment.MachineName}"),
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        return certificate;
    }

    private static string GenerateDeviceId(X509Certificate2 certificate)
    {
        // Syncthing uses SHA-256 hash of the certificate as device ID
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(certificate.RawData);
        return Convert.ToHexString(hash).ToLower();
    }
}


/// <summary>
/// Hosted service wrapper for sync engine
/// </summary>
public class SyncEngineHostedService : BackgroundService
{
    private readonly ISyncEngine _syncEngine;
    private readonly SyncEventBroadcaster _eventBroadcaster;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SyncEngineHostedService> _logger;

    public SyncEngineHostedService(
        ISyncEngine syncEngine,
        SyncEventBroadcaster eventBroadcaster,
        IConfiguration configuration,
        ILogger<SyncEngineHostedService> logger)
    {
        _syncEngine = syncEngine;
        _eventBroadcaster = eventBroadcaster;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Subscribe to sync events for broadcasting
            _syncEngine.FolderSynced += async (sender, e) =>
            {
                await _eventBroadcaster.BroadcastFolderSyncedAsync(
                    e.FolderId, e.Summary.FilesTransferred, e.Summary.BytesTransferred);
            };

            _syncEngine.ConflictDetected += async (sender, e) =>
            {
                await _eventBroadcaster.BroadcastConflictDetectedAsync(e.FolderId, e.FilePath);
            };

            _syncEngine.SyncError += async (sender, e) =>
            {
                await _eventBroadcaster.BroadcastSyncErrorAsync(e.FolderId, e.Error, e.DeviceId);
            };

            // Start the sync engine
            await _syncEngine.StartAsync(stoppingToken);
            _logger.LogInformation("Sync engine hosted service started");

            // Initialize devices and folders from configuration
            await InitializeFromConfigurationAsync();

            // Keep running until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Sync engine hosted service is stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in sync engine hosted service");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping sync engine hosted service");
        await _syncEngine.StopAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task InitializeFromConfigurationAsync()
    {
        try
        {
            _logger.LogInformation("Initializing sync engine from configuration");

            // Load sync configuration from appsettings
            var syncConfig = _configuration.GetSection("Sync").Get<SyncConfigurationFromFile>();
            if (syncConfig == null || syncConfig.Folders.Count == 0)
            {
                _logger.LogWarning("No sync folders found in configuration");
                return;
            }

            // Add all folders from configuration
            foreach (var folderConfig in syncConfig.Folders)
            {
                _logger.LogInformation("Adding folder {FolderId} at {Path}", folderConfig.Id, folderConfig.Path);
                await _syncEngine.AddFolderAsync(folderConfig.Id, folderConfig.Label, folderConfig.Path, folderConfig.GetFolderType());

                // Add devices for this folder
                foreach (var deviceConfig in folderConfig.Devices)
                {
                    _logger.LogInformation("Adding device {DeviceId} with addresses {Addresses}", 
                        deviceConfig.DeviceId, string.Join(", ", deviceConfig.Addresses));
                    
                    await _syncEngine.AddDeviceAsync(deviceConfig.DeviceId, deviceConfig.Name, 
                        deviceConfig.CertificateFingerprint, deviceConfig.Addresses);
                    
                    await _syncEngine.ShareFolderWithDeviceAsync(folderConfig.Id, deviceConfig.DeviceId);
                }
            }

            _logger.LogInformation("Sync engine initialization completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing sync engine from configuration");
            throw;
        }
    }
}