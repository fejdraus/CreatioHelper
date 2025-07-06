using System;
using System.Collections.Generic;
namespace CreatioHelper.Core.Models;

public class WebSiteInfo
{
    public string Name { get; set; } = "";           // Отображаемое имя
    public string Type { get; set; } = "";           // IIS, WindowsService, Systemd
    public string ServiceName { get; set; } = "";    // Реальное имя сервиса/сайта
    public bool AutoDiscovered { get; set; }         // true для IIS, false для ручных
    public Dictionary<string, string> Properties { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Unknown";
}