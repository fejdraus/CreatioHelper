using System.Text.Json.Serialization;

namespace CreatioHelper.Agent.Services;

internal class SyncthingFolderStatus
{
    [JsonPropertyName("globalBytes")]
    public long GlobalBytes { get; set; }
    [JsonPropertyName("globalDeleted")]
    public long GlobalDeleted { get; set; }
    [JsonPropertyName("globalDirectories")]
    public int GlobalDirectories { get; set; }
    [JsonPropertyName("globalFiles")]
    public int GlobalFiles { get; set; }
    [JsonPropertyName("globalSymlinks")]
    public int GlobalSymlinks { get; set; }
    [JsonPropertyName("globalTotalItems")]
    public int GlobalTotalItems { get; set; }
    [JsonPropertyName("inSyncBytes")]
    public long InSyncBytes { get; set; }
    [JsonPropertyName("inSyncFiles")]
    public int InSyncFiles { get; set; }
    [JsonPropertyName("invalid")]
    public string Invalid { get; set; } = string.Empty;
    [JsonPropertyName("localBytes")]
    public long LocalBytes { get; set; }
    [JsonPropertyName("localDeleted")]
    public long LocalDeleted { get; set; }
    [JsonPropertyName("localDirectories")]
    public int LocalDirectories { get; set; }
    [JsonPropertyName("localFiles")]
    public int LocalFiles { get; set; }
    [JsonPropertyName("localSymlinks")]
    public int LocalSymlinks { get; set; }
    [JsonPropertyName("localTotalItems")]
    public int LocalTotalItems { get; set; }
    [JsonPropertyName("needBytes")]
    public long NeedBytes { get; set; }
    [JsonPropertyName("needDeletes")]
    public int NeedDeletes { get; set; }
    [JsonPropertyName("needDirectories")]
    public int NeedDirectories { get; set; }
    [JsonPropertyName("needFiles")]
    public int NeedFiles { get; set; }
    [JsonPropertyName("needSymlinks")]
    public int NeedSymlinks { get; set; }
    [JsonPropertyName("needTotalItems")]
    public int NeedTotalItems { get; set; }
    [JsonPropertyName("pullErrors")]
    public int PullErrors { get; set; }
    [JsonPropertyName("receiveOnlyChangedBytes")]
    public long ReceiveOnlyChangedBytes { get; set; }
    [JsonPropertyName("receiveOnlyChangedDeletes")]
    public int ReceiveOnlyChangedDeletes { get; set; }
    [JsonPropertyName("receiveOnlyChangedDirectories")]
    public int ReceiveOnlyChangedDirectories { get; set; }
    [JsonPropertyName("receiveOnlyChangedFiles")]
    public int ReceiveOnlyChangedFiles { get; set; }
    [JsonPropertyName("receiveOnlyChangedSymlinks")]
    public int ReceiveOnlyChangedSymlinks { get; set; }
    [JsonPropertyName("receiveOnlyTotalItems")]
    public int ReceiveOnlyTotalItems { get; set; }
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;
    [JsonPropertyName("stateChanged")]
    public DateTime StateChanged { get; set; }
    [JsonPropertyName("version")]
    public long Version { get; set; }
}
