using CreatioHelper.WebUI.Models;
using Microsoft.Extensions.Localization;

namespace CreatioHelper.WebUI.Services;

public static class FolderDisplay
{
    public static string FormatType(FolderType type, IStringLocalizer localizer)
    {
        return FormatType(type.ToString(), localizer);
    }

    public static string FormatState(string? state, IStringLocalizer localizer)
    {
        return state?.ToLowerInvariant() switch
        {
            "idle" => localizer["FolderState_Idle"],
            "syncing" => localizer["FolderState_Syncing"],
            "scanning" => localizer["FolderState_Scanning"],
            "error" => localizer["FolderState_Error"],
            _ => localizer["FolderState_Unknown"]
        };
    }

    public static string FormatType(string? type, IStringLocalizer localizer)
    {
        return type?.ToLowerInvariant() switch
        {
            "sendreceive" or null => localizer["FolderType_SendReceive"],
            "sendonly" => localizer["FolderType_SendOnly"],
            "receiveonly" => localizer["FolderType_ReceiveOnly"],
            "receiveencrypted" => localizer["FolderType_ReceiveEncrypted"],
            _ => type
        };
    }
}
