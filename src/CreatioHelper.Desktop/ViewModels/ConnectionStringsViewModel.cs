using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.ViewModels;

public partial class RedisClusterNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private int? _port = 6379;
}

public partial class RedisSectionAttributeViewModel : ObservableObject
{
    public string Name { get; init; } = "";
    [ObservableProperty] private string _value = "";
}

public partial class ConnectionStringsViewModel : ObservableObject
{
    public const string RedisModeSingleNode = "Single node";
    public const string RedisModeCluster = "Cluster";
    public const string RedisModeSentinel = "Sentinel (deprecated)";

    private static readonly Version SentinelRetiredVersion = new(7, 18, 3);
    private static readonly Version ClusterMinVersion = new(7, 18, 0);

    public const string StackExchangeClientsManager =
        "Terrasoft.Redis.StackExchangeAdapters.RedisClientsManagerAdapter, Terrasoft.Redis.StackExchangeAdapters";

    private static readonly (string Name, string Value)[] RecommendedRedisSettings =
    {
        ("enablePerformanceMonitor", "false"),
        ("executionTimeLoggingThresholdSec", "5"),
        ("featureUseCustomRedisTimeouts", "false"),
        ("clientRetryTimeoutMs", "4000"),
        ("clientReceiveTimeoutMs", "3000"),
        ("clientSendTimeoutMs", "3000"),
        ("clientConnectTimeoutMs", "5000"),
        ("clientSyncTimeoutMs", "5000"),
        ("clientAsyncTimeoutMs", "5000"),
        ("deactivatedClientsExpirySec", "0"),
        ("operationRetryIntervalMs", "5000"),
        ("operationRetryCount", "25"),
        ("clientsManager", StackExchangeClientsManager),
        ("abortOnConnectFail", "false"),
        ("timeToCheckConfigurationSeconds", "60"),
    };

    private readonly IConnectionStringsEditor _editor;
    private readonly IWebConfigEditor _webConfigEditor;
    private readonly Func<string?> _sitePathProvider;
    private readonly Func<Version?> _siteVersionProvider;
    private ConnectionStringsData? _loadedData;
    private string? _lastSitePath;
    private bool _isPopulating;

    private static readonly HashSet<string> _editableProperties = new()
    {
        nameof(DbServer), nameof(DbPort), nameof(DbCatalog), nameof(DbUserId), nameof(DbPassword), nameof(DbExtraParams),
        nameof(RedisHost), nameof(RedisPort), nameof(RedisDb), nameof(RedisPassword), nameof(RedisClusterHosts), nameof(RedisExtraParams),
        nameof(SentinelHosts), nameof(SentinelMasterName), nameof(SentinelScanForOther), nameof(SentinelDb), nameof(SentinelExtraParams),
        nameof(SelectedRedisMode), nameof(UseRetryRedisOperation),
        nameof(MqUser), nameof(MqPassword), nameof(MqHost), nameof(MqPort), nameof(MqVirtualHost),
        nameof(ElasticUser), nameof(ElasticPassword),
        nameof(InfluxUrl), nameof(InfluxUser), nameof(InfluxPassword), nameof(InfluxBatchIntervalMs),
        nameof(S3ServiceUrl), nameof(S3AccessKey), nameof(S3SecretKey), nameof(S3ObjectBucketName), nameof(S3RecycleBucketName),
        nameof(DefPackagesWorkingCopyPath), nameof(TempDirectoryPath), nameof(SourceControlAuthPath),
    };

    [ObservableProperty] private bool _isConfigLoaded;

    [ObservableProperty] private bool _hasDb;
    [ObservableProperty] private bool _hasRedisEntry;
    [ObservableProperty] private bool _hasRedis;
    [ObservableProperty] private bool _hasRedisCluster;
    [ObservableProperty] private bool _hasRedisSentinel;

