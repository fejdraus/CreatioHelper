using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading.Tasks;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services;

[SupportedOSPlatform("windows")]
public class WindowsFeaturesService : IWindowsFeaturesService
{
    private readonly IOutputWriter _output;

    public WindowsFeaturesService(IOutputWriter output)
    {
        _output = output;
    }

    public async Task EnableCreatioFeaturesAsync()
    {
        var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            _output.WriteLine("[ERROR] Administrator rights required. Please restart CreatioHelper as administrator.");
            return;
        }

        _output.WriteLine("[INFO] Enabling Windows features for Creatio (.NET Framework)...");

        var featuresToEnable = new List<string>(BaseWindowsFeatures);

        await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT ProductType FROM Win32_OperatingSystem");
                var productType = (uint)searcher.Get().Cast<ManagementObject>().First()["ProductType"];
                featuresToEnable.Add(productType is 2 or 3 ? "MSMQ" : "MSMQ-Container");
            }
            catch
            {
                featuresToEnable.Add("MSMQ-Container");
            }
        });

        var toEnable = new List<string>();

        await Task.Run(() =>
        {
            var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_OptionalFeature WHERE InstallState=1");
                foreach (ManagementObject obj in searcher.Get())
                {
                    enabled.Add((string)obj["Name"]);
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[WARN] Could not query feature states via WMI: {ex.Message}");
            }

            foreach (var feature in featuresToEnable)
            {
                if (enabled.Contains(feature))
                {
                    _output.WriteLine($"{feature} is already enabled.");
                }
                else
                {
                    toEnable.Add(feature);
                }
            }
        });

        if (toEnable.Count == 0)
        {
            _output.WriteLine("[OK] All features are already enabled.");
            return;
        }

        _output.WriteLine($"[INFO] Enabling {toEnable.Count} feature(s) via DISM...");

        var featureArgs = string.Join(" ", toEnable.Select(f => $"/FeatureName:{f}"));
        var psi = new ProcessStartInfo
        {
            FileName = "dism.exe",
            Arguments = $"/online /Enable-Feature {featureArgs} /All /NoRestart",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { _output.WriteLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { _output.WriteLine($"[ERROR] {e.Data}"); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        _output.WriteLine(process.ExitCode switch
        {
            0 => "[OK] Features enabled successfully.",
            3010 => "[OK] Features enabled. Restart required.",
            _ => $"[ERROR] DISM exited with code {process.ExitCode}.",
        });
    }

    private static readonly string[] BaseWindowsFeatures =
    [
        "NetFx3",
        "WCF-Services45",
        "WCF-HTTP-Activation45",
        "WCF-TCP-Activation45",
        "WCF-Pipe-Activation45",
        "WCF-MSMQ-Activation45",
        "WCF-TCP-PortSharing45",
        "IIS-WebServerRole",
        "IIS-WebServer",
        "IIS-CommonHttpFeatures",
        "IIS-HttpErrors",
        "IIS-HttpRedirect",
        "IIS-ApplicationDevelopment",
        "IIS-Security",
        "IIS-RequestFiltering",
        "IIS-NetFxExtensibility",
        "IIS-NetFxExtensibility45",
        "IIS-HealthAndDiagnostics",
        "IIS-HttpLogging",
        "IIS-LoggingLibraries",
        "IIS-RequestMonitor",
        "IIS-HttpTracing",
        "IIS-IPSecurity",
        "IIS-Performance",
        "IIS-WebServerManagementTools",
        "WAS-WindowsActivationService",
        "WAS-ProcessModel",
        "WAS-NetFxEnvironment",
        "WAS-ConfigurationAPI",
        "WCF-HTTP-Activation",
        "WCF-NonHTTP-Activation",
        "IIS-StaticContent",
        "IIS-DefaultDocument",
        "IIS-WebSockets",
        "IIS-ApplicationInit",
        "IIS-ISAPIFilter",
        "IIS-ISAPIExtensions",
        "IIS-ASPNET",
        "IIS-ASPNET45",
        "IIS-CustomLogging",
        "IIS-BasicAuthentication",
        "IIS-HttpCompressionStatic",
        "IIS-ManagementConsole",
        "MSMQ-Server",
    ];
}
