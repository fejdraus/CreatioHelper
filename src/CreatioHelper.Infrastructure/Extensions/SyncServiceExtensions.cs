using System.Security.Cryptography.X509Certificates;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Sync;
using CreatioHelper.Infrastructure.Services.Sync.Configuration;
using CreatioHelper.Infrastructure.Services.Sync.Database;
using CreatioHelper.Infrastructure.Services.Sync.Handlers;
using CreatioHelper.Infrastructure.Services.Sync.Encryption;
using CreatioHelper.Infrastructure.Services.Network;
using CreatioHelper.Infrastructure.Services.Network.UPnP;
using CreatioHelper.Infrastructure.Services.Security;
using CreatioHelper.Infrastructure.Services.Events;
using CreatioHelper.Infrastructure.Services.Statistics;
using CreatioHelper.Infrastructure.Services.Sync.Events;
using CreatioHelper.Infrastructure.Services.Sync.Diagnostics;
using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using CreatioHelper.Infrastructure.Services.Sync.Security;
using CreatioHelper.Infrastructure.Services.Sync.Transfer;
using ScanningNs = CreatioHelper.Infrastructure.Services.Sync.Scanning;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using ISyncConfigManager = CreatioHelper.Application.Interfaces.IConfigurationManager;

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
        public List<SyncDeviceConfig> Devices { get; set; } = new();

        public string GetFolderType()
        {
            return Type.ToLower() switch
            {
                "sendonly" => "sendonly",
                "receiveonly" => "receiveonly", 
                "receiveencrypted" => "receiveencrypted",
                _ => "sendreceive"
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
        
        // Register Database Layer - SQLite implementation
        services.AddSingleton<ISyncDatabase>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<SqliteSyncDatabase>>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            var databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                "CreatioHelper", "Sync", $"sync_{syncConfig.DeviceId}.db");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            return new SqliteSyncDatabase(logger, loggerFactory, databasePath);
        });

        // Register Block Info Repository
        services.AddSingleton<IBlockInfoRepository>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<SqliteBlockInfoRepository>>();
            var databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                "CreatioHelper", "Sync", $"sync_{syncConfig.DeviceId}.db");
            var connectionString = $"Data Source={databasePath}";
            return new SqliteBlockInfoRepository(logger, connectionString);
        });

        // Register core sync services - using new BEP protocol implementation
        services.AddSingleton<ISyncProtocol>(provider =>
            new BepProtocol(
                provider.GetRequiredService<ILogger<BepProtocol>>(),
                provider.GetRequiredService<ISyncDatabase>(),
                provider.GetRequiredService<BlockDuplicationDetector>(),
                port,
                certificate,
                syncConfig.DeviceId));

        services.AddSingleton<IDeviceDiscovery>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<DeviceDiscovery>>();
            var discoveryPort = syncConfig.DiscoveryPort;
            return new DeviceDiscovery(logger, discoveryPort);
        });
        
        // Syncthing 100% compatible Global Discovery
        services.AddSingleton<SyncthingGlobalDiscovery>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<SyncthingGlobalDiscovery>>();
            return new SyncthingGlobalDiscovery(logger, certificate, syncConfig.DeviceId);
        });
        
        // Syncthing 100% compatible File Versioning
        services.AddSingleton<Func<string, SyncthingFileVersioner>>(provider => folderPath =>
        {
            var logger = provider.GetRequiredService<ILogger<SyncthingFileVersioner>>();
            return new SyncthingFileVersioner(logger, folderPath, keepVersions: 5, cleanoutDays: 0);
        });
        services.AddSingleton<AdaptiveBlockSizer>();
        services.AddSingleton<DeltaSyncEngine>();
        services.AddSingleton<FileWatcher>();
        services.AddSingleton<ConflictResolver>();
        services.AddSingleton<FileComparator>();
        services.AddSingleton<FileDownloader>();
        services.AddSingleton<FileUploader>();
        
        // Syncthing-compatible block services
        services.AddSingleton<SyncthingBlockCalculator>();
        services.AddSingleton<SyncthingBlockStorage>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<SyncthingBlockStorage>>();
            var blockRepository = provider.GetRequiredService<IBlockInfoRepository>();
            var blockStoragePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                "CreatioHelper", "blocks");
            return new SyncthingBlockStorage(logger, blockRepository, blockStoragePath);
        });
        
        services.AddSingleton<BlockRequestHandler>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<BlockRequestHandler>>();
            var protocol = provider.GetRequiredService<ISyncProtocol>();
            var blockStorage = provider.GetRequiredService<SyncthingBlockStorage>();
            var blockRepository = provider.GetRequiredService<IBlockInfoRepository>();
            return new BlockRequestHandler(logger, protocol, blockStorage, blockRepository);
        });
        
        // Folder Type Handlers (PHASE 11) - Register all folder handlers
        services.AddSingleton<ConflictResolutionEngine>();
        services.AddSingleton<SendReceiveFolderHandler>();
        services.AddSingleton<SendOnlyFolderHandler>();
        services.AddSingleton<ReceiveOnlyFolderHandler>();
        services.AddSingleton<ReceiveEncryptedFolderHandler>();
        services.AddSingleton<MasterFolderHandler>();
        services.AddSingleton<SlaveFolderHandler>();
        services.AddSingleton<SyncFolderHandlerFactory>();
        
        // Block-level deduplication services - updated to use Syncthing approach
        services.AddSingleton<BlockDuplicationDetector>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<BlockDuplicationDetector>>();
            var blockRepository = provider.GetRequiredService<IBlockInfoRepository>();
            var blockCalculator = provider.GetRequiredService<SyncthingBlockCalculator>();
            return new BlockDuplicationDetector(logger, blockRepository, blockCalculator);
        });
        services.AddSingleton<ParallelBlockTransfer>();
        services.AddSingleton<TransferOptimizer>();

        // Bandwidth Management Services - Syncthing Compatible
        services.AddSingleton<IBandwidthManager>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<SyncthingBandwidthManager>>();
            var manager = new SyncthingBandwidthManager(logger, syncConfig.DeviceId);
            
            // Configure with Syncthing-compatible bandwidth settings
            if (syncConfig.BandwidthSettings != null)
            {
                manager.UpdateConfiguration(syncConfig.BandwidthSettings);
            }
            
            return manager;
        });

        services.AddSingleton<IPriorityManager>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<PriorityManager>>();
            return new PriorityManager(logger, syncConfig.TrafficShaping);
        });

        services.AddSingleton<BandwidthAwareBepConnectionFactory>();

        // Event and Statistics Services (ФАЗА 13)
        services.AddSingleton<IEventLogger, EventLogger>();
        services.AddSingleton<IStatisticsCollector, StatisticsCollector>();
        services.AddHostedService<EventLogger>(provider => (EventLogger)provider.GetRequiredService<IEventLogger>());
        services.AddHostedService<StatisticsCollector>(provider => (StatisticsCollector)provider.GetRequiredService<IStatisticsCollector>());

        // Encryption Services (100% Syncthing compatibility)
        services.AddSingleton<EncryptionKeyGenerator>();
        services.AddSingleton<SyncthingEncryption>();
        services.AddSingleton<IEncryptionService, SimplifiedEncryptionService>();

        // Security Services (ФАЗА 14)
        services.AddSingleton<SecurityConfiguration>(provider =>
        {
            var config = provider.GetService<IConfiguration>();
            var securityConfig = config?.GetSection("Security").Get<SecurityConfiguration>() ?? new SecurityConfiguration();
            return securityConfig;
        });
        services.AddSingleton<ICertificateManager, CertificateManager>();
        services.AddSingleton<SyncthingTlsManager>();
        services.AddSingleton<ISecurityAuditor, SecurityAuditor>();
        services.AddHostedService<SecurityAuditor>(provider => (SecurityAuditor)provider.GetRequiredService<ISecurityAuditor>());

        // UPnP/NAT Traversal Services (ФАЗА 9)
        services.AddSingleton<IUPnPService, SyncthingUPnPService>();

        // Performance Optimization Services (ФАЗА 15) - Syncthing-compatible connection pooling
        // No need to register SyncthingSemaphore separately as it's created internally
        
        services.AddSingleton<SyncthingConnectionPool>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<SyncthingConnectionPool>>();
            return new SyncthingConnectionPool(logger);
        });
        
        services.AddSingleton<IBepConnection>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<BepConnectionAdapter>>();
            var connectionPool = provider.GetRequiredService<SyncthingConnectionPool>();
            var bandwidthManager = provider.GetRequiredService<IBandwidthManager>();
            return new BepConnectionAdapter(logger, connectionPool, bandwidthManager);
        });

        // Scan Progress Service (tracks folder scanning progress) - registered before SyncEngine
        services.AddSingleton<ScanningNs.IScanProgressService>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<ScanningNs.ScanProgressService>>();
            return new ScanningNs.ScanProgressService(logger, progressIntervalSeconds: 1);
        });

        services.AddSingleton<ISyncEngine>(provider =>
            new SyncEngine(
                provider.GetRequiredService<ILogger<SyncEngine>>(),
                provider.GetRequiredService<ISyncProtocol>(),
                provider.GetRequiredService<IDeviceDiscovery>(),
                provider.GetRequiredService<FileWatcher>(),
                provider.GetRequiredService<ConflictResolver>(),
                provider.GetRequiredService<FileComparator>(),
                provider.GetRequiredService<FileDownloader>(),
                provider.GetRequiredService<FileUploader>(),
                provider.GetRequiredService<BlockRequestHandler>(),
                provider.GetRequiredService<DeltaSyncEngine>(),
                provider.GetRequiredService<BlockDuplicationDetector>(),
                provider.GetRequiredService<TransferOptimizer>(),
                syncConfig,
                provider.GetRequiredService<ISyncDatabase>(),
                provider.GetRequiredService<ISyncConfigManager>(),
                provider.GetRequiredService<IEventLogger>(),
                provider.GetRequiredService<IStatisticsCollector>(),
                provider.GetRequiredService<ICertificateManager>(),
                provider.GetRequiredService<SyncFolderHandlerFactory>(),
                provider.GetRequiredService<SyncthingGlobalDiscovery>(),
                provider.GetService<ICombinedNatService>(),
                certificate,
                provider.GetService<ScanningNs.IScanProgressService>()));

        // Configuration XML Service (Syncthing-compatible config.xml)
        services.AddSingleton<IConfigXmlService>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<ConfigXmlService>>();
            return new ConfigXmlService(logger);
        });

        // Register event broadcaster
        services.AddSingleton<SyncEventBroadcaster>();

        // Event Subscription Service (REST API compatible)
        services.AddSingleton<SyncEventQueue>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<SyncEventQueue>>();
            var eventLogRepository = provider.GetService<IEventLogRepository>();
            return new SyncEventQueue(logger, eventLogRepository: eventLogRepository);
        });
        services.AddSingleton<IEventSubscriptionService, EventSubscriptionService>();

        // Register sync engine as hosted service
        services.AddHostedService<SyncEngineHostedService>();

        // Database Maintenance Service
        services.AddSingleton<IDatabaseMaintenanceService>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<DatabaseMaintenanceService>>();
            var databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CreatioHelper", "Sync", $"sync_{syncConfig.DeviceId}.db");
            var connectionString = $"Data Source={databasePath};Cache=Shared;Foreign Keys=True;";
            return new DatabaseMaintenanceService(logger, connectionString);
        });
        services.AddHostedService<DatabaseMaintenanceService>(provider =>
            (DatabaseMaintenanceService)provider.GetRequiredService<IDatabaseMaintenanceService>());

        // ============================================
        // Syncthing-compatible extended services
        // ============================================

        // Diagnostics Services
        services.AddSingleton<IDebugFacilities, DebugFacilities>();

        services.AddSingleton<UsageReportingOptions>(provider =>
        {
            var config = provider.GetService<IConfiguration>();
            var options = config?.GetSection("UsageReporting").Get<UsageReportingOptions>() ?? new UsageReportingOptions();
            return options;
        });
        services.AddSingleton<IUsageReportingService>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<UsageReportingService>>();
            var httpClient = new HttpClient();
            var options = provider.GetRequiredService<UsageReportingOptions>();
            return new UsageReportingService(logger, httpClient, options);
        });

        services.AddSingleton<UpgradeOptions>(provider =>
        {
            var config = provider.GetService<IConfiguration>();
            var options = config?.GetSection("Upgrade").Get<UpgradeOptions>() ?? new UpgradeOptions();
            return options;
        });
        services.AddSingleton<IUpgradeService>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<UpgradeService>>();
            var httpClient = new HttpClient();
            var options = provider.GetRequiredService<UpgradeOptions>();
            return new UpgradeService(logger, httpClient, options);
        });

        // FileSystem Services - Factories for folder-specific instances
        services.AddSingleton<CaseSensitiveFileSystemFactory>();
        services.AddSingleton<ExtendedAttributeProviderFactory>();
        services.AddSingleton<OwnershipProviderFactory>();

        services.AddSingleton<IFolderMarkerService, FolderMarkerService>();

        services.AddSingleton<IFsyncController>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<FsyncController>>();
            var config = provider.GetService<IConfiguration>();
            var fsyncEnabled = config?.GetValue<bool>("Sync:FsyncEnabled") ?? true;
            return new FsyncController(logger, fsyncEnabled);
        });

        services.AddSingleton<ICopyRangeOptimizer>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<CopyRangeOptimizer>>();
            var config = provider.GetService<IConfiguration>();
            var methodStr = config?.GetValue<string>("Sync:CopyRangeMethod") ?? "Auto";
            var method = Enum.TryParse<CopyRangeMethod>(methodStr, true, out var m) ? m : CopyRangeMethod.Auto;
            return new CopyRangeOptimizer(logger, method);
        });

        // Security Services
        services.AddSingleton<IApiTokenAuthService>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<ApiTokenAuthService>>();
            var config = provider.GetService<IConfiguration>();
            var storagePath = config?.GetValue<string>("Security:TokenStoragePath");
            return new ApiTokenAuthService(logger, storagePath);
        });

        services.AddSingleton<IUntrustedDeviceHandler>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<UntrustedDeviceHandler>>();
            var encryptionService = provider.GetRequiredService<IEncryptionService>();
            return new UntrustedDeviceHandler(logger, encryptionService);
        });

        // Transfer Services
        services.AddSingleton<ConcurrentFileWriterFactory>();

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
    private readonly IConfigXmlService _configXmlService;
    private readonly ILogger<SyncEngineHostedService> _logger;

    public SyncEngineHostedService(
        ISyncEngine syncEngine,
        SyncEventBroadcaster eventBroadcaster,
        IConfiguration configuration,
        IConfigXmlService configXmlService,
        ILogger<SyncEngineHostedService> logger)
    {
        _syncEngine = syncEngine;
        _eventBroadcaster = eventBroadcaster;
        _configuration = configuration;
        _configXmlService = configXmlService;
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

            // First, try to load from config.xml (Syncthing-compatible format)
            if (_configXmlService.ConfigExists())
            {
                _logger.LogInformation("Loading configuration from config.xml at {Path}", _configXmlService.ConfigPath);
                await LoadFromConfigXmlAsync();
                return;
            }

            _logger.LogInformation("No config.xml found, falling back to appsettings.json");

            // Fallback: Load sync configuration from appsettings
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

    private async Task LoadFromConfigXmlAsync()
    {
        try
        {
            var configXml = await _configXmlService.LoadAsync();

            // Validate configuration
            var validation = _configXmlService.Validate(configXml);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Configuration validation failed: {Errors}", string.Join(", ", validation.Errors));
            }

            // Add devices (skip local device)
            var localDeviceId = (await _syncEngine.GetConfigurationAsync()).DeviceId;
            foreach (var device in configXml.Devices.Where(d => d.Id != localDeviceId))
            {
                _logger.LogInformation("Adding device {DeviceId} ({DeviceName}) from config.xml", device.Id, device.Name);
                await _syncEngine.AddDeviceAsync(device.Id, device.Name, null, device.Addresses);
            }

            // Add folders
            foreach (var folder in configXml.Folders)
            {
                // Skip if folder already exists (loaded from database)
                var existingFolder = await _syncEngine.GetFolderAsync(folder.Id);
                if (existingFolder != null)
                {
                    _logger.LogDebug("Folder {FolderId} already exists, skipping", folder.Id);
                    continue;
                }

                _logger.LogInformation("Adding folder {FolderId} ({FolderLabel}) at {Path} from config.xml",
                    folder.Id, folder.Label, folder.Path);

                await _syncEngine.AddFolderAsync(folder.Id, folder.Label ?? folder.Id, folder.Path, folder.Type ?? "sendreceive");

                // Share folder with devices
                foreach (var device in folder.Devices)
                {
                    await _syncEngine.ShareFolderWithDeviceAsync(folder.Id, device.Id);
                }
            }

            _logger.LogInformation("Loaded {FolderCount} folders and {DeviceCount} devices from config.xml",
                configXml.Folders.Count, configXml.Devices.Count);
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("Config.xml not found at {Path}", _configXmlService.ConfigPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading configuration from config.xml");
            throw;
        }
    }
}