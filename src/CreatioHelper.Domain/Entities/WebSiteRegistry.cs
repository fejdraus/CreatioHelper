using System;
using System.Collections.Generic;
namespace CreatioHelper.Domain.Entities;

public class WebSiteRegistry
{
    public List<WebSiteInfo> Sites { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = "1.0";
}