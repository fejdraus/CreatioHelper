using System.Text;
using System.Xml;
using System.Xml.Serialization;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Configuration;

/// <summary>
/// Service for reading and writing Syncthing-compatible config.xml files.
/// </summary>
public class ConfigXmlService : IConfigXmlService
{
    private readonly ILogger<ConfigXmlService> _logger;
    private readonly string _configDirectory;
    private readonly string _configPath;
    private readonly XmlSerializer _serializer;
    private readonly object _lock = new();

    public string ConfigPath => _configPath;

    public ConfigXmlService(ILogger<ConfigXmlService> logger, string? configDirectory = null)
    {
        _logger = logger;
        _configDirectory = configDirectory ?? GetDefaultConfigDirectory();
        _configPath = Path.Combine(_configDirectory, "config.xml");
        _serializer = new XmlSerializer(typeof(ConfigXml));

        // Ensure config directory exists
        Directory.CreateDirectory(_configDirectory);
    }

    public string GetConfigDirectory() => _configDirectory;

    public bool ConfigExists() => File.Exists(_configPath);

    public async Task<ConfigXml> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!ConfigExists())
        {
            _logger.LogInformation("Config file not found at {Path}, will need to create default", _configPath);
            throw new FileNotFoundException("Configuration file not found", _configPath);
        }

        try
        {
            _logger.LogDebug("Loading configuration from {Path}", _configPath);

            string xml;
            lock (_lock)
            {
                xml = File.ReadAllText(_configPath, Encoding.UTF8);
            }

            using var reader = new StringReader(xml);
            var config = (ConfigXml?)_serializer.Deserialize(reader);

            if (config == null)
            {
                throw new InvalidOperationException("Failed to deserialize configuration");
            }

            _logger.LogInformation("Loaded configuration v{Version} with {FolderCount} folders and {DeviceCount} devices",
                config.Version, config.Folders.Count, config.Devices.Count);

            return config;
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            _logger.LogError(ex, "Error loading configuration from {Path}", _configPath);
            throw;
        }
    }

    public async Task SaveAsync(ConfigXml config, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Saving configuration to {Path}", _configPath);

            // Create backup if file exists
            if (File.Exists(_configPath))
            {
                var backupPath = _configPath + ".bak";
                lock (_lock)
                {
                    File.Copy(_configPath, backupPath, overwrite: true);
                }
                _logger.LogDebug("Created backup at {BackupPath}", backupPath);
            }

            // Serialize to XML
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "    ",
                Encoding = Encoding.UTF8,
                OmitXmlDeclaration = false
            };

            using var stringWriter = new StringWriter();
            using (var xmlWriter = XmlWriter.Create(stringWriter, settings))
            {
                var namespaces = new XmlSerializerNamespaces();
                namespaces.Add("", ""); // Remove default namespaces
                _serializer.Serialize(xmlWriter, config, namespaces);
            }

            var xml = stringWriter.ToString();

            // Write atomically using temp file
            var tempPath = _configPath + ".tmp";
            lock (_lock)
            {
                File.WriteAllText(tempPath, xml, Encoding.UTF8);
                File.Move(tempPath, _configPath, overwrite: true);
            }

            _logger.LogInformation("Saved configuration v{Version} with {FolderCount} folders and {DeviceCount} devices",
                config.Version, config.Folders.Count, config.Devices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving configuration to {Path}", _configPath);
            throw;
        }
    }

    public async Task<ConfigXml> CreateDefaultConfigAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating default configuration for device {DeviceId} ({DeviceName})", deviceId, deviceName);

        var config = new ConfigXml
        {
            Version = 37,
            Devices = new List<ConfigXmlDevice>
            {
                new ConfigXmlDevice
                {
                    Id = deviceId,
                    Name = deviceName,
                    Compression = "metadata",
                    Addresses = new List<string> { "dynamic" }
                }
            },
            Gui = new ConfigXmlGui
            {
                Enabled = true,
                Tls = false,
                Address = "127.0.0.1:8384",
                ApiKey = GenerateApiKey(),
                Theme = "default"
            },
            Options = new ConfigXmlOptions
            {
                ListenAddresses = new List<string> { "default" },
                GlobalAnnounceServers = new List<string> { "default" },
                GlobalAnnounceEnabled = true,
                LocalAnnounceEnabled = true,
                LocalAnnouncePort = 21027,
                RelaysEnabled = true,
                NatEnabled = true,
                StunServers = new List<string> { "default" }
            },
            Defaults = new ConfigXmlDefaults
            {
                Folder = new ConfigXmlFolder
                {
                    RescanIntervalS = 3600,
                    FsWatcherEnabled = true,
                    FsWatcherDelayS = 10,
                    IgnorePerms = false,
                    AutoNormalize = true
                },
                Device = new ConfigXmlDevice
                {
                    Compression = "metadata",
                    Addresses = new List<string> { "dynamic" }
                },
                Ignores = new ConfigXmlIgnores
                {
                    Lines = new List<string>
                    {
                        "// Default ignore patterns",
                        ".stfolder",
                        ".stignore",
                        "~*",
                        ".~*",
                        "*.tmp",
                        "*.temp"
                    }
                }
            }
        };

        await SaveAsync(config, cancellationToken);
        return config;
    }

    public SyncConfiguration ToSyncConfiguration(ConfigXml config)
    {
        var syncConfig = new SyncConfiguration
        {
            DeviceId = config.Devices.FirstOrDefault()?.Id ?? string.Empty,
            DeviceName = config.Devices.FirstOrDefault()?.Name ?? Environment.MachineName,
            Port = ExtractPort(config.Options.ListenAddresses.FirstOrDefault() ?? "default"),
            GlobalAnnounceEnabled = config.Options.GlobalAnnounceEnabled,
            LocalAnnounceEnabled = config.Options.LocalAnnounceEnabled,
            LocalAnnouncePort = config.Options.LocalAnnouncePort,
            RelaysEnabled = config.Options.RelaysEnabled,
            NatEnabled = config.Options.NatEnabled,
            MaxSendKbps = config.Options.MaxSendKbps,
            MaxRecvKbps = config.Options.MaxRecvKbps,
            ReconnectionIntervalSeconds = config.Options.ReconnectionIntervalS,
            RelayReconnectIntervalMinutes = config.Options.RelayReconnectIntervalM,
            StartBrowser = config.Options.StartBrowser,
            NatLeaseMinutes = config.Options.NatLeaseMinutes,
            NatRenewalMinutes = config.Options.NatRenewalMinutes,
            NatTimeoutSeconds = config.Options.NatTimeoutSeconds,
            AutoUpgradeEnabled = config.Options.AutoUpgradeIntervalH > 0,
            AutoUpgradeIntervalHours = config.Options.AutoUpgradeIntervalH,
            UpgradeToPreReleases = config.Options.UpgradeToPreReleases,
            KeepTemporariesHours = config.Options.KeepTemporariesH,
            CacheIgnoredFiles = config.Options.CacheIgnoredFiles,
            ProgressUpdateIntervalSeconds = config.Options.ProgressUpdateIntervalS,
            LimitBandwidthInLan = config.Options.LimitBandwidthInLan,
            SetLowPriority = config.Options.SetLowPriority,
            MaxFolderConcurrency = config.Options.MaxFolderConcurrency,
            GuiEnabled = config.Gui.Enabled,
            GuiAddress = config.Gui.Address,
            GuiTls = config.Gui.Tls,
            GuiApiKey = config.Gui.ApiKey,
            GuiUser = config.Gui.User,
            GuiPassword = config.Gui.Password
        };

        // Map LDAP configuration
        if (config.Ldap != null)
        {
            syncConfig.LdapAddress = config.Ldap.Address;
            syncConfig.LdapBindDN = config.Ldap.BindDN;
            syncConfig.LdapTransport = config.Ldap.Transport;
            syncConfig.LdapInsecureSkipVerify = config.Ldap.InsecureSkipVerify;
            syncConfig.LdapSearchBaseDN = config.Ldap.SearchBaseDN;
            syncConfig.LdapSearchFilter = config.Ldap.SearchFilter;
        }

        // Set listen addresses ("default" expands to tcp + quic on port 22000)
        var listenAddresses = config.Options.ListenAddresses
            .SelectMany(addr => addr == "default"
                ? new[] { "tcp://0.0.0.0:22000", "quic://0.0.0.0:22000" }
                : new[] { addr })
            .ToList();
        syncConfig.SetListenAddresses(listenAddresses);

        // Set global announce servers
        var announceServers = config.Options.GlobalAnnounceServers
            .Where(s => s != "default" && !string.IsNullOrWhiteSpace(s))
            .ToList();
        syncConfig.SetGlobalAnnounceServers(announceServers);

        // Set NAT traversal configuration
        syncConfig.EnableNatTraversal(config.Options.NatEnabled);
        if (syncConfig.NatTraversal != null)
        {
            syncConfig.NatTraversal.StunKeepAliveSeconds = config.Options.StunKeepaliveMinS;
            syncConfig.NatTraversal.StunServers = config.Options.StunServers
                .Where(s => s != "default" && !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        return syncConfig;
    }

    public ConfigXml FromSyncConfiguration(SyncConfiguration config, IEnumerable<SyncDevice> devices, IEnumerable<SyncFolder> folders)
    {
        var configXml = new ConfigXml
        {
            Version = 37,
            Devices = devices.Select(d => new ConfigXmlDevice
            {
                Id = d.DeviceId,
                Name = d.DeviceName,
                Compression = d.Compression ?? "metadata",
                Paused = d.Paused,
                Introducer = d.Introducer,
                MaxSendKbps = d.MaxSendKbps,
                MaxRecvKbps = d.MaxRecvKbps,
                Untrusted = d.Untrusted,
                Addresses = d.Addresses?.ToList() ?? new List<string> { "dynamic" }
            }).ToList(),
            Folders = folders.Select(f => new ConfigXmlFolder
            {
                Id = f.Id,
                Label = f.Label,
                Path = f.Path,
                Type = f.SyncType switch
                {
                    SyncFolderType.SendOnly => "sendonly",
                    SyncFolderType.ReceiveOnly => "receiveonly",
                    SyncFolderType.Master => "receiveencrypted",
                    _ => "sendreceive"
                },
                Paused = f.Paused,
                Devices = f.Devices.Select(deviceId => new ConfigXmlFolderDevice { Id = deviceId }).ToList()
            }).ToList(),
            Gui = new ConfigXmlGui
            {
                Enabled = config.GuiEnabled,
                Address = config.GuiAddress,
                Tls = config.GuiTls,
                ApiKey = config.GuiApiKey,
                User = config.GuiUser,
                Password = config.GuiPassword
            },
            Ldap = !string.IsNullOrWhiteSpace(config.LdapAddress) ? new ConfigXmlLdap
            {
                Address = config.LdapAddress,
                BindDN = config.LdapBindDN,
                Transport = config.LdapTransport,
                InsecureSkipVerify = config.LdapInsecureSkipVerify,
                SearchBaseDN = config.LdapSearchBaseDN,
                SearchFilter = config.LdapSearchFilter
            } : null,
            Options = new ConfigXmlOptions
            {
                ListenAddresses = config.ListenAddresses.Count > 0 ? config.ListenAddresses : new List<string> { "default" },
                GlobalAnnounceServers = config.GlobalAnnounceServers.Count > 0 ? config.GlobalAnnounceServers : new List<string> { "default" },
                GlobalAnnounceEnabled = config.GlobalAnnounceEnabled,
                LocalAnnounceEnabled = config.LocalAnnounceEnabled,
                LocalAnnouncePort = config.LocalAnnouncePort,
                RelaysEnabled = config.RelaysEnabled,
                NatEnabled = config.NatEnabled,
                MaxSendKbps = config.MaxSendKbps,
                MaxRecvKbps = config.MaxRecvKbps,
                ReconnectionIntervalS = config.ReconnectionIntervalSeconds,
                RelayReconnectIntervalM = config.RelayReconnectIntervalMinutes,
                StartBrowser = config.StartBrowser,
                NatLeaseMinutes = config.NatLeaseMinutes,
                NatRenewalMinutes = config.NatRenewalMinutes,
                NatTimeoutSeconds = config.NatTimeoutSeconds,
                AutoUpgradeIntervalH = config.AutoUpgradeIntervalHours,
                UpgradeToPreReleases = config.UpgradeToPreReleases,
                KeepTemporariesH = config.KeepTemporariesHours,
                CacheIgnoredFiles = config.CacheIgnoredFiles,
                ProgressUpdateIntervalS = config.ProgressUpdateIntervalSeconds,
                LimitBandwidthInLan = config.LimitBandwidthInLan,
                SetLowPriority = config.SetLowPriority,
                MaxFolderConcurrency = config.MaxFolderConcurrency,
                StunServers = config.NatTraversal?.StunServers ?? new List<string> { "default" },
                StunKeepaliveMinS = config.NatTraversal?.StunKeepAliveSeconds ?? 20
            }
        };

        // Add local device if not present
        if (!configXml.Devices.Any(d => d.Id == config.DeviceId))
        {
            configXml.Devices.Insert(0, new ConfigXmlDevice
            {
                Id = config.DeviceId,
                Name = config.DeviceName,
                Compression = "metadata",
                Addresses = new List<string> { "dynamic" }
            });
        }

        return configXml;
    }

    public ConfigValidationResult Validate(ConfigXml config)
    {
        var result = new ConfigValidationResult { IsValid = true };

        // Check for local device
        if (config.Devices.Count == 0)
        {
            result.Errors.Add("Configuration must have at least one device (local device)");
            result.IsValid = false;
        }

        // Validate devices
        foreach (var device in config.Devices)
        {
            if (string.IsNullOrWhiteSpace(device.Id))
            {
                result.Errors.Add($"Device has empty ID");
                result.IsValid = false;
            }
            else if (!IsValidDeviceId(device.Id))
            {
                result.Warnings.Add($"Device ID '{device.Id}' may not be a valid Syncthing device ID format");
            }
        }

        // Validate folders
        foreach (var folder in config.Folders)
        {
            if (string.IsNullOrWhiteSpace(folder.Id))
            {
                result.Errors.Add("Folder has empty ID");
                result.IsValid = false;
            }

            if (string.IsNullOrWhiteSpace(folder.Path))
            {
                result.Errors.Add($"Folder '{folder.Id}' has empty path");
                result.IsValid = false;
            }

            // Check for valid folder type
            var validTypes = new[] { "sendreceive", "sendonly", "receiveonly", "receiveencrypted" };
            if (!validTypes.Contains(folder.Type.ToLowerInvariant()))
            {
                result.Warnings.Add($"Folder '{folder.Id}' has unknown type '{folder.Type}'");
            }

            // Check that folder devices exist
            foreach (var device in folder.Devices)
            {
                if (!config.Devices.Any(d => d.Id == device.Id))
                {
                    result.Warnings.Add($"Folder '{folder.Id}' references unknown device '{device.Id}'");
                }
            }
        }

        // Validate GUI
        if (config.Gui.Enabled && string.IsNullOrWhiteSpace(config.Gui.Address))
        {
            result.Errors.Add("GUI is enabled but address is empty");
            result.IsValid = false;
        }

        // Validate options
        if (config.Options.LocalAnnouncePort <= 0 || config.Options.LocalAnnouncePort > 65535)
        {
            result.Errors.Add($"Invalid local announce port: {config.Options.LocalAnnouncePort}");
            result.IsValid = false;
        }

        return result;
    }

    private static string GetDefaultConfigDirectory()
    {
        // Follow Syncthing's convention: ~/.config/syncthing on Linux, %LOCALAPPDATA%\Syncthing on Windows
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CreatioHelper");
        }
        else if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "CreatioHelper");
        }
        else
        {
            // Linux and others
            var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrEmpty(xdgConfig))
            {
                return Path.Combine(xdgConfig, "creatiohelper");
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "creatiohelper");
        }
    }

    private static string GenerateApiKey()
    {
        var bytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "").Replace("/", "").Replace("=", "")[..32];
    }

    private static int ExtractPort(string listenAddress)
    {
        if (listenAddress == "default")
            return 22000;

        // Parse tcp://0.0.0.0:22000 or similar
        try
        {
            var uri = new Uri(listenAddress.Replace("tcp://", "http://").Replace("quic://", "http://"));
            return uri.Port > 0 ? uri.Port : 22000;
        }
        catch
        {
            return 22000;
        }
    }

    private static bool IsValidDeviceId(string deviceId)
    {
        // Syncthing device IDs are 52 characters with dashes every 7 characters
        // Format: XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        var parts = deviceId.Split('-');
        if (parts.Length != 8)
            return false;

        return parts.All(p => p.Length == 7 && p.All(c => char.IsLetterOrDigit(c)));
    }
}
