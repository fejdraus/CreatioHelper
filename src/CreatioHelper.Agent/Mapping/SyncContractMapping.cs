using CreatioHelper.Application.Interfaces;
using CreatioHelper.Contracts.Responses;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Agent.Mapping;

public static class SyncContractMapping
{
    public static SyncFolderDto ToDto(this SyncFolder folder, SyncStatus status) => new()
    {
        FolderId = folder.Id,
        Label = folder.Label,
        Path = folder.Path,
        Type = folder.Type,
        IsPaused = folder.IsPaused,
        State = status.State.ToString(),
        GlobalBytes = status.GlobalBytes,
        LocalBytes = status.LocalBytes,
        GlobalFiles = status.GlobalFiles,
        LocalFiles = status.LocalFiles,
        LastScan = status.LastScan,
        LastSync = status.LastSync,
        DeviceIds = folder.Devices.ToList()
    };

    public static SyncDeviceDto ToDto(this SyncDevice device) => new()
    {
        DeviceId = device.DeviceId,
        Name = device.DeviceName,
        IsConnected = device.IsConnected,
        LastSeen = device.LastSeen ?? DateTime.MinValue,
        Status = device.Status.ToString(),
        IsPaused = device.IsPaused,
        Addresses = device.Addresses
    };

    public static SyncSystemStatus ToSystemStatus(this SyncStatistics statistics, IEnumerable<SyncDevice> devices) => new()
    {
        Uptime = statistics.Uptime,
        ConnectedDevices = statistics.ConnectedDevices,
        TotalDevices = statistics.TotalDevices,
        SyncedFolders = statistics.SyncedFolders,
        TotalFolders = statistics.TotalFolders,
        TotalBytesIn = statistics.TotalBytesIn,
        TotalBytesOut = statistics.TotalBytesOut,
        IsOnline = devices.Any(d => d.IsConnected)
    };
}
