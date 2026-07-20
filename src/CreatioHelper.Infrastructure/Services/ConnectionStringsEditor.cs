using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Utils;

namespace CreatioHelper.Infrastructure.Services;

public class ConnectionStringsEditor : IConnectionStringsEditor
{
    private const string FileName = "ConnectionStrings.config";

    private static readonly string[] KnownNames =
    {
        "db", "redis", "redisSentinel", "messageBroker", "elasticsearchCredentials", "influx", "s3Connection",
        "defPackagesWorkingCopyPath", "tempDirectoryPath", "sourceControlAuthPath",
    };

    private static readonly string[] DbServerKeys = { "Server", "Host" };
    private static readonly string[] DbCatalogKeys = { "Initial Catalog", "Database" };
    private static readonly string[] DbUserKeys = { "User ID", "Username", "UID" };
    private static readonly string[] DbPasswordKeys = { "Password", "PWD" };

    private static readonly string[] DbKnownKeys =
    {
        "Data Source", "Server", "Host", "Port",
        "Initial Catalog", "Database", "User ID", "Username", "UID", "Password", "PWD",
    };
    private static readonly string[] RedisKnownKeys =
    {
        "host", "port", "db", "password", "clusterHosts", "sentinelHosts", "masterName", "scanForOtherSentinels",
    };
    private static readonly string[] SentinelKnownKeys = { "sentinelHosts", "masterName", "scanForOtherSentinels", "db" };

    public ConnectionStringsData Read(string sitePath)
    {
        var doc = new XmlDocument();
        doc.Load(Path.Combine(sitePath, FileName));

        var data = new ConnectionStringsData();
        foreach (var name in KnownNames)
        {
            var node = doc.SelectSingleNode($"/connectionStrings/add[@name='{name}']");
            var value = node?.Attributes?["connectionString"]?.Value;
            if (value is not null)
            {
                data.RawEntries[name] = value;
            }
        }

        ReadDb(data, ParseKv(data.RawEntries.GetValueOrDefault("db", "")));
        data.DbExtraParams = ExtractExtras(data.RawEntries.GetValueOrDefault("db", ""), DbKnownKeys);
        var configuredDbType = DetectDbTypeFromSiteConfig(sitePath);
        if (configuredDbType.Length > 0)
        {
            data.DbType = configuredDbType;
        }

        var redisRaw = data.RawEntries.GetValueOrDefault("redis", "");
        var redis = ParseKv(redisRaw);
        data.RedisClusterHosts = redis.GetValueOrDefault("clusterHosts", "");
        data.RedisHost = redis.GetValueOrDefault("host", "");
        data.RedisPort = ParseInt(redis.GetValueOrDefault("port", ""), 0);
        data.RedisDb = ParseInt(redis.GetValueOrDefault("db", ""), 0);
        data.RedisPassword = redis.GetValueOrDefault("password", "");
        data.RedisExtraParams = ExtractExtras(redisRaw, RedisKnownKeys);

        if (data.RawEntries.ContainsKey("redisSentinel"))
        {
            data.RedisMode = RedisConnectionMode.Sentinel;
            data.SentinelIsLegacyEntry = true;
            var sentinelRaw = data.RawEntries.GetValueOrDefault("redisSentinel", "");
            var sentinel = ParseKv(sentinelRaw);
            data.SentinelHosts = sentinel.GetValueOrDefault("sentinelHosts", "");
            data.SentinelMasterName = sentinel.GetValueOrDefault("masterName", "");
            data.SentinelScanForOther = bool.TryParse(sentinel.GetValueOrDefault("scanForOtherSentinels", ""), out var scanLegacy) && scanLegacy;
            data.SentinelDb = ParseInt(sentinel.GetValueOrDefault("db", ""), 0);
            data.SentinelExtraParams = ExtractExtras(sentinelRaw, SentinelKnownKeys);
        }
        else if (redis.ContainsKey("sentinelHosts"))
        {
            data.RedisMode = RedisConnectionMode.Sentinel;
            data.SentinelHosts = redis.GetValueOrDefault("sentinelHosts", "");
            data.SentinelMasterName = redis.GetValueOrDefault("masterName", "");
            data.SentinelScanForOther = bool.TryParse(redis.GetValueOrDefault("scanForOtherSentinels", ""), out var scan) && scan;
            data.SentinelDb = ParseInt(redis.GetValueOrDefault("db", ""), 0);
            data.SentinelExtraParams = data.RedisExtraParams;
        }
        else if (redis.ContainsKey("clusterHosts"))
        {
            data.RedisMode = RedisConnectionMode.Cluster;
        }
        else
        {
            data.RedisMode = RedisConnectionMode.SingleNode;
        }

        ParseAmqp(data.RawEntries.GetValueOrDefault("messageBroker", ""), data);

        var elastic = ParseKv(data.RawEntries.GetValueOrDefault("elasticsearchCredentials", ""));
        data.ElasticUser = elastic.GetValueOrDefault("User", "");
        data.ElasticPassword = elastic.GetValueOrDefault("Password", "");

        var influx = ParseKv(data.RawEntries.GetValueOrDefault("influx", ""));
        data.InfluxUrl = influx.GetValueOrDefault("url", "");
        data.InfluxUser = influx.GetValueOrDefault("user", "");
        data.InfluxPassword = influx.GetValueOrDefault("password", "");
        data.InfluxBatchIntervalMs = ParseInt(influx.GetValueOrDefault("batchIntervalMs", ""), 5000);

        var s3 = ParseKv(data.RawEntries.GetValueOrDefault("s3Connection", ""));
        data.S3ServiceUrl = s3.GetValueOrDefault("ServiceUrl", "");
        data.S3AccessKey = s3.GetValueOrDefault("AccessKey", "");
        data.S3SecretKey = s3.GetValueOrDefault("SecretKey", "");
        data.S3ObjectBucketName = s3.GetValueOrDefault("ObjectBucketName", "");
        data.S3RecycleBucketName = s3.GetValueOrDefault("RecycleBucketName", "");

        data.DefPackagesWorkingCopyPath = data.RawEntries.GetValueOrDefault("defPackagesWorkingCopyPath", "");
        data.TempDirectoryPath = data.RawEntries.GetValueOrDefault("tempDirectoryPath", "");
        data.SourceControlAuthPath = data.RawEntries.GetValueOrDefault("sourceControlAuthPath", "");

        return data;
    }

