using System.Xml;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Utils;

namespace CreatioHelper.Infrastructure.Services;

public class WebConfigEditor : IWebConfigEditor
{
    public WebConfigData Read(string sitePath)
    {
        var data = new WebConfigData();
        if (CreatioSiteLayout.IsDotNetCore(sitePath))
        {
            data.IsNetCore = true;
            ReadCoreConfig(Path.Combine(sitePath, CreatioSiteLayout.CoreConfigFileName), data);
            ReadCoreRootConfig(Path.Combine(sitePath, CreatioSiteLayout.FrameworkConfigFileName), data);
        }
        else
        {
            ReadRootConfig(Path.Combine(sitePath, CreatioSiteLayout.FrameworkConfigFileName), data);
            ReadAppConfig(Path.Combine(CreatioSiteLayout.GetWebAppPath(sitePath), CreatioSiteLayout.FrameworkConfigFileName), data);
        }
        return data;
    }

    public void Write(string sitePath, WebConfigData data)
    {
        if (data.IsNetCore)
        {
            WriteCoreConfig(Path.Combine(sitePath, CreatioSiteLayout.CoreConfigFileName), data);
            WriteCoreRootConfig(Path.Combine(sitePath, CreatioSiteLayout.FrameworkConfigFileName), data);
        }
        else
        {
            WriteRootConfig(Path.Combine(sitePath, CreatioSiteLayout.FrameworkConfigFileName), data);
            WriteAppConfig(Path.Combine(CreatioSiteLayout.GetWebAppPath(sitePath), CreatioSiteLayout.FrameworkConfigFileName), data);
        }
    }

    private const string RetryRedisOperationKey = "Feature-UseRetryRedisOperation";

    public bool? ReadRetryRedisOperation(string sitePath)
    {
        var filePath = ResolveAppSettingsPath(sitePath);
        if (filePath is null)
        {
            return null;
        }

        var doc = new XmlDocument();
        doc.Load(filePath);
        var node = doc.SelectSingleNode($"/configuration/appSettings/add[@key='{RetryRedisOperationKey}']");
        var value = node?.Attributes?.GetNamedItem("value")?.InnerText;
        return bool.TryParse(value, out var enabled) ? enabled : null;
    }

    public void WriteRetryRedisOperation(string sitePath, bool enabled)
    {
        var filePath = ResolveAppSettingsPath(sitePath);
        if (filePath is null)
        {
            return;
        }

        var doc = new XmlDocument();
        doc.Load(filePath);

        var appSettings = doc.SelectSingleNode("/configuration/appSettings");
        if (appSettings is null)
        {
            var configuration = doc.SelectSingleNode("/configuration");
            if (configuration is null)
            {
                return;
            }
            appSettings = configuration.AppendChild(doc.CreateElement("appSettings"))!;
        }

        if (appSettings.SelectSingleNode($"add[@key='{RetryRedisOperationKey}']") is not XmlElement node)
        {
            node = doc.CreateElement("add");
            node.SetAttribute("key", RetryRedisOperationKey);
            appSettings.AppendChild(node);
        }

        node.SetAttribute("value", enabled ? "true" : "false");
        doc.Save(filePath);
    }

    public string? GetRedisSectionFileName(string sitePath)
    {
        var filePath = ResolveAppSettingsPath(sitePath);
        return filePath is null ? null : Path.GetFileName(filePath);
    }

    public IReadOnlyList<KeyValuePair<string, string>>? ReadRedisSection(string sitePath)
    {
        var filePath = ResolveAppSettingsPath(sitePath);
        if (filePath is null)
        {
            return null;
        }

        var doc = new XmlDocument();
        doc.Load(filePath);
        if (FindRedisSection(doc) is not XmlElement redis)
        {
            return null;
        }

        var result = new List<KeyValuePair<string, string>>();
        foreach (XmlAttribute attribute in redis.Attributes)
        {
            result.Add(new KeyValuePair<string, string>(attribute.Name, attribute.Value));
        }
        return result;
    }

    public void WriteRedisSection(string sitePath, IReadOnlyList<KeyValuePair<string, string>> attributes)
    {
        var filePath = ResolveAppSettingsPath(sitePath);
        if (filePath is null)
        {
            return;
        }

        var doc = new XmlDocument();
        doc.Load(filePath);
        if (FindRedisSection(doc) is not XmlElement redis)
        {
            return;
        }

        foreach (var attribute in attributes)
        {
            redis.SetAttribute(attribute.Key, attribute.Value);
        }
        doc.Save(filePath);
    }

