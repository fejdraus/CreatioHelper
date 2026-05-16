using System.Xml;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace CreatioHelper.Infrastructure.Services.Database;

public class PackageFlagsResetter : IPackageFlagsResetter
{
    private readonly IOutputWriter _output;

    public PackageFlagsResetter(IOutputWriter output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public bool ResetFlags(string sitePath, bool includeUnlockedPackages)
    {
        if (string.IsNullOrWhiteSpace(sitePath) || !Directory.Exists(sitePath))
        {
            _output.WriteLine("[ERROR] Reset package flags: site path is missing.");
            return false;
        }

        var configPath = Path.Combine(sitePath, "ConnectionStrings.config");
        if (!File.Exists(configPath))
        {
            _output.WriteLine($"[ERROR] Reset package flags: ConnectionStrings.config not found at '{configPath}'.");
            return false;
        }

        var connectionString = ReadDbConnectionString(configPath);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("[ERROR] Reset package flags: 'db' connection string not found in ConnectionStrings.config.");
            return false;
        }

        bool isMsSql = LooksLikeMsSql(connectionString);
        string scope = includeUnlockedPackages ? "all packages" : "locked packages (InstallType = 1)";

        try
        {
            int totalAffected = isMsSql
                ? ExecuteMsSql(connectionString, includeUnlockedPackages)
                : ExecutePostgres(connectionString, includeUnlockedPackages);

            _output.WriteLine($"[OK] Reset IsLocked/IsChanged on {scope}: {totalAffected} row(s) updated.");
            return true;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Reset package flags failed: {ex.Message}");
            return false;
        }
    }

    private static string? ReadDbConnectionString(string configPath)
    {
        var xmlDoc = new XmlDocument();
        xmlDoc.Load(configPath);
        return xmlDoc.SelectSingleNode("/connectionStrings/add[@name='db']") is XmlElement element
            ? element.GetAttribute("connectionString")
            : null;
    }