    public void Write(string sitePath, ConnectionStringsData data)
    {
        var path = Path.Combine(sitePath, FileName);
        var doc = new XmlDocument();
        doc.Load(path);

        var dbCreated = EnsureEntry(data, "db",
            data.DbServer.Length > 0 || data.DbCatalog.Length > 0 || data.DbUserId.Length > 0
            || data.DbPassword.Length > 0 || data.DbExtraParams.Length > 0,
            DbTemplate(data.DbType));
        if (dbCreated && data.DbExtraParams.Length == 0)
        {
            data.DbExtraParams = ExtractExtras(data.RawEntries["db"], DbKnownKeys);
        }
        EnsureEntry(data, "redis", data.RedisMode switch
        {
            RedisConnectionMode.Cluster => data.RedisClusterHosts.Length > 0,
            RedisConnectionMode.Sentinel => !data.RawEntries.ContainsKey("redisSentinel")
                && (data.SentinelHosts.Length > 0 || data.SentinelMasterName.Length > 0),
            _ => data.RedisHost.Length > 0 || data.RedisPassword.Length > 0 || data.RedisExtraParams.Length > 0,
        });
        EnsureEntry(data, "messageBroker", data.MqHost.Length > 0, "amqp://");
        EnsureEntry(data, "elasticsearchCredentials",
            data.ElasticUser.Length > 0 || data.ElasticPassword.Length > 0);
        EnsureEntry(data, "influx",
            data.InfluxUrl.Length > 0 || data.InfluxUser.Length > 0 || data.InfluxPassword.Length > 0);
        EnsureEntry(data, "s3Connection",
            data.S3ServiceUrl.Length > 0 || data.S3AccessKey.Length > 0 || data.S3SecretKey.Length > 0
            || data.S3ObjectBucketName.Length > 0 || data.S3RecycleBucketName.Length > 0);
        EnsureEntry(data, "defPackagesWorkingCopyPath", data.DefPackagesWorkingCopyPath.Length > 0);
        EnsureEntry(data, "tempDirectoryPath", data.TempDirectoryPath.Length > 0);
        EnsureEntry(data, "sourceControlAuthPath", data.SourceControlAuthPath.Length > 0);

        SetEntry(doc, data, "db", raw =>
            ApplyExtras(MergeKv(raw, BuildDbUpdates(raw, data)), DbKnownKeys, data.DbExtraParams));

        if (data.RedisMode == RedisConnectionMode.Sentinel && data.RawEntries.ContainsKey("redisSentinel"))
        {
            SetEntry(doc, data, "redisSentinel", raw => ApplyExtras(MergeKv(raw, new()
            {
                ["sentinelHosts"] = data.SentinelHosts,
                ["masterName"] = data.SentinelMasterName,
                ["scanForOtherSentinels"] = data.SentinelScanForOther ? "true" : "false",
                ["db"] = data.SentinelDb.ToString(),
            }), SentinelKnownKeys, data.SentinelExtraParams));
        }
        else
        {
            RemoveEntry(doc, data, "redisSentinel");
            SetEntry(doc, data, "redis", raw =>
            {
                string result;
                var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var extras = data.RedisExtraParams;
                switch (data.RedisMode)
                {
                    case RedisConnectionMode.Cluster:
                        result = RemoveKeys(raw, new[] { "host", "port", "db", "sentinelHosts", "masterName", "scanForOtherSentinels" });
                        updates["clusterHosts"] = data.RedisClusterHosts;
                        break;
                    case RedisConnectionMode.Sentinel:
                        result = RemoveKeys(raw, new[] { "host", "port", "clusterHosts" });
                        updates["sentinelHosts"] = data.SentinelHosts;
                        updates["masterName"] = data.SentinelMasterName;
                        updates["scanForOtherSentinels"] = data.SentinelScanForOther ? "true" : "false";
                        updates["db"] = data.SentinelDb.ToString();
                        extras = data.SentinelExtraParams;
                        break;
                    default:
                        result = RemoveKeys(raw, new[] { "clusterHosts", "sentinelHosts", "masterName", "scanForOtherSentinels" });
                        updates["host"] = data.RedisHost;
                        if (data.RedisPort > 0)
                        {
                            updates["port"] = data.RedisPort.ToString();
                        }
                        updates["db"] = data.RedisDb.ToString();
                        break;
                }
                updates["password"] = data.RedisPassword;
                return ApplyExtras(MergeKv(result, updates), RedisKnownKeys, extras);
            });
        }

        SetEntry(doc, data, "messageBroker",
            raw => raw.Contains("://", StringComparison.Ordinal) ? BuildAmqp(raw, data) : raw);

        SetEntry(doc, data, "elasticsearchCredentials", raw => MergeKv(raw, new()
        {
            ["User"] = data.ElasticUser,
            ["Password"] = data.ElasticPassword,
        }));

        SetEntry(doc, data, "influx", raw => MergeKv(raw, new()
        {
            ["url"] = data.InfluxUrl,
            ["user"] = data.InfluxUser,
            ["password"] = data.InfluxPassword,
            ["batchIntervalMs"] = data.InfluxBatchIntervalMs.ToString(),
        }));

        SetEntry(doc, data, "s3Connection", raw => MergeKv(raw, new()
        {
            ["ServiceUrl"] = data.S3ServiceUrl,
            ["AccessKey"] = data.S3AccessKey,
            ["SecretKey"] = data.S3SecretKey,
            ["ObjectBucketName"] = data.S3ObjectBucketName,
            ["RecycleBucketName"] = data.S3RecycleBucketName,
        }));

        SetEntry(doc, data, "defPackagesWorkingCopyPath", _ => data.DefPackagesWorkingCopyPath);
        SetEntry(doc, data, "tempDirectoryPath", _ => data.TempDirectoryPath);
        SetEntry(doc, data, "sourceControlAuthPath", _ => data.SourceControlAuthPath);

        doc.Save(path);
    }