    private static XmlNode? FindRedisSection(XmlDocument doc)
        => doc.SelectSingleNode("/configuration/terrasoft/redis") ?? doc.SelectSingleNode("//redis");

    private static string? ResolveAppSettingsPath(string sitePath)
        => CreatioSiteLayout.FindExistingRootConfigPath(sitePath);

    private static void ReadRootConfig(string filePath, WebConfigData data)
    {
        var doc = new XmlDocument();
        doc.Load(filePath);

        data.CurrentSchemaName = doc
            .SelectSingleNode("/configuration/terrasoft/db/general")!
            .Attributes!.GetNamedItem("currentSchemaName")!.InnerText;

        data.ProviderNames = doc
            .SelectSingleNode("/configuration/terrasoft/auth")!
            .Attributes!.GetNamedItem("providerNames")!.InnerText;

        data.AutoLoginProviderNames = doc
            .SelectSingleNode("/configuration/terrasoft/auth")!
            .Attributes!.GetNamedItem("autoLoginProviderNames")?.InnerText ?? "";

        bool.TryParse(doc
            .SelectSingleNode("/configuration/terrasoft/fileDesignMode")!
            .Attributes!.GetNamedItem("enabled")!.InnerText, out var fileDesignMode);
        data.FileDesignMode = fileDesignMode;

        data.BehaviorsConfigSource = doc
            .SelectSingleNode("/configuration/system.serviceModel/behaviors")!
            .Attributes!.GetNamedItem("configSource")!.InnerText;

        data.BindingsConfigSource = doc
            .SelectSingleNode("/configuration/system.serviceModel/bindings")!
            .Attributes!.GetNamedItem("configSource")!.InnerText;

        var ldapProviders = doc.SelectSingleNode("/configuration/terrasoft/auth/providers");
        if (ldapProviders != null)
        {
            foreach (XmlNode provider in ldapProviders)
            {
                if (provider.Attributes?.GetNamedItem("name")?.InnerText != "Ldap")
                    continue;

                // Structure: provider → <parameters> (or similar group) → <add name="..." value="..."/>
                foreach (XmlNode paramGroup in provider)
                {
                    foreach (XmlNode node in paramGroup)
                    {
                        var name = node.Attributes?.GetNamedItem("name")?.InnerText;
                        var value = node.Attributes?.GetNamedItem("value")?.InnerText ?? "";
                        if (name == "ServerPath")
                            data.ServerPath = value;
                        else if (name == "DistinguishedName")
                            data.DistinguishedName = value;
                    }
                }
            }
        }

        var appSettings = doc.SelectSingleNode("/configuration/appSettings");
        if (appSettings != null)
        {
            foreach (XmlNode node in appSettings)
            {
                var key = node.Attributes?.GetNamedItem("key")?.InnerText;
                var val = node.Attributes?.GetNamedItem("value")?.InnerText ?? "";
                if (key == "UsePathThroughAuthentication" && bool.TryParse(val, out var passThrough))
                    data.UsePathThroughAuthentication = passThrough;
                else if (key == "UseStaticFileContent" && bool.TryParse(val, out var staticContent))
                    data.UseStaticFileContent = staticContent;
            }
        }

        var firstQuartz = doc.SelectSingleNode("/configuration/quartzConfig/quartz");
        if (bool.TryParse(firstQuartz?.Attributes?.GetNamedItem("isActive")?.InnerText, out var quartzActive))
            data.QuartzEnabled = quartzActive;
    }

    private static void ReadAppConfig(string filePath, WebConfigData data)
    {
        if (!File.Exists(filePath))
            return;

        var doc = new XmlDocument();
        doc.Load(filePath);

        data.AppServicesSource = doc
            .SelectSingleNode("/configuration/system.serviceModel/services")!
            .Attributes?.GetNamedItem("configSource")?.InnerText ?? "";

        var wsService = doc.SelectSingleNode("/configuration/terrasoft/wsService");
        if (wsService?.Attributes != null)
        {
            if (int.TryParse(wsService.Attributes.GetNamedItem("portForClientConnection")?.InnerText, out var port))
                data.PortForClientConnection = port;
            // HTTPS is truly on only when all three configSource paths contain \https\ AND encrypted=true
            if (bool.TryParse(wsService.Attributes.GetNamedItem("encrypted")?.InnerText, out var enc))
                data.HttpsEncrypted = enc
                    && data.BehaviorsConfigSource.Contains("\\https\\")
                    && data.BindingsConfigSource.Contains("\\https\\")
                    && data.AppServicesSource.Contains("\\https\\");
        }

        var defaultProxy = doc.SelectSingleNode("/configuration/system.net/defaultProxy");
        if (defaultProxy != null)
        {
            bool.TryParse(defaultProxy.Attributes?.GetNamedItem("enabled")?.InnerText, out var proxyEnabled);
            data.EnableProxy = proxyEnabled;
            data.ProxyAddress = doc
                .SelectSingleNode("/configuration/system.net/defaultProxy/proxy")
                ?.Attributes?.GetNamedItem("proxyaddress")?.InnerText ?? "";
        }
    }

