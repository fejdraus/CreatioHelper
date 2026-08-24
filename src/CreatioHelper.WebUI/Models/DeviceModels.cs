using System.Text.Json.Serialization;

namespace CreatioHelper.WebUI.Models;

/// <summary>
/// Device configuration
/// </summary>
public class DeviceConfig
{
    [JsonPropertyName("deviceID")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("addresses")]
    public string[] Addresses { get; set; } = ["dynamic"];

    [JsonPropertyName("compression")]
    public string Compression { get; set; } = "metadata";

    [JsonPropertyName("certName")]
    public string CertName { get; set; } = string.Empty;

    [JsonPropertyName("introducer")]
    public bool Introducer { get; set; }

    [JsonPropertyName("skipIntroductionRemovals")]
    public bool SkipIntroductionRemovals { get; set; }

    [JsonPropertyName("introducedBy")]
    public string IntroducedBy { get; set; } = string.Empty;

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    [JsonPropertyName("allowedNetworks")]
    public string[] AllowedNetworks { get; set; } = [];

    [JsonPropertyName("autoAcceptFolders")]
    public bool AutoAcceptFolders { get; set; }

    [JsonPropertyName("maxSendKbps")]
    public int MaxSendKbps { get; set; }

    [JsonPropertyName("maxRecvKbps")]
    public int MaxRecvKbps { get; set; }

    [JsonPropertyName("ignoredFolders")]
    public IgnoredFolder[] IgnoredFolders { get; set; } = [];

    [JsonPropertyName("maxRequestKiB")]
    public int MaxRequestKiB { get; set; }

    [JsonPropertyName("untrusted")]
    public bool Untrusted { get; set; }

    [JsonPropertyName("remoteGUIPort")]
    public int RemoteGuiPort { get; set; }

    [JsonPropertyName("numConnections")]
    public int NumConnections { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Name) ? ShortId : Name;
    public string ShortId => DeviceId.Length > 7 ? DeviceId[..7] : DeviceId;
}

public class IgnoredFolder
{
    [JsonPropertyName("time")]
    public DateTime Time { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// Device statistics from /rest/stats/device
/// </summary>
public class DeviceStats
{
    [JsonPropertyName("lastSeen")]
    public DateTime? LastSeen { get; set; }

    [JsonPropertyName("lastConnectionDurationS")]
    public double? LastConnectionDurationS { get; set; }
}

/// <summary>
/// Connection information from /rest/system/connections
/// </summary>
public class ConnectionInfo
{
    [JsonPropertyName("deviceID")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("crypto")]
    public string Crypto { get; set; } = string.Empty;

    [JsonPropertyName("inBytesTotal")]
    public long InBytesTotal { get; set; }

    [JsonPropertyName("outBytesTotal")]
    public long OutBytesTotal { get; set; }

    [JsonPropertyName("inBytesPerSec")]
    public long InBytesPerSec { get; set; }

    [JsonPropertyName("outBytesPerSec")]
    public long OutBytesPerSec { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("connectedAt")]
    public DateTime? ConnectedAt { get; set; }

    [JsonPropertyName("at")]
    public DateTime At { get; set; }

    public ConnectionType ConnectionType => Type switch
    {
        "tcp-client" => ConnectionType.TcpClient,
        "tcp-server" => ConnectionType.TcpServer,
        "quic-client" => ConnectionType.QuicClient,
        "quic-server" => ConnectionType.QuicServer,
        "relay-client" => ConnectionType.RelayClient,
        "relay-server" => ConnectionType.RelayServer,
        _ => ConnectionType.Unknown
    };

    public bool IsRelay => Type.Contains("relay", StringComparison.OrdinalIgnoreCase);

    public TimeSpan? ConnectionDuration => StartedAt.HasValue
        ? DateTime.UtcNow - StartedAt.Value
        : null;
}

/// <summary>
/// Pending device waiting for approval
/// </summary>
public class PendingDevice
{
    [JsonPropertyName("deviceID")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("time")]
    public DateTime Time { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Name) ? ShortId : Name;
    public string ShortId => DeviceId.Length > 7 ? DeviceId[..7] : DeviceId;
}

public enum ConnectionType
{
    Unknown,
    TcpClient,
    TcpServer,
    QuicClient,
    QuicServer,
    RelayClient,
    RelayServer
}