    private static void RemoveEntry(XmlDocument doc, ConnectionStringsData data, string name)
    {
        if (!data.RawEntries.ContainsKey(name))
        {
            return;
        }
        var node = doc.SelectSingleNode($"/connectionStrings/add[@name='{name}']");
        node?.ParentNode?.RemoveChild(node);
        data.RawEntries.Remove(name);
    }

    private static bool EnsureEntry(ConnectionStringsData data, string name, bool filled, string template = "")
    {
        if (filled && !data.RawEntries.ContainsKey(name))
        {
            data.RawEntries[name] = template;
            return true;
        }
        return false;
    }

    private static string DbTemplate(string dbType)
    {
        return dbType switch
        {
            "PostgreSQL" => "Server=;Port=5432;Database=;User ID=;password=;Timeout=500; CommandTimeout=400;MaxPoolSize=1024;",
            "Oracle" => "Data Source=;User Id=;Password=;Statement Cache Size = 300",
            _ => "Data Source=; Initial Catalog=; Persist Security Info=True; MultipleActiveResultSets=True; User ID=; Password=; Pooling = true;",
        };
    }

    private static string DetectDbTypeFromSiteConfig(string sitePath)
    {
        var configPath = CreatioSiteLayout.FindExistingRootConfigPath(sitePath);
        if (configPath is null)
        {
            return "";
        }

        try
        {
            var doc = new XmlDocument();
            doc.Load(configPath);
            var executor = doc.SelectSingleNode("//db/general/@executorType")?.Value ?? "";
            if (executor.Contains("MSSql", StringComparison.OrdinalIgnoreCase))
            {
                return "MS SQL Server";
            }
            if (executor.Contains("PostgreSql", StringComparison.OrdinalIgnoreCase))
            {
                return "PostgreSQL";
            }
            if (executor.Contains("Oracle", StringComparison.OrdinalIgnoreCase))
            {
                return "Oracle";
            }
        }
        catch
        {
        }
        return "";
    }

    private static void ReadDb(ConnectionStringsData data, Dictionary<string, string> db)
    {
        if (db.TryGetValue("Data Source", out var dataSource))
        {
            data.DbType = dataSource.Contains('(') || dataSource.Contains('/') ? "Oracle" : "MS SQL Server";
            var separator = dataSource.LastIndexOf(',');
            if (separator >= 0 && int.TryParse(dataSource[(separator + 1)..].Trim(), out var port))
            {
                data.DbServer = dataSource[..separator].Trim();
                data.DbPort = port;
            }
            else
            {
                data.DbServer = dataSource.Trim();
            }
        }
        else
        {
            data.DbType = DbServerKeys.Any(db.ContainsKey) ? "PostgreSQL" : "";
            data.DbServer = FindValue(db, DbServerKeys);
            data.DbPort = ParseInt(db.GetValueOrDefault("Port", ""), 0);
        }

        data.DbCatalog = FindValue(db, DbCatalogKeys);
        data.DbUserId = FindValue(db, DbUserKeys);
        data.DbPassword = FindValue(db, DbPasswordKeys);
    }

    private static Dictionary<string, string> BuildDbUpdates(string raw, ConnectionStringsData data)
    {
        var kv = ParseKv(raw);
        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (kv.ContainsKey("Data Source"))
        {
            var appendPort = data.DbPort > 0 && !data.DbServer.Contains('(') && !data.DbServer.Contains('/');
            updates["Data Source"] = appendPort ? $"{data.DbServer},{data.DbPort}" : data.DbServer;
        }
        else
        {
            AddUpdate(kv, updates, DbServerKeys, data.DbServer);
            if (kv.ContainsKey("Port"))
            {
                updates["Port"] = data.DbPort.ToString();
            }
        }

        UpdateIfExists(kv, updates, DbCatalogKeys, data.DbCatalog);
        AddUpdate(kv, updates, DbUserKeys, data.DbUserId);
        AddUpdate(kv, updates, DbPasswordKeys, data.DbPassword);
        return updates;
    }

    private static void AddUpdate(Dictionary<string, string> existing, Dictionary<string, string> updates, string[] synonyms, string value)
    {
        var key = synonyms.FirstOrDefault(existing.ContainsKey);
        if (key is not null)
        {
            updates[key] = value;
        }
        else if (value.Length > 0)
        {
            updates[synonyms[0]] = value;
        }
    }

    private static void UpdateIfExists(Dictionary<string, string> existing, Dictionary<string, string> updates, string[] synonyms, string value)
    {
        var key = synonyms.FirstOrDefault(existing.ContainsKey);
        if (key is not null)
        {
            updates[key] = value;
        }
    }