    [ObservableProperty] private List<string> _redisModes = new() { RedisModeSingleNode };
    [ObservableProperty] private string _selectedRedisMode = RedisModeSingleNode;

    public ObservableCollection<RedisClusterNodeViewModel> ClusterNodes { get; } = new();
    public ObservableCollection<RedisSectionAttributeViewModel> RedisSectionAttributes { get; } = new();

    [ObservableProperty] private bool _hasRedisSection;
    [ObservableProperty] private bool _showClientsManagerWarning;
    [ObservableProperty] private bool _hasMq;
    [ObservableProperty] private bool _hasElastic;
    [ObservableProperty] private bool _hasInflux;
    [ObservableProperty] private bool _hasS3;
    [ObservableProperty] private bool _hasPaths;

    [ObservableProperty] private string _dbType = "";
    [ObservableProperty] private string _dbServer = "";
    [ObservableProperty] private int? _dbPort;
    [ObservableProperty] private string _dbCatalog = "";
    [ObservableProperty] private string _dbUserId = "";
    [ObservableProperty] private string _dbPassword = "";
    [ObservableProperty] private string _dbExtraParams = "";

    [ObservableProperty] private string _redisHost = "";
    [ObservableProperty] private int? _redisPort;
    [ObservableProperty] private int? _redisDb;
    [ObservableProperty] private string _redisPassword = "";
    [ObservableProperty] private string _redisClusterHosts = "";
    [ObservableProperty] private string _redisExtraParams = "";
    [ObservableProperty] private bool _useRetryRedisOperation;

    [ObservableProperty] private string _sentinelHosts = "";
    [ObservableProperty] private string _sentinelMasterName = "";
    [ObservableProperty] private bool _sentinelScanForOther;
    [ObservableProperty] private int? _sentinelDb;
    [ObservableProperty] private string _sentinelExtraParams = "";

    [ObservableProperty] private string _mqUser = "";
    [ObservableProperty] private string _mqPassword = "";
    [ObservableProperty] private string _mqHost = "";
    [ObservableProperty] private int? _mqPort;
    [ObservableProperty] private string _mqVirtualHost = "";

    [ObservableProperty] private string _elasticUser = "";
    [ObservableProperty] private string _elasticPassword = "";

    [ObservableProperty] private string _influxUrl = "";
    [ObservableProperty] private string _influxUser = "";
    [ObservableProperty] private string _influxPassword = "";
    [ObservableProperty] private int? _influxBatchIntervalMs = 5000;

    [ObservableProperty] private string _s3ServiceUrl = "";
    [ObservableProperty] private string _s3AccessKey = "";
    [ObservableProperty] private string _s3SecretKey = "";
    [ObservableProperty] private string _s3ObjectBucketName = "";
    [ObservableProperty] private string _s3RecycleBucketName = "";

    [ObservableProperty] private string _defPackagesWorkingCopyPath = "";
    [ObservableProperty] private string _tempDirectoryPath = "";
    [ObservableProperty] private string _sourceControlAuthPath = "";

    public event EventHandler<string>? SaveFailed;

    public event EventHandler? ConfigSaved;

    public ConnectionStringsViewModel(IConnectionStringsEditor editor, IWebConfigEditor webConfigEditor, Func<string?> sitePathProvider, Func<Version?> siteVersionProvider)
    {
        _editor = editor;
        _webConfigEditor = webConfigEditor;
        _sitePathProvider = sitePathProvider;
        _siteVersionProvider = siteVersionProvider;
    }

    [RelayCommand]
    internal void LoadConfig()
    {
        var sitePath = _sitePathProvider();
        if (string.IsNullOrWhiteSpace(sitePath) || !File.Exists(Path.Combine(sitePath, "ConnectionStrings.config")))
        {
            Unload();
            return;
        }

        try
        {
            _isPopulating = true;
            _loadedData = _editor.Read(sitePath);
            _lastSitePath = sitePath;
            PopulateFromData(_loadedData);
            UseRetryRedisOperation = _webConfigEditor.ReadRetryRedisOperation(sitePath) ?? false;
            PopulateRedisSection(sitePath);
            IsConfigLoaded = true;
        }
        catch
        {
            Unload();
        }
        finally
        {
            _isPopulating = false;
        }
    }

    private void Unload()
    {
        IsConfigLoaded = false;
        _loadedData = null;
        _lastSitePath = null;
    }

    private void PopulateFromData(ConnectionStringsData data)
    {
        var siteVersion = _siteVersionProvider();
        var versionKnown = siteVersion is not null && siteVersion.Major > 0;

        HasDb = true;
        HasRedisEntry = true;

        var clusterAllowed = !versionKnown || siteVersion! >= ClusterMinVersion
            || data.RedisMode == RedisConnectionMode.Cluster;
        var sentinelAllowed = (versionKnown && siteVersion! < SentinelRetiredVersion)
            || data.RedisMode == RedisConnectionMode.Sentinel;
        var modes = new List<string> { RedisModeSingleNode };
        if (clusterAllowed)
        {
            modes.Add(RedisModeCluster);
        }
        if (sentinelAllowed)
        {
            modes.Add(RedisModeSentinel);
        }
        RedisModes = modes;
        SelectedRedisMode = data.RedisMode switch
        {
            RedisConnectionMode.Cluster => RedisModeCluster,
            RedisConnectionMode.Sentinel => RedisModeSentinel,
            _ => RedisModeSingleNode,
        };
        UpdateRedisSectionVisibility();
        HasMq = data.MqParsed || !data.RawEntries.ContainsKey("messageBroker");
        HasElastic = true;
        HasInflux = true;
        HasS3 = true;
        HasPaths = true;

        DbType = data.DbType;
        DbServer = data.DbServer;
        DbPort = data.DbPort > 0 ? data.DbPort : null;
        DbCatalog = data.DbCatalog;
        DbUserId = data.DbUserId;
        DbPassword = data.DbPassword;
        DbExtraParams = data.DbExtraParams;

        RedisHost = data.RedisHost;
        RedisPort = data.RedisPort > 0 ? data.RedisPort : null;
        RedisDb = data.RedisDb;
        RedisPassword = data.RedisPassword;
        RedisClusterHosts = data.RedisClusterHosts;
        RebuildClusterNodes(data.RedisClusterHosts);
        RedisExtraParams = data.RedisExtraParams;

        SentinelHosts = data.SentinelHosts;
        SentinelMasterName = data.SentinelMasterName;
        SentinelScanForOther = data.SentinelScanForOther;
        SentinelDb = data.SentinelDb;
        SentinelExtraParams = data.SentinelExtraParams;

        MqUser = data.MqUser;
        MqPassword = data.MqPassword;
        MqHost = data.MqHost;
        MqPort = data.MqPort > 0 ? data.MqPort : null;
        MqVirtualHost = data.MqVirtualHost;

        ElasticUser = data.ElasticUser;
        ElasticPassword = data.ElasticPassword;

        InfluxUrl = data.InfluxUrl;
        InfluxUser = data.InfluxUser;
        InfluxPassword = data.InfluxPassword;
        InfluxBatchIntervalMs = data.InfluxBatchIntervalMs;

        S3ServiceUrl = data.S3ServiceUrl;
        S3AccessKey = data.S3AccessKey;
        S3SecretKey = data.S3SecretKey;
        S3ObjectBucketName = data.S3ObjectBucketName;
        S3RecycleBucketName = data.S3RecycleBucketName;

        DefPackagesWorkingCopyPath = data.DefPackagesWorkingCopyPath;
        TempDirectoryPath = data.TempDirectoryPath;
        SourceControlAuthPath = data.SourceControlAuthPath;
    }

    private void PopulateRedisSection(string sitePath)
    {
        foreach (var attribute in RedisSectionAttributes)
        {
            attribute.PropertyChanged -= OnRedisSectionAttributeChanged;
        }
        RedisSectionAttributes.Clear();

        var attributes = _webConfigEditor.ReadRedisSection(sitePath);
        HasRedisSection = attributes is { Count: > 0 };
        if (attributes is null)
        {
            UpdateClientsManagerWarning();
            return;
        }

        foreach (var attribute in attributes)
        {
            var item = new RedisSectionAttributeViewModel { Name = attribute.Key, Value = attribute.Value };
            item.PropertyChanged += OnRedisSectionAttributeChanged;
            RedisSectionAttributes.Add(item);
        }
        UpdateClientsManagerWarning();
    }

    private void OnRedisSectionAttributeChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateClientsManagerWarning();
        if (!_isPopulating)
        {
            SaveConfig();
        }
    }

    private void UpdateClientsManagerWarning()
    {
        var clientsManager = RedisSectionAttributes
            .FirstOrDefault(a => string.Equals(a.Name, "clientsManager", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        ShowClientsManagerWarning = SelectedRedisMode == RedisModeCluster
            && !clientsManager.Contains("StackExchangeAdapters", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ApplyRecommendedRedisSettings()
    {
        if (!HasRedisSection)
        {
            return;
        }

        _isPopulating = true;
        foreach (var (name, value) in RecommendedRedisSettings)
        {
            var existing = RedisSectionAttributes
                .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Value = value;
            }
            else
            {
                var item = new RedisSectionAttributeViewModel { Name = name, Value = value };
                item.PropertyChanged += OnRedisSectionAttributeChanged;
                RedisSectionAttributes.Add(item);
            }
        }
        _isPopulating = false;

        UpdateClientsManagerWarning();
        SaveConfig();
    }

    partial void OnSelectedRedisModeChanged(string value) => UpdateRedisSectionVisibility();

    private void UpdateRedisSectionVisibility()
    {
        HasRedis = HasRedisEntry && SelectedRedisMode == RedisModeSingleNode;
        HasRedisCluster = HasRedisEntry && SelectedRedisMode == RedisModeCluster;
        HasRedisSentinel = HasRedisEntry && SelectedRedisMode == RedisModeSentinel;

        if (SelectedRedisMode == RedisModeCluster && !UseRetryRedisOperation)
        {
            var populating = _isPopulating;
            _isPopulating = true;
            UseRetryRedisOperation = true;
            _isPopulating = populating;
        }

        UpdateClientsManagerWarning();
    }

    public static List<RedisClusterNodeViewModel> ParseClusterNodes(string hosts)
    {
        var nodes = new List<RedisClusterNodeViewModel>();
        foreach (var part in hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colonIdx = part.LastIndexOf(':');
            if (colonIdx > 0 && int.TryParse(part[(colonIdx + 1)..], out var port))
            {
                nodes.Add(new RedisClusterNodeViewModel { Host = part[..colonIdx], Port = port });
            }
            else
            {
                nodes.Add(new RedisClusterNodeViewModel { Host = part, Port = null });
            }
        }
        return nodes;
    }

    public static string FormatClusterNodes(IEnumerable<RedisClusterNodeViewModel> nodes)
    {
        return string.Join(",", nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.Host))
            .Select(n => n.Port is > 0 ? $"{n.Host}:{n.Port}" : n.Host));
    }

    private void RebuildClusterNodes(string hosts)
    {
        foreach (var node in ClusterNodes)
        {
            node.PropertyChanged -= OnClusterNodeChanged;
        }
        ClusterNodes.Clear();
        foreach (var node in ParseClusterNodes(hosts))
        {
            node.PropertyChanged += OnClusterNodeChanged;
            ClusterNodes.Add(node);
        }
    }

    private void OnClusterNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isPopulating)
        {
            RedisClusterHosts = FormatClusterNodes(ClusterNodes);
        }
    }

    [RelayCommand]
    private void AddClusterNode()
    {
        var node = new RedisClusterNodeViewModel();
        node.PropertyChanged += OnClusterNodeChanged;
        ClusterNodes.Add(node);
    }

    [RelayCommand]
    private void RemoveClusterNode(RedisClusterNodeViewModel node)
    {
        node.PropertyChanged -= OnClusterNodeChanged;
        ClusterNodes.Remove(node);
        RedisClusterHosts = FormatClusterNodes(ClusterNodes);
    }

    private void SaveConfig()
    {
        if (_lastSitePath is null)
        {
            return;
        }
        try
        {
            BuildData(_loadedData!);
            _editor.Write(_lastSitePath, _loadedData!);
            _webConfigEditor.WriteRetryRedisOperation(
                _lastSitePath,
                SelectedRedisMode == RedisModeCluster || UseRetryRedisOperation);
            if (HasRedisSection)
            {
                _webConfigEditor.WriteRedisSection(
                    _lastSitePath,
                    RedisSectionAttributes.Select(a => new KeyValuePair<string, string>(a.Name, a.Value)).ToList());
            }
            if (DbExtraParams != _loadedData!.DbExtraParams)
            {
                _isPopulating = true;
                DbExtraParams = _loadedData!.DbExtraParams;
                _isPopulating = false;
            }
            ConfigSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SaveFailed?.Invoke(this, ex.Message);
        }
    }

    private void BuildData(ConnectionStringsData data)
    {
        data.DbServer = DbServer;
        data.DbPort = DbPort ?? 0;
        data.DbCatalog = DbCatalog;
        data.DbUserId = DbUserId;
        data.DbPassword = DbPassword;
        data.DbExtraParams = DbExtraParams;

        data.RedisMode = SelectedRedisMode switch
        {
            RedisModeCluster => RedisConnectionMode.Cluster,
            RedisModeSentinel => RedisConnectionMode.Sentinel,
            _ => RedisConnectionMode.SingleNode,
        };
        data.RedisHost = RedisHost;
        data.RedisPort = RedisPort ?? 0;
        data.RedisDb = RedisDb ?? 0;
        data.RedisPassword = RedisPassword;
        data.RedisClusterHosts = RedisClusterHosts;
        data.RedisExtraParams = RedisExtraParams;

        data.SentinelHosts = SentinelHosts;
        data.SentinelMasterName = SentinelMasterName;
        data.SentinelScanForOther = SentinelScanForOther;
        data.SentinelDb = SentinelDb ?? 0;
        data.SentinelExtraParams = SentinelExtraParams;

        data.MqUser = MqUser;
        data.MqPassword = MqPassword;
        data.MqHost = MqHost;
        data.MqPort = MqPort ?? 0;
        data.MqVirtualHost = MqVirtualHost;

        data.ElasticUser = ElasticUser;
        data.ElasticPassword = ElasticPassword;

        data.InfluxUrl = InfluxUrl;
        data.InfluxUser = InfluxUser;
        data.InfluxPassword = InfluxPassword;
        data.InfluxBatchIntervalMs = InfluxBatchIntervalMs ?? 5000;

        data.S3ServiceUrl = S3ServiceUrl;
        data.S3AccessKey = S3AccessKey;
        data.S3SecretKey = S3SecretKey;
        data.S3ObjectBucketName = S3ObjectBucketName;
        data.S3RecycleBucketName = S3RecycleBucketName;

        data.DefPackagesWorkingCopyPath = DefPackagesWorkingCopyPath;
        data.TempDirectoryPath = TempDirectoryPath;
        data.SourceControlAuthPath = SourceControlAuthPath;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loadedData is not null && !_isPopulating && e.PropertyName is not null
            && _editableProperties.Contains(e.PropertyName))
        {
            SaveConfig();
        }
    }
}