    private static void WriteRootConfig(string filePath, WebConfigData data)
    {
        var doc = new XmlDocument();
        doc.Load(filePath);

        doc.SelectSingleNode("/configuration/terrasoft/db/general")!
            .Attributes!.GetNamedItem("currentSchemaName")!.Value = data.CurrentSchemaName;

        doc.SelectSingleNode("/configuration/terrasoft/auth")!
            .Attributes!.GetNamedItem("providerNames")!.Value = data.ProviderNames;

        var autoLoginAttr = doc.SelectSingleNode("/configuration/terrasoft/auth")!
            .Attributes!.GetNamedItem("autoLoginProviderNames");
        if (autoLoginAttr != null)
        {
            autoLoginAttr.Value = data.AutoLoginProviderNames;
        }

        doc.SelectSingleNode("/configuration/terrasoft/fileDesignMode")!
            .Attributes!.GetNamedItem("enabled")!.Value = data.FileDesignMode ? "true" : "false";

        var providers = doc.SelectSingleNode("/configuration/terrasoft/auth/providers");
        if (providers != null)
        {
            foreach (XmlNode provider in providers)
            {
                var provName = provider.Attributes?.GetNamedItem("name")?.InnerText;
                if (provName != "Ldap" && provName != "SSPLdapProvider")
                    continue;

                // Structure: provider → <parameters> group → <add name="..." value="..."/>
                foreach (XmlNode paramGroup in provider)
                {
                    foreach (XmlNode node in paramGroup)
                    {
                        var name = node.Attributes?.GetNamedItem("name")?.InnerText;
                        var valueAttr = node.Attributes?.GetNamedItem("value");
                        if (valueAttr == null) continue;
                        if (name == "ServerPath")
                            valueAttr.Value = data.ServerPath;
                        else if (name == "DistinguishedName")
                            valueAttr.Value = data.DistinguishedName;
                    }
                }
            }
        }

        var appSettings = doc.SelectSingleNode("/configuration/appSettings");
        if (appSettings != null)
        {
            foreach (XmlNode node in appSettings)
            {
                var key = node.Attributes?.GetNamedItem("key")?.InnerText;
                if (key == "UsePathThroughAuthentication")
                {
                    var attr = node.Attributes?.GetNamedItem("value");
                    if (attr != null) { attr.Value = data.UsePathThroughAuthentication ? "true" : "false"; }
                }
                else if (key == "UseStaticFileContent")
                {
                    var attr = node.Attributes?.GetNamedItem("value");
                    if (attr != null) { attr.Value = data.UseStaticFileContent ? "true" : "false"; }
                }
            }
        }

        var behaviorsAttr = doc.SelectSingleNode("/configuration/system.serviceModel/behaviors")!
            .Attributes?.GetNamedItem("configSource");
        var bindingsAttr = doc.SelectSingleNode("/configuration/system.serviceModel/bindings")!
            .Attributes?.GetNamedItem("configSource");

        if (behaviorsAttr != null)
        {
            behaviorsAttr.Value = data.HttpsEncrypted
                ? data.BehaviorsConfigSource.Replace("\\http\\", "\\https\\")
                : data.BehaviorsConfigSource.Replace("\\https\\", "\\http\\");
        }

        if (bindingsAttr != null)
        {
            bindingsAttr.Value = data.HttpsEncrypted
                ? data.BindingsConfigSource.Replace("\\http\\", "\\https\\")
                : data.BindingsConfigSource.Replace("\\https\\", "\\http\\");
        }

        var quartzNodes = doc.SelectNodes("/configuration/quartzConfig/quartz");
        if (quartzNodes != null)
        {
            foreach (XmlNode node in quartzNodes)
            {
                var attr = node.Attributes?.GetNamedItem("isActive");
                if (attr != null) { attr.Value = data.QuartzEnabled ? "true" : "false"; }
            }
        }

        SetProxy(doc, data);
        doc.Save(filePath);
    }

