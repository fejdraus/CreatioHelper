using System;
using System.IO;
using System.Xml;
using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Configuration;

public class SiteConfigEditor : ISiteConfigEditor
{
    public void UpdateConnectionString(string configPath, string name, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(configPath)) throw new ArgumentNullException(nameof(configPath));
        if (!File.Exists(configPath)) throw new FileNotFoundException("Config file not found", configPath);

        var xmlDoc = new XmlDocument();
        xmlDoc.Load(configPath);

        var node = xmlDoc.SelectSingleNode($"/connectionStrings/add[@name='{name}']") as XmlElement;
        if (node != null)
        {
            node.SetAttribute("connectionString", connectionString);
            xmlDoc.Save(configPath);
        }
    }
}
