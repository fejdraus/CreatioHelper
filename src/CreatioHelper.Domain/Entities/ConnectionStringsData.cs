using System;
using System.Collections.Generic;

namespace CreatioHelper.Domain.Entities;

public enum RedisConnectionMode
{
    SingleNode,
    Cluster,
    Sentinel,
}

public class ConnectionStringsData
{
    public Dictionary<string, string> RawEntries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string DbType { get; set; } = "";
    public string DbServer { get; set; } = "";
    public int DbPort { get; set; }
    public string DbCatalog { get; set; } = "";
    public string DbUserId { get; set; } = "";
    public string DbPassword { get; set; } = "";
    public string DbExtraParams { get; set; } = "";

    public RedisConnectionMode RedisMode { get; set; }
    public bool SentinelIsLegacyEntry { get; set; }
    public string RedisClusterHosts { get; set; } = "";
    public string RedisHost { get; set; } = "";
    public int RedisPort { get; set; }
    public int RedisDb { get; set; }
    public string RedisPassword { get; set; } = "";
    public string RedisExtraParams { get; set; } = "";

    public string SentinelHosts { get; set; } = "";
    public string SentinelMasterName { get; set; } = "";
    public bool SentinelScanForOther { get; set; }
    public int SentinelDb { get; set; }
    public string SentinelExtraParams { get; set; } = "";

    public bool MqParsed { get; set; }
    public string MqUser { get; set; } = "";
    public string MqPassword { get; set; } = "";
    public string MqHost { get; set; } = "";
    public int MqPort { get; set; }
    public string MqVirtualHost { get; set; } = "";

    public string ElasticUser { get; set; } = "";
    public string ElasticPassword { get; set; } = "";

    public string InfluxUrl { get; set; } = "";
    public string InfluxUser { get; set; } = "";
    public string InfluxPassword { get; set; } = "";
    public int InfluxBatchIntervalMs { get; set; } = 5000;

    public string S3ServiceUrl { get; set; } = "";
    public string S3AccessKey { get; set; } = "";
    public string S3SecretKey { get; set; } = "";
    public string S3ObjectBucketName { get; set; } = "";
    public string S3RecycleBucketName { get; set; } = "";

    public string DefPackagesWorkingCopyPath { get; set; } = "";
    public string TempDirectoryPath { get; set; } = "";
    public string SourceControlAuthPath { get; set; } = "";
}