    private static string FindValue(Dictionary<string, string> kv, string[] synonyms)
    {
        var key = synonyms.FirstOrDefault(kv.ContainsKey);
        return key is not null ? kv[key] : "";
    }

    private static void SetEntry(XmlDocument doc, ConnectionStringsData data, string name, Func<string, string> build)
    {
        if (!data.RawEntries.TryGetValue(name, out var raw))
        {
            return;
        }

        var node = doc.SelectSingleNode($"/connectionStrings/add[@name='{name}']") as XmlElement;
        if (node is null)
        {
            var root = doc.SelectSingleNode("/connectionStrings");
            if (root is null)
            {
                return;
            }
            node = doc.CreateElement("add");
            node.SetAttribute("name", name);
            root.AppendChild(node);
        }

        var value = build(raw);
        node.SetAttribute("connectionString", value);
        data.RawEntries[name] = value;
    }

    private static int ParseInt(string value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static List<string> SplitParts(string value)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        foreach (var ch in value)
        {
            if (quote != '\0')
            {
                current.Append(ch);
                if (ch == quote)
                {
                    quote = '\0';
                }
            }
            else if (ch is '\'' or '"')
            {
                quote = ch;
                current.Append(ch);
            }
            else if (ch == ';')
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        parts.Add(current.ToString());
        return parts;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0])
        {
            var quote = value[0];
            var inner = value[1..^1];
            return quote == '\'' ? inner.Replace("''", "'") : inner.Replace("\"\"", "\"");
        }
        return value;
    }

    private static string QuoteIfNeeded(string value)
    {
        if (value.Contains(';') || value.StartsWith('\'') || value.StartsWith('"'))
        {
            return "'" + value.Replace("'", "''") + "'";
        }
        return value;
    }

    private static Dictionary<string, string> ParseKv(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in SplitParts(value))
        {
            var trimmed = part.Trim();
            var idx = trimmed.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }
            result[trimmed[..idx].Trim()] = Unquote(trimmed[(idx + 1)..].Trim());
        }
        return result;
    }

    private static string RemoveKeys(string raw, string[] keys)
    {
        var remove = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        var trailingSemicolon = raw.TrimEnd().EndsWith(';');
        foreach (var part in SplitParts(raw))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            var idx = trimmed.IndexOf('=');
            var key = idx <= 0 ? trimmed : trimmed[..idx].Trim();
            if (idx > 0 && remove.Contains(key))
            {
                continue;
            }
            parts.Add(trimmed);
        }
        var result = string.Join("; ", parts);
        return trailingSemicolon && result.Length > 0 ? result + ";" : result;
    }

    private static string ExtractExtras(string raw, string[] knownKeys)
    {
        var known = new HashSet<string>(knownKeys, StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        foreach (var part in SplitParts(raw))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            var idx = trimmed.IndexOf('=');
            var key = idx <= 0 ? trimmed : trimmed[..idx].Trim();
            if (!known.Contains(key))
            {
                parts.Add(trimmed);
            }
        }
        return string.Join("; ", parts);
    }

    private static string ApplyExtras(string merged, string[] knownKeys, string extrasText)
    {
        var known = new HashSet<string>(knownKeys, StringComparer.OrdinalIgnoreCase);

        var desired = new List<(string Key, string? Value)>();
        var desiredMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in SplitParts(extrasText))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            var idx = trimmed.IndexOf('=');
            var key = idx <= 0 ? trimmed : trimmed[..idx].Trim();
            var value = idx <= 0 ? null : Unquote(trimmed[(idx + 1)..].Trim());
            if (known.Contains(key) || desiredMap.ContainsKey(key))
            {
                continue;
            }
            desiredMap[key] = value;
            desired.Add((key, value));
        }

        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        var trailingSemicolon = merged.TrimEnd().EndsWith(';');

        foreach (var part in SplitParts(merged))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            var idx = trimmed.IndexOf('=');
            var key = idx <= 0 ? trimmed : trimmed[..idx].Trim();
            if (idx > 0 && known.Contains(key))
            {
                parts.Add(trimmed);
                continue;
            }
            if (desiredMap.TryGetValue(key, out var newValue))
            {
                var originalValue = idx <= 0 ? null : Unquote(trimmed[(idx + 1)..].Trim());
                if (originalValue == newValue)
                {
                    parts.Add(trimmed);
                }
                else
                {
                    parts.Add(newValue is null ? key : $"{key}={QuoteIfNeeded(newValue)}");
                }
                applied.Add(key);
            }
        }

        foreach (var (key, value) in desired)
        {
            if (!applied.Contains(key))
            {
                parts.Add(value is null ? key : $"{key}={QuoteIfNeeded(value)}");
            }
        }

        var result = string.Join("; ", parts);
        return trailingSemicolon ? result + ";" : result;
    }

    private static string MergeKv(string raw, Dictionary<string, string> updates)
    {
        var pending = new Dictionary<string, string>(updates, StringComparer.OrdinalIgnoreCase);
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        var trailingSemicolon = raw.TrimEnd().EndsWith(';');

        foreach (var part in SplitParts(raw))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var idx = trimmed.IndexOf('=');
            if (idx <= 0)
            {
                parts.Add(trimmed);
                continue;
            }

            var key = trimmed[..idx].Trim();
            if (pending.TryGetValue(key, out var newValue))
            {
                parts.Add($"{key}={QuoteIfNeeded(newValue)}");
                applied.Add(key);
            }
            else
            {
                parts.Add(trimmed);
            }
        }

        foreach (var kv in pending)
        {
            if (!applied.Contains(kv.Key) && kv.Value.Length > 0)
            {
                parts.Add($"{kv.Key}={QuoteIfNeeded(kv.Value)}");
            }
        }

        var result = string.Join("; ", parts);
        return trailingSemicolon ? result + ";" : result;
    }

    private static void ParseAmqp(string value, ConnectionStringsData data)
    {
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return;
        }

        data.MqParsed = true;

        var rest = value[(schemeEnd + 3)..];
        var queryIdx = rest.IndexOf('?');
        if (queryIdx >= 0)
        {
            rest = rest[..queryIdx];
        }

        var atIdx = rest.LastIndexOf('@');
        var userInfo = atIdx >= 0 ? rest[..atIdx] : "";
        var hostPart = atIdx >= 0 ? rest[(atIdx + 1)..] : rest;

        var colonIdx = userInfo.IndexOf(':');
        data.MqUser = Uri.UnescapeDataString(colonIdx >= 0 ? userInfo[..colonIdx] : userInfo);
        data.MqPassword = colonIdx >= 0 ? Uri.UnescapeDataString(userInfo[(colonIdx + 1)..]) : "";

        var slashIdx = hostPart.IndexOf('/');
        var hostWithPort = slashIdx >= 0 ? hostPart[..slashIdx] : hostPart;
        data.MqVirtualHost = slashIdx >= 0 ? Uri.UnescapeDataString(hostPart[(slashIdx + 1)..]) : "";

        var hostColonIdx = hostWithPort.LastIndexOf(':');
        if (hostColonIdx >= 0 && int.TryParse(hostWithPort[(hostColonIdx + 1)..], out var port))
        {
            data.MqHost = hostWithPort[..hostColonIdx];
            data.MqPort = port;
        }
        else
        {
            data.MqHost = hostWithPort;
        }
    }

    private static string BuildAmqp(string raw, ConnectionStringsData data)
    {
        var schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
        var scheme = raw[..schemeEnd];

        var query = "";
        var queryIdx = raw.IndexOf('?');
        if (queryIdx >= 0)
        {
            query = raw[queryIdx..];
        }

        var rest = queryIdx >= 0 ? raw[(schemeEnd + 3)..queryIdx] : raw[(schemeEnd + 3)..];
        var atIdx = rest.LastIndexOf('@');
        var rawHadSlash = (atIdx >= 0 ? rest[(atIdx + 1)..] : rest).Contains('/');

        var user = Uri.EscapeDataString(data.MqUser);
        var password = Uri.EscapeDataString(data.MqPassword);
        var userInfo = user.Length > 0 || password.Length > 0 ? $"{user}:{password}@" : "";
        var port = data.MqPort > 0 ? $":{data.MqPort}" : "";

        var vhostPart = data.MqVirtualHost.Length > 0
            ? "/" + Uri.EscapeDataString(data.MqVirtualHost)
            : rawHadSlash ? "/" : "";

        return $"{scheme}://{userInfo}{data.MqHost}{port}{vhostPart}{query}";
    }
}
