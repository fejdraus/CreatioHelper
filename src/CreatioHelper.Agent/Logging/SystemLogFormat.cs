using Serilog.Events;

namespace CreatioHelper.Agent.Logging;

public static class SystemLogFormat
{
    public static string Abbreviate(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "INF"
    };

    public static string ParseLevel(string abbreviation) => abbreviation.ToUpperInvariant() switch
    {
        "VRB" => "verbose",
        "DBG" => "debug",
        "INF" => "info",
        "WRN" => "warning",
        "ERR" => "error",
        "FTL" => "fatal",
        _ => "info"
    };

    public static string LevelToAbbreviation(string level) => level.ToLowerInvariant() switch
    {
        "verbose" => "VRB",
        "debug" => "DBG",
        "info" => "INF",
        "warning" => "WRN",
        "error" => "ERR",
        "fatal" => "FTL",
        _ => ""
    };

    public static string ExtractFacility(string message)
    {
        if (message.Contains("SyncEngine") || message.Contains("Sync:"))
            return "sync";
        if (message.Contains("Connection") || message.Contains("Device"))
            return "connections";
        if (message.Contains("Database") || message.Contains("DB") || message.Contains("Index"))
            return "db";
        if (message.Contains("API") || message.Contains("Controller") || message.Contains("Request"))
            return "api";
        if (message.Contains("Model") || message.Contains("Config"))
            return "model";
        if (message.Contains("Auth") || message.Contains("Login") || message.Contains("JWT"))
            return "auth";
        if (message.Contains("File") || message.Contains("Folder"))
            return "fs";
        if (message.Contains("Network") || message.Contains("NAT") || message.Contains("UPnP"))
            return "network";

        return "app";
    }
}