    private static void WriteAppConfig(string filePath, WebConfigData data)
    {
        if (!File.Exists(filePath))
            return;

        var doc = new XmlDocument();
        doc.Load(filePath);

        var wsService = doc.SelectSingleNode("/configuration/terrasoft/wsService");
        if (wsService?.Attributes != null)
        {
            var portAttr = wsService.Attributes.GetNamedItem("portForClientConnection");
            if (portAttr != null) portAttr.Value = data.PortForClientConnection.ToString();
            var encAttr = wsService.Attributes.GetNamedItem("encrypted");
            if (encAttr != null) { encAttr.Value = data.HttpsEncrypted ? "true" : "false"; }
        }

        var servicesAttr = doc.SelectSingleNode("/configuration/system.serviceModel/services")
            ?.Attributes?.GetNamedItem("configSource");
        if (servicesAttr != null)
        {
            servicesAttr.Value = data.HttpsEncrypted
                ? data.AppServicesSource.Replace("\\http\\", "\\https\\")
                : data.AppServicesSource.Replace("\\https\\", "\\http\\");
        }

        SetProxy(doc, data);
        doc.Save(filePath);
    }

    private static void ReadCoreConfig(string filePath, WebConfigData data)
    {
        var doc = new XmlDocument();
        doc.Load(filePath);

        data.CurrentSchemaName = doc
            .SelectSingleNode("/configuration/terrasoft/db/general")!
            .Attributes!.GetNamedItem("currentSchemaName")!.InnerText;

        data.ProviderNames = doc
            .SelectSingleNode("/configuration/terrasoft/auth")!
            .Attributes!.GetNamedItem("providerNames")!.InnerText;

        data.AutoLoginProviderNames = doc
            .SelectSingleNode("/configuration/terrasoft/auth")!
            .Attributes!.GetNamedItem("autoLoginProviderNames")?.InnerText ?? "";

        bool.TryParse(doc
            .SelectSingleNode("/configuration/terrasoft/fileDesignMode")!
            .Attributes!.GetNamedItem("enabled")!.InnerText, out var fileDesignMode);
        data.FileDesignMode = fileDesignMode;

        var ldapProviders = doc.SelectSingleNode("/configuration/terrasoft/auth/providers");
        if (ldapProviders != null)
        {
            foreach (XmlNode provider in ldapProviders)
            {
                var name = provider.Attributes?.GetNamedItem("name")?.InnerText;
                if (name != "LdapNtlmProviderOnWindows" && name != "LdapProvider" && name != "SSPLdapProvider")
                    continue;

                foreach (XmlNode paramGroup in provider)
                {
                    foreach (XmlNode node in paramGroup)
                    {
                        var paramName = node.Attributes?.GetNamedItem("name")?.InnerText;
                        var value = node.Attributes?.GetNamedItem("value")?.InnerText ?? "";
                        if (paramName == "ServerPath" && string.IsNullOrEmpty(data.ServerPath))
                            data.ServerPath = value;
                        else if (paramName == "DistinguishedName" && string.IsNullOrEmpty(data.DistinguishedName))
                            data.DistinguishedName = value;
                    }
                }
            }
        }

        var appSettings = doc.SelectSingleNode("/configuration/appSettings");
        if (appSettings != null)
        {
            foreach (XmlNode node in appSettings)
            {
                var key = node.Attributes?.GetNamedItem("key")?.InnerText;
                var val = node.Attributes?.GetNamedItem("value")?.InnerText ?? "";
                if (key == "UsePathThroughAuthentication" && bool.TryParse(val, out var passThrough))
                    data.UsePathThroughAuthentication = passThrough;
                else if (key == "UseStaticFileContent" && bool.TryParse(val, out var staticContent))
                    data.UseStaticFileContent = staticContent;
            }
        }

        var firstQuartz = doc.SelectSingleNode("/configuration/quartzConfig/quartz");
        if (bool.TryParse(firstQuartz?.Attributes?.GetNamedItem("isActive")?.InnerText, out var quartzActive))
            data.QuartzEnabled = quartzActive;
    }

    private static void ReadCoreRootConfig(string filePath, WebConfigData data)
    {
        if (!File.Exists(filePath))
            return;

        var doc = new XmlDocument();
        doc.Load(filePath);

        var defaultProxy = doc.SelectSingleNode("/configuration/system.net/defaultProxy");
        if (defaultProxy != null)
        {
            bool.TryParse(defaultProxy.Attributes?.GetNamedItem("enabled")?.InnerText, out var proxyEnabled);
            data.EnableProxy = proxyEnabled;
            data.ProxyAddress = doc
                .SelectSingleNode("/configuration/system.net/defaultProxy/proxy")
                ?.Attributes?.GetNamedItem("proxyaddress")?.InnerText ?? "";
        }
    }