    private static bool LooksLikeMsSql(string connectionString)
    {
        return connectionString.IndexOf("Initial Catalog", StringComparison.OrdinalIgnoreCase) >= 0
            || connectionString.IndexOf("Data Source", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int ExecuteMsSql(string connectionString, bool includeUnlockedPackages)
    {
        string packageFilter = includeUnlockedPackages ? string.Empty : " AND sp.InstallType = 1";
        string[] schemaUpdates =
        {
            $@"UPDATE ss SET IsChanged = 0, IsLocked = 0
               FROM SysWorkspace sw
               INNER JOIN SysPackage sp ON sp.SysWorkspaceId = sw.Id
               INNER JOIN SysSchema ss ON ss.SysPackageId = sp.Id
               WHERE sw.Name = 'Default'{packageFilter}
                 AND (ss.IsChanged = 1 OR ss.IsLocked = 1);",

            $@"UPDATE ss SET IsChanged = 0, IsLocked = 0
               FROM SysWorkspace sw
               INNER JOIN SysPackage sp ON sp.SysWorkspaceId = sw.Id
               INNER JOIN SysPackageSchemaData ss ON ss.SysPackageId = sp.Id
               WHERE sw.Name = 'Default'{packageFilter}
                 AND (ss.IsChanged = 1 OR ss.IsLocked = 1);",

            $@"UPDATE ss SET IsChanged = 0, IsLocked = 0
               FROM SysWorkspace sw
               INNER JOIN SysPackage sp ON sp.SysWorkspaceId = sw.Id
               INNER JOIN SysPackageReferenceAssembly ss ON ss.SysPackageId = sp.Id
               WHERE sw.Name = 'Default'{packageFilter}
                 AND (ss.IsChanged = 1 OR ss.IsLocked = 1);",

            $@"UPDATE ss SET IsChanged = 0, IsLocked = 0
               FROM SysWorkspace sw
               INNER JOIN SysPackage sp ON sp.SysWorkspaceId = sw.Id
               INNER JOIN SysPackageSqlScript ss ON ss.SysPackageId = sp.Id
               WHERE sw.Name = 'Default'{packageFilter}
                 AND (ss.IsChanged = 1 OR ss.IsLocked = 1);",

            $@"UPDATE sp SET IsChanged = 0, IsLocked = 0
               FROM SysWorkspace sw
               INNER JOIN SysPackage sp ON sp.SysWorkspaceId = sw.Id
               WHERE sw.Name = 'Default'{packageFilter}
                 AND (sp.IsChanged = 1 OR sp.IsLocked = 1);",
        };

        int totalAffected = 0;
        using var connection = new SqlConnection(EnsureSqlServerTrust(connectionString));
        connection.Open();
        foreach (string sql in schemaUpdates)
        {
            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 120;
            totalAffected += cmd.ExecuteNonQuery();
        }
        return totalAffected;
    }

    private static string EnsureSqlServerTrust(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
        {
            builder.TrustServerCertificate = true;
        }
        return builder.ConnectionString;
    }

    private static int ExecutePostgres(string connectionString, bool includeUnlockedPackages)
    {
        string packageFilter = includeUnlockedPackages ? string.Empty : " AND sp.\"InstallType\" = 1";
        string[] schemaUpdates =
        {
            $@"UPDATE ""SysSchema"" ss SET ""IsChanged"" = false, ""IsLocked"" = false
               FROM ""SysWorkspace"" sw
               INNER JOIN ""SysPackage"" sp ON sp.""SysWorkspaceId"" = sw.""Id""
               WHERE ss.""SysPackageId"" = sp.""Id"" AND sw.""Name"" = 'Default'{packageFilter}
                 AND (ss.""IsChanged"" = true OR ss.""IsLocked"" = true);",

            $@"UPDATE ""SysPackageSchemaData"" ss SET ""IsChanged"" = false, ""IsLocked"" = false
               FROM ""SysWorkspace"" sw
               INNER JOIN ""SysPackage"" sp ON sp.""SysWorkspaceId"" = sw.""Id""
               WHERE ss.""SysPackageId"" = sp.""Id"" AND sw.""Name"" = 'Default'{packageFilter}
                 AND (ss.""IsChanged"" = true OR ss.""IsLocked"" = true);",

            $@"UPDATE ""SysPackageReferenceAssembly"" ss SET ""IsChanged"" = false, ""IsLocked"" = false
               FROM ""SysWorkspace"" sw
               INNER JOIN ""SysPackage"" sp ON sp.""SysWorkspaceId"" = sw.""Id""
               WHERE ss.""SysPackageId"" = sp.""Id"" AND sw.""Name"" = 'Default'{packageFilter}
                 AND (ss.""IsChanged"" = true OR ss.""IsLocked"" = true);",

            $@"UPDATE ""SysPackageSqlScript"" ss SET ""IsChanged"" = false, ""IsLocked"" = false
               FROM ""SysWorkspace"" sw
               INNER JOIN ""SysPackage"" sp ON sp.""SysWorkspaceId"" = sw.""Id""
               WHERE ss.""SysPackageId"" = sp.""Id"" AND sw.""Name"" = 'Default'{packageFilter}
                 AND (ss.""IsChanged"" = true OR ss.""IsLocked"" = true);",

            $@"UPDATE ""SysPackage"" sp SET ""IsChanged"" = false, ""IsLocked"" = false
               FROM ""SysWorkspace"" sw
               WHERE sp.""SysWorkspaceId"" = sw.""Id"" AND sw.""Name"" = 'Default'{packageFilter}
                 AND (sp.""IsChanged"" = true OR sp.""IsLocked"" = true);",
        };

        int totalAffected = 0;
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        foreach (string sql in schemaUpdates)
        {
            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.CommandTimeout = 120;
            totalAffected += cmd.ExecuteNonQuery();
        }
        return totalAffected;
    }
}
