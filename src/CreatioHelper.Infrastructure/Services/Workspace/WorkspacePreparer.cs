#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Shared.Utils;

namespace CreatioHelper.Infrastructure.Services.Workspace;

public class WorkspacePreparer : IWorkspacePreparer
{
    private readonly IOutputWriter _output;

    public WorkspacePreparer(IOutputWriter output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public void Prepare(string sitePath, out bool quartzIsActiveOriginal)
    {
        _output.WriteLine("Starting WorkspaceConsole preparation...");
        quartzIsActiveOriginal = true;
        if (string.IsNullOrWhiteSpace(sitePath) || !Directory.Exists(sitePath))
        {
            _output.WriteLine("❌ SitePath is not provided or does not exist.");
            return;
        }

            sitePath = sitePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            bool isFramework = IsDotNetFramework(sitePath);
            string appDllPath = isFramework
                ? Path.Combine(sitePath, "bin", "Terrasoft.Common.dll")
                : Path.Combine(sitePath, "Terrasoft.Common.dll");
            string consoleDllPath = isFramework
                ? Path.Combine(sitePath, "Terrasoft.WebApp", "DesktopBin", "WorkspaceConsole", "Terrasoft.Tools.Common.dll")
                : Path.Combine(sitePath, "WorkspaceConsole", "Terrasoft.Tools.Common.dll");

            string? appDllVersion = GetDllVersion(appDllPath);
            string? consoleDllVersion = GetDllVersion(consoleDllPath);

            if (appDllVersion == null || consoleDllVersion == null)
                return;

            if (appDllVersion != consoleDllVersion)
            {
                _output.WriteLine("Version mismatch detected:");
                _output.WriteLine($"- Application: {appDllVersion}");
                _output.WriteLine($"- WorkspaceConsole: {consoleDllVersion}");
                return;
            }

            if (!CheckRequiredConfigFiles(sitePath, isFramework))
                return;

            var connectionStringsConfig = Path.Combine(sitePath, "ConnectionStrings.config");
            var connectionString = GetDatabaseConnectionString(connectionStringsConfig);
            if (connectionString == null)
                return;

            var webConfigPath = isFramework
                ? Path.Combine(sitePath, "Web.config")
                : Path.Combine(sitePath, "Terrasoft.WebHost.dll.config");
            var (useStaticFileContent, fileDesignModeEnabled, quartzIsActive) = ReadWebConfigSettings(webConfigPath);
            UpdateOutConfig(webConfigPath, true);
            quartzIsActiveOriginal = quartzIsActive;
            if (useStaticFileContent == null || fileDesignModeEnabled == null)
                return;

            var consoleConfigPath = isFramework
                ? Path.Combine(sitePath, "Terrasoft.WebApp", "DesktopBin", "WorkspaceConsole", "Terrasoft.Tools.WorkspaceConsole.exe.config")
                : Path.Combine(sitePath, "WorkspaceConsole", "Terrasoft.Tools.WorkspaceConsole.dll.config");
            UpdateWorkspaceConsoleConfig(consoleConfigPath, connectionString, useStaticFileContent, fileDesignModeEnabled);

            GrantAccessViaIcacls(connectionStringsConfig, "R");
            GrantAccessViaIcacls(webConfigPath, "R");
            GrantAccessViaIcacls(consoleConfigPath, "F");

            if (isFramework)
            {
                ExecuteModifiedBatchIfNeeded(sitePath, "PrepareWorkspaceConsole.x64.bat", Path.Combine("Bin", "Roslyn", "VBCSCompiler.exe"));
            }
        }

        private string GetWorkspaceConsoleExePath(string sitePath)
        {
            if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));
            return IsDotNetFramework(sitePath)
                ? Path.Combine(sitePath, "Terrasoft.WebApp", "DesktopBin", "WorkspaceConsole", "Terrasoft.Tools.WorkspaceConsole.exe")
                : Path.Combine(sitePath, "WorkspaceConsole", "Terrasoft.Tools.WorkspaceConsole.dll");
        }

        private string? GetDllVersion(string dllPath)
        {
            if (string.IsNullOrEmpty(dllPath)) throw new ArgumentNullException(nameof(dllPath));

            if (!File.Exists(dllPath))
            {
                _output.WriteLine($"DLL not found: {dllPath}");
                return null;
            }
            return FileVersionInfo.GetVersionInfo(dllPath).FileVersion;
        }

        private bool CheckRequiredConfigFiles(string sitePath, bool isFramework)
        {
            if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));

            string[] configFiles = isFramework
                ? new[]
                {
                    Path.Combine(sitePath, "ConnectionStrings.config"),
                    Path.Combine(sitePath, "Web.config"),
                    Path.Combine(sitePath, "Terrasoft.WebApp", "Web.config"),
                    Path.Combine(sitePath, "Terrasoft.WebApp", "DesktopBin", "WorkspaceConsole", "Terrasoft.Tools.WorkspaceConsole.exe.config")
                }
                : new[]
                {
                    Path.Combine(sitePath, "ConnectionStrings.config"),
                    Path.Combine(sitePath, "Terrasoft.WebHost.dll.config"),
                    Path.Combine(sitePath, "WorkspaceConsole", "Terrasoft.Tools.WorkspaceConsole.dll.config")
                };

            foreach (string file in configFiles)
            {
                if (!File.Exists(file))
                {
                    _output.WriteLine($"Missing required config file: {file}");
                    return false;
                }
            }
            return true;
        }

        private string? GetDatabaseConnectionString(string configPath)
        {
            if (string.IsNullOrEmpty(configPath)) throw new ArgumentNullException(nameof(configPath));

            var xmlDoc = new XmlDocument();
            xmlDoc.Load(configPath);
            return xmlDoc.SelectSingleNode("/connectionStrings/add[@name='db']") is XmlElement element
                ? element.GetAttribute("connectionString")
                : null;
        }

        private (string? UseStaticFileContent, string? FileDesignModeEnabled, bool quartzIsActive) ReadWebConfigSettings(string webConfigPath)
        {
            if (string.IsNullOrEmpty(webConfigPath)) throw new ArgumentNullException(nameof(webConfigPath));

            var xmlDoc = new XmlDocument();
            xmlDoc.Load(webConfigPath);

            var useStaticFileContent = xmlDoc.SelectSingleNode("/configuration/appSettings/add[@key='UseStaticFileContent']") is XmlElement useStaticFileContentValue
                ? useStaticFileContentValue.GetAttribute("value")
                : null;

            var fileDesignModeEnabled = xmlDoc.SelectSingleNode("/configuration/terrasoft/fileDesignMode") is XmlElement fileDesignModeNode
                ? fileDesignModeNode.GetAttribute("enabled")
                : null;
            
            var quartzIsActive = xmlDoc.SelectSingleNode("//quartzConfig[@defaultScheduler='BPMonlineQuartzScheduler']/quartz") is XmlElement quartzNode
                ? quartzNode.GetAttribute("isActive")
                : null;

            return (useStaticFileContent, fileDesignModeEnabled, quartzIsActive != null && bool.Parse(quartzIsActive));
        }

        private void UpdateWorkspaceConsoleConfig(string configPath, string connectionString, string useStaticFileContent, string fileDesignModeEnabled)
        {
            if (string.IsNullOrEmpty(configPath)) throw new ArgumentNullException(nameof(configPath));
            ArgumentNullException.ThrowIfNull(connectionString);
            ArgumentNullException.ThrowIfNull(useStaticFileContent);
            ArgumentNullException.ThrowIfNull(fileDesignModeEnabled);

            var xmlDoc = new XmlDocument();
            xmlDoc.Load(configPath);

            if (xmlDoc.SelectSingleNode("/configuration/connectionStrings/add[@name='db']") is XmlElement dbNode)
                dbNode.SetAttribute("connectionString", connectionString);

            if (xmlDoc.SelectSingleNode("/configuration/appSettings/add[@key='UseStaticFileContent']") is XmlElement staticContentNode)
                staticContentNode.SetAttribute("value", useStaticFileContent);

            if (xmlDoc.SelectSingleNode("/configuration/terrasoft/fileDesignMode") is XmlElement designModeNode)
                designModeNode.SetAttribute("enabled", fileDesignModeEnabled);

            if (xmlDoc.SelectSingleNode("//quartzConfig[@defaultScheduler='BPMonlineQuartzScheduler']/quartz") is XmlElement quartzNode)
                quartzNode.SetAttribute("isActive", "true");

            xmlDoc.Save(configPath);
            _output.WriteLine("Updated WorkspaceConsole configuration file.");
        }
        
        public void UpdateOutConfig(string configPath, bool quartzIsActive)
        {
            if (string.IsNullOrEmpty(configPath)) throw new ArgumentNullException(nameof(configPath));
            ArgumentNullException.ThrowIfNull(quartzIsActive);

            var xmlDoc = new XmlDocument();
            xmlDoc.Load(configPath);

            if (xmlDoc.SelectSingleNode("//quartzConfig[@defaultScheduler='BPMonlineQuartzScheduler']/quartz") is XmlElement quartzNode)
                quartzNode.SetAttribute("isActive", quartzIsActive.ToString().ToLowerInvariant());

            xmlDoc.Save(configPath);
            _output.WriteLine("Updated WorkspaceConsole configuration file.");
        }

        private void ExecuteModifiedBatchIfNeeded(string sitePath, string batchFileName, string targetFileRelativePath)
        {
            if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));
            if (string.IsNullOrEmpty(batchFileName)) throw new ArgumentNullException(nameof(batchFileName));
            if (string.IsNullOrEmpty(targetFileRelativePath)) throw new ArgumentNullException(nameof(targetFileRelativePath));

            var consoleDir = Path.GetDirectoryName(GetWorkspaceConsoleExePath(sitePath));
            if (consoleDir == null)
                return;

            var targetFilePath = Path.Combine(consoleDir, targetFileRelativePath);
            var batchFilePath = Path.Combine(consoleDir, batchFileName);

            if (File.Exists(targetFilePath))
            {
                return;
            }

            if (!File.Exists(batchFilePath))
            {
                return;
            }

            string originalBatchContent = File.ReadAllText(batchFilePath);
            string modifiedBatchContent = originalBatchContent.Replace("pause", "REM pause");
            File.WriteAllText(batchFilePath, modifiedBatchContent);

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{batchFileName}\"",
                    WorkingDirectory = consoleDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });

                if (process == null) return;
                process.OutputDataReceived += (_, e) => { if (e.Data != null) _output.WriteLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) _output.WriteLine($"[ERROR] {e.Data}"); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
            }
            finally
            {
                File.WriteAllText(batchFilePath, originalBatchContent);
            }
        }

        private int GrantAccessViaIcacls(string filePath, string permission)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
            if (string.IsNullOrEmpty(permission)) throw new ArgumentNullException(nameof(permission));

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _output.WriteLine($"Skipping access grant for non-Windows OS: {filePath}");
                return 0;
            }

            if (!File.Exists(filePath))
            {
                _output.WriteLine($"File not found: {filePath}");
                return 0;
            }

            string userName = Environment.UserName;
            return ProcessHelper.Run("icacls", $"\"{filePath}\" /grant \"{userName}\":{permission}", _output);
        }

        public int InstallFromRepository(string sitePath, string packagesPath)
        {
            if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));
            if (string.IsNullOrEmpty(packagesPath)) throw new ArgumentNullException(nameof(packagesPath));

            packagesPath = packagesPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string consoleExePath = GetWorkspaceConsoleExePath(sitePath);

            if (!File.Exists(consoleExePath))
            {
                _output.WriteLine($"Executable not found: {consoleExePath}");
                return 0;
            }

            var consoleDir = Path.GetDirectoryName(consoleExePath);
            if (consoleDir == null)
            {
                return 0;
            }
            var tempPackagesPath = Path.Combine(packagesPath, "TempPackages");
            if (Directory.Exists(tempPackagesPath))
            {
                try
                {
                    Directory.Delete(tempPackagesPath, true);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"[ERROR] Failed to delete temp directory '{tempPackagesPath}': {ex.Message}");
                    return 0;
                }
            }
            var appDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
            var logPath = Path.Combine(appDirectory, "WSCLog");
            _output.WriteLine($"Path to log file: {logPath}");
            var webAppPath = GetWebAppPath(sitePath);
            var configPath = GetConfigurationPath(sitePath);
            var arguments = $"-operation=\"InstallFromRepository\" -workspaceName=\"Default\" -confRuntimeParentDirectory=\"{SafePath(webAppPath)}\" -sourcePath=\"{SafePath(packagesPath)}\" -destinationPath=\"{SafePath(tempPackagesPath)}\" -skipConstraints=\"false\" -skipValidateActions=\"true\" -regenerateSchemaSources=\"true\" -updateDBStructure=\"true\" -updateSystemDBStructure=\"true\" -installPackageSqlScript=\"true\" -installPackageData=\"true\" -continueIfError=\"true\" -webApplicationPath=\"{SafePath(sitePath)}\" -logPath=\"{SafePath(logPath)}\" -configurationPath=\"{SafePath(configPath)}\" -autoExit=\"true\"";
            return RunWorkspaceConsole(sitePath, arguments, consoleDir);
        }
        
        public int RegenerateSchemaSources(string sitePath)
        {
            if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));

            sitePath = sitePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string consoleExePath = GetWorkspaceConsoleExePath(sitePath);

            if (!File.Exists(consoleExePath))
            {
                _output.WriteLine($"Executable not found: {consoleExePath}");
                return 0;
            }
            var consoleDir = Path.GetDirectoryName(consoleExePath);
            var appDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
            var logPath = Path.Combine(appDirectory, "WSCLog");
            _output.WriteLine($"Path to log file: {logPath}");
            var webAppPath = GetWebAppPath(sitePath);
            var configPath = GetConfigurationPath(sitePath);
            var arguments = $"-operation=\"RegenerateSchemaSources\" -workspaceName=\"Default\" -configurationPath=\"{SafePath(configPath)}\" -confRuntimeParentDirectory=\"{SafePath(webAppPath)}\" -logPath=\"{SafePath(logPath)}\" -autoExit=\"true\"";
            _output.WriteLine("Starting Regenerate Schema Sources...");
            return RunWorkspaceConsole(sitePath, arguments, consoleDir);
        }

        public int RebuildWorkspace(string sitePath)
        {
            if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));

            sitePath = sitePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string consoleExePath = GetWorkspaceConsoleExePath(sitePath);

            if (!File.Exists(consoleExePath))
            {
                _output.WriteLine($"Executable not found: {consoleExePath}");
                return 0;
            }

            var consoleDir = Path.GetDirectoryName(consoleExePath);
            if (consoleDir == null)
            {
                return 0;
            }
            var appDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
            var logPath = Path.Combine(appDirectory, "WSCLog");
            _output.WriteLine($"Path to log file: {logPath}");
            var webAppPath = GetWebAppPath(sitePath);
            var configPath = GetConfigurationPath(sitePath);
            var arguments = $"-operation=\"RebuildWorkspace\" -workspaceName=\"Default\" -webApplicationPath=\"{SafePath(sitePath)}\" -configurationPath=\"{SafePath(configPath)}\" -confRuntimeParentDirectory=\"{SafePath(webAppPath)}\" -logPath=\"{SafePath(logPath)}\" -autoExit=\"true\"";
            _output.WriteLine("Starting Rebuild Workspace...");
            return RunWorkspaceConsole(sitePath, arguments, consoleDir);
        }

        public int BuildConfiguration(string sitePath)
        {
            if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));

            sitePath = sitePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string consoleExePath = GetWorkspaceConsoleExePath(sitePath);

            if (!File.Exists(consoleExePath))
            {
                _output.WriteLine($"Executable not found: {consoleExePath}");
                return 0;
            }

            string? consoleDir = Path.GetDirectoryName(consoleExePath);
            if (consoleDir == null)
            {
                return 0;
            }
            var appDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
            string logPath = Path.Combine(appDirectory, "WSCLog");
            _output.WriteLine($"Path to log file: {logPath}");
            string webAppPath = GetWebAppPath(sitePath);
            string configPath = GetConfigurationPath(sitePath);
            string arguments = $"-operation=\"BuildConfiguration\" -force=\"True\" -workspaceName=\"Default\" -destinationPath=\"{SafePath(webAppPath)}\" -configurationPath=\"{SafePath(configPath)}\" -confRuntimeParentDirectory=\"{SafePath(webAppPath)}\" -logPath=\"{SafePath(logPath)}\" -autoExit=\"true\"";
            _output.WriteLine("Starting Build Configuration...");
            return RunWorkspaceConsole(sitePath, arguments, consoleDir);
        }

        public int DeletePackages(string sitePath, string packageList)
        {
            if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));
            if (string.IsNullOrEmpty(packageList)) throw new ArgumentNullException(nameof(packageList));

            sitePath = sitePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string consoleExePath = GetWorkspaceConsoleExePath(sitePath);

            if (!File.Exists(consoleExePath))
            {
                _output.WriteLine($"[ERROR] WorkspaceConsole not found: {consoleExePath}");
                return 0;
            }

            string? consoleDir = Path.GetDirectoryName(consoleExePath);
            if (consoleDir == null)
            {
                return 0;
            }
            var appDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
            string logPath = Path.Combine(appDirectory, "WSCLog");
            _output.WriteLine($"Path to log file: {logPath}");
            string webAppPath = GetWebAppPath(sitePath);
            string configPath = GetConfigurationPath(sitePath);
            string arguments = $"-operation=\"DeletePackages\" -workspaceName=\"Default\" -packagesToDelete=\"{packageList}\" -continueIfError=\"true\" -webApplicationPath=\"{SafePath(sitePath)}\" -configurationPath=\"{SafePath(configPath)}\" -confRuntimeParentDirectory=\"{SafePath(webAppPath)}\" -logPath=\"{SafePath(logPath)}\" -autoExit=\"true\"";
            _output.WriteLine($"Deleting packages: {packageList}");
            return RunWorkspaceConsole(sitePath, arguments, consoleDir);
        }

        private int RunWorkspaceConsole(string sitePath, string arguments, string? workingDirectory)
        {
            string exePath = GetWorkspaceConsoleExePath(sitePath);
            string args = $"{Quote(exePath)} {arguments}";
            _output.WriteLine($"Running dotnet with args: {args}");
            if (IsDotNetFramework(sitePath))
            {
                return ProcessHelper.Run(exePath, arguments, _output, workingDirectory);
            }
            return ProcessHelper.Run("dotnet", args, _output, workingDirectory);
        }

        private static string Quote(string path) => $"\"{path}\"";
        
        private string GetWebAppPath(string sitePath) => IsDotNetFramework(sitePath)
            ? Path.Combine(sitePath, "Terrasoft.WebApp")
            : SafePath(sitePath);

        private string GetConfigurationPath(string sitePath) => Path.Combine(GetWebAppPath(sitePath), "Terrasoft.Configuration");

        private bool IsDotNetFramework(string sitePath) => Directory.Exists(Path.Combine(sitePath, "Terrasoft.WebApp"));
        
        string SafePath(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    }