    private static void WriteCoreConfig(string filePath, WebConfigData data)
    {
        var doc = new XmlDocument();
        doc.Load(filePath);

        doc.SelectSingleNode("/configuration/terrasoft/db/general")!
            .Attributes!.GetNamedItem("currentSchemaName")!.Value = data.CurrentSchemaName;

        doc.SelectSingleNode("/configuration/terrasoft/auth")!
            .Attributes!.GetNamedItem("providerNames")!.Value = data.ProviderNames;

        var autoLoginAttr = doc.SelectSingleNode("/configuration/terrasoft/auth")!
            .Attributes!.GetNamedItem("autoLoginProviderNames");
        if (autoLoginAttr != null)
        {
            autoLoginAttr.Value = data.AutoLoginProviderNames;
        }

        doc.SelectSingleNode("/configuration/terrasoft/fileDesignMode")!
            .Attributes!.GetNamedItem("enabled")!.Value = data.FileDesignMode ? "true" : "false";

        var providers = doc.SelectSingleNode("/configuration/terrasoft/auth/providers");
        if (providers != null)
        {
            foreach (XmlNode provider in providers)
            {
                var provName = provider.Attributes?.GetNamedItem("name")?.InnerText;
                if (provName != "LdapNtlmProviderOnWindows" && provName != "LdapProvider" && provName != "SSPLdapProvider")
                    continue;

                foreach (XmlNode paramGroup in provider)
                {
                    foreach (XmlNode node in paramGroup)
                    {
                        var name = node.Attributes?.GetNamedItem("name")?.InnerText;
                        var valueAttr = node.Attributes?.GetNamedItem("value");
                        if (valueAttr == null) continue;
                        if (name == "ServerPath")
                            valueAttr.Value = data.ServerPath;
                        else if (name == "DistinguishedName")
                            valueAttr.Value = data.DistinguishedName;
                    }
                }
            }
        }

        var appSettings = doc.SelectSingleNode("/configuration/appSettings");
        if (appSettings != null)
        {
            foreach (XmlNode node in appSettings)
            {
                var key = node.Attributes?.GetNamedItem("key")?.InnerText;
                if (key == "UsePathThroughAuthentication")
                {
                    var attr = node.Attributes?.GetNamedItem("value");
                    if (attr != null) { attr.Value = data.UsePathThroughAuthentication ? "true" : "false"; }
                }
                else if (key == "UseStaticFileContent")
                {
                    var attr = node.Attributes?.GetNamedItem("value");
                    if (attr != null) { attr.Value = data.UseStaticFileContent ? "true" : "false"; }
                }
            }
        }

        var quartzNodes = doc.SelectNodes("/configuration/quartzConfig/quartz");
        if (quartzNodes != null)
        {
            foreach (XmlNode node in quartzNodes)
            {
                var attr = node.Attributes?.GetNamedItem("isActive");
                if (attr != null) { attr.Value = data.QuartzEnabled ? "true" : "false"; }
            }
        }

        doc.Save(filePath);
    }

    private static void WriteCoreRootConfig(string filePath, WebConfigData data)
    {
        if (!File.Exists(filePath))
            return;

        var doc = new XmlDocument();
        doc.Load(filePath);
        SetProxy(doc, data);
        doc.Save(filePath);
    }

    private static void SetProxy(XmlDocument doc, WebConfigData data)
    {
        var existing = doc.SelectNodes("/configuration/system.net");
        if (existing != null)
        {
            for (var i = existing.Count - 1; i >= 0; i--)
                existing[i]!.ParentNode!.RemoveChild(existing[i]!);
        }

        if (!data.EnableProxy || string.IsNullOrWhiteSpace(data.ProxyAddress))
            return;

        var root = doc.DocumentElement!;
        var systemNet = doc.CreateElement("system.net");
        var defaultProxy = doc.CreateElement("defaultProxy");
        defaultProxy.SetAttribute("enabled", "true");
        defaultProxy.SetAttribute("useDefaultCredentials", "true");
        var proxy = doc.CreateElement("proxy");
        proxy.SetAttribute("bypassonlocal", "False");
        proxy.SetAttribute("proxyaddress", data.ProxyAddress);
        proxy.SetAttribute("usesystemdefault", "False");
        defaultProxy.AppendChild(proxy);
        systemNet.AppendChild(defaultProxy);
        root.AppendChild(systemNet);
    }
}
