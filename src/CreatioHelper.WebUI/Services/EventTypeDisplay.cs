using CreatioHelper.WebUI.Models;
using Microsoft.Extensions.Localization;

namespace CreatioHelper.WebUI.Services;

public static class EventTypeDisplay
{
    public static string Format(EventType type, IStringLocalizer localizer)
    {
        return type switch
        {
            EventType.StateChanged => localizer["EventType_State"],
            EventType.ItemStarted => localizer["EventType_Started"],
            EventType.ItemFinished => localizer["EventType_Finished"],
            EventType.FolderCompletion => localizer["EventType_Complete"],
            EventType.DeviceConnected => localizer["EventType_Connected"],
            EventType.DeviceDisconnected => localizer["EventType_Disconnected"],
            EventType.FolderErrors => localizer["EventType_Error"],
            EventType.Unknown => localizer["EventType_Unknown"],
            EventType.Starting => localizer["EventType_Starting"],
            EventType.StartupComplete => localizer["EventType_StartupComplete"],
            EventType.FolderWatchStateChanged => localizer["EventType_WatchState"],
            EventType.ConfigSaved => localizer["EventType_ConfigSaved"],
            EventType.DevicePaused => localizer["EventType_Paused"],
            EventType.DeviceResumed => localizer["EventType_Resumed"],
            EventType.FolderPaused => localizer["EventType_Paused"],
            EventType.FolderResumed => localizer["EventType_Resumed"],
            EventType.LocalChangeDetected => localizer["EventType_LocalChange"],
            EventType.RemoteChangeDetected => localizer["EventType_RemoteChange"],
            EventType.DownloadProgress => localizer["EventType_Downloading"],
            EventType.FolderScanProgress => localizer["EventType_Scanning"],
            EventType.DiscoveryCompleted => localizer["EventType_DiscoveryCompleted"],
            _ => type.ToString()
        };
    }
}
