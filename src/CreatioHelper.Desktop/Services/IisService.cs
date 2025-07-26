
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia.Threading;
using CreatioHelper.Core;
using CreatioHelper.Domain.Entities;
using Microsoft.Web.Administration;

namespace CreatioHelper.Services;

[SupportedOSPlatform("windows")]
public class IisService
{
    public static bool IsIisAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;
    
        // Quick check via the registry
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\InetStp");
            if (key == null) return false;
        }
        catch
        {
            return false;
        }
    
        // Check the IIS service
        try
        {
            using var serviceController = new System.ServiceProcess.ServiceController("W3SVC");
            var _ = serviceController.Status; // Just try to retrieve the status
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
    
    public void LoadIisSites(ObservableCollection<IisSiteInfo> iisSites, Action<bool> onCompletion)
    {
        if (!OperatingSystem.IsWindows() || !IsIisAvailable())
        {
            iisSites.Clear();
            onCompletion(false);
            return;
        }
        
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                using var manager = new ServerManager();
                var sites = manager.Sites.ToList();
                iisSites.Clear();
            
                if (sites.Count == 0)
                {
                    onCompletion(true);
                    return;
                }
            
                foreach (var site in manager.Sites)
                {
                    var app = site.Applications.FirstOrDefault(a => a.Path == "/0");
                    var appVdir = app?.VirtualDirectories["/"];
                    var rootApp = site.Applications["/"];
                    var rootVdir = rootApp?.VirtualDirectories["/"];
                    string sitePath = rootVdir?.PhysicalPath ?? "";
                    string appPath = appVdir?.PhysicalPath ?? "";
                    string poolName = rootApp?.ApplicationPoolName ?? "";
                    var connectionStrings = Path.Combine(sitePath, "ConnectionStrings.config");
                    if (string.IsNullOrEmpty(sitePath) || string.IsNullOrEmpty(appPath) || string.IsNullOrEmpty(poolName))
                    {
                        continue;
                    }
                    if (!File.Exists(Path.Combine(appPath, "Web.config")))
                    {
                        continue;
                    }
                    if (!File.Exists(connectionStrings))
                    {
                        continue;
                    }
                    if (!File.Exists(Path.Combine(sitePath, "Web.config")))
                    {
                        continue;
                    }
                    var assemblyName = AppVersionHelper.GetAppVersion(sitePath);
                    iisSites.Add(new IisSiteInfo {Id = site.Id, Name = site.Name, Path = sitePath, PoolName = poolName, Version = assemblyName});
                }
                onCompletion(true);
            }
            catch (Exception ex)
            {
                iisSites.Clear();
                iisSites.Add(new IisSiteInfo { Name = $"[Error loading IIS] {ex.Message}", Path = "", PoolName = "" });
                onCompletion(false);
            }
        });
    }
}
