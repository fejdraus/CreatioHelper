using System;
using System.IO;
using System.Threading.Tasks;
using System.Xml;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;
using Microsoft.Data.SqlClient;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace CreatioHelper.Infrastructure.Services.Database;

public class ModuleCleanupService : IModuleCleanupService
{
    private readonly IOutputWriter _output;

    public ModuleCleanupService(IOutputWriter output)
    {
        _output = output;
    }

    public async Task<bool> CleanupAsync(string sitePath)
    {
        if (string.IsNullOrWhiteSpace(sitePath) || !Directory.Exists(sitePath))
        {
            _output.WriteLine("[ERROR] Module cleanup: site path is missing.");
            return false;
        }

        var configPath = Path.Combine(sitePath, "ConnectionStrings.config");
        if (!File.Exists(configPath))
        {
            _output.WriteLine($"[ERROR] Module cleanup: ConnectionStrings.config not found at '{configPath}'.");
            return false;
        }

        var connectionString = ReadDbConnectionString(configPath);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("[ERROR] Module cleanup: 'db' connection string not found in ConnectionStrings.config.");
            return false;
        }

        var dbKind = DetectDbKind(connectionString);

        try
        {
            int total = await (dbKind switch
            {
                DbKind.MsSql  => ExecuteMsSqlAsync(connectionString),
                DbKind.Oracle => ExecuteOracleAsync(connectionString),
                _             => ExecutePostgresAsync(connectionString),
            });

            _output.WriteLine($"[OK] Module cleanup completed: {total} row(s) deleted.");
            return true;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Module cleanup failed: {ex.Message}");
            return false;
        }
    }

    private async Task<int> ExecuteMsSqlAsync(string connectionString)
    {
        string[] statements =
        [
            @"DELETE sme
              FROM SysModuleEdit sme
              LEFT JOIN SysSchema ss1 ON sme.SysPageSchemaUId    = ss1.UId
              LEFT JOIN SysSchema ss2 ON sme.CardSchemaUId        = ss2.UId
              LEFT JOIN SysSchema ss3 ON sme.MiniPageSchemaUId    = ss3.UId
              LEFT JOIN SysSchema ss4 ON sme.SearchRowSchemaUId   = ss4.UId
              LEFT JOIN SysModuleEntity sme2 ON sme.SysModuleEntityId = sme2.Id
              LEFT JOIN SysSchema ss5 ON sme2.SysEntitySchemaUId  = ss5.UId
              WHERE (sme.SysPageSchemaUId  IS NOT NULL AND ss1.UId IS NULL)
                 OR (sme.CardSchemaUId      IS NOT NULL AND ss2.UId IS NULL)
                 OR (sme.MiniPageSchemaUId  IS NOT NULL AND ss3.UId IS NULL)
                 OR (sme.SearchRowSchemaUId IS NOT NULL AND ss4.UId IS NULL)
                 OR (sme.SysModuleEntityId  IS NOT NULL AND ss5.UId IS NULL)",

            @"DELETE sd
              FROM SysDetail sd
              LEFT JOIN SysSchema ss1 ON sd.DetailSchemaUId  = ss1.UId
              LEFT JOIN SysSchema ss2 ON sd.EntitySchemaUId  = ss2.UId
              WHERE (sd.DetailSchemaUId IS NOT NULL AND ss1.UId IS NULL)
                 OR (sd.EntitySchemaUId IS NOT NULL AND ss2.UId IS NULL)",

            @"DELETE sme2
              FROM SysModuleEntity sme2
              LEFT JOIN SysSchema              ss   ON sme2.SysEntitySchemaUId = ss.UId
              LEFT JOIN SysModuleEdit          sme  ON sme.SysModuleEntityId   = sme2.Id
              LEFT JOIN Portal_SysModule       psm  ON psm.SysModuleEntityId   = sme2.Id
              LEFT JOIN SysDcmSettings         sds  ON sds.SysModuleEntityId   = sme2.Id
              LEFT JOIN SysModule              sm   ON sm.SysModuleEntityId    = sme2.Id
              LEFT JOIN SysModuleDcmSettings   smds ON smds.SysModuleEntityId  = sme2.Id
              LEFT JOIN SysModuleEntityInPortal smep ON smep.SysModuleEntityId = sme2.Id
              LEFT JOIN SysModuleGrid          smg  ON smg.SysModuleEntityId   = sme2.Id
              WHERE sme2.SysEntitySchemaUId IS NOT NULL AND ss.UId IS NULL
                AND sme.Id IS NULL AND psm.SysModuleEntityId IS NULL
                AND sds.SysModuleEntityId IS NULL AND sm.SysModuleEntityId IS NULL
                AND smds.SysModuleEntityId IS NULL AND smep.SysModuleEntityId IS NULL
                AND smg.SysModuleEntityId IS NULL",

            "DELETE FROM SysDetailLcz    WHERE RecordId NOT IN (SELECT Id FROM SysDetail)",
            "DELETE FROM SysModuleEditLcz WHERE RecordId NOT IN (SELECT Id FROM SysModuleEdit)",
        ];

        int total = 0;
        await using var connection = new SqlConnection(EnsureSqlServerTrust(connectionString));
        await connection.OpenAsync();
        foreach (var sql in statements)
        {
            await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            int rows = await cmd.ExecuteNonQueryAsync();
            if (rows > 0) { _output.WriteLine($"  {rows} row(s) deleted."); }
            total += rows;
        }
        return total;
    }

    private async Task<int> ExecutePostgresAsync(string connectionString)
    {
        string[] statements =
        [
            @"DELETE FROM ""SysModuleEdit""
              WHERE ""Id"" IN (
                SELECT sme.""Id""
                FROM ""SysModuleEdit"" sme
                LEFT JOIN ""SysSchema"" ss1 ON sme.""SysPageSchemaUId""    = ss1.""UId""
                LEFT JOIN ""SysSchema"" ss2 ON sme.""CardSchemaUId""       = ss2.""UId""
                LEFT JOIN ""SysSchema"" ss3 ON sme.""MiniPageSchemaUId""   = ss3.""UId""
                LEFT JOIN ""SysSchema"" ss4 ON sme.""SearchRowSchemaUId""  = ss4.""UId""
                LEFT JOIN ""SysModuleEntity"" sme2 ON sme.""SysModuleEntityId"" = sme2.""Id""
                LEFT JOIN ""SysSchema"" ss5 ON sme2.""SysEntitySchemaUId"" = ss5.""UId""
                WHERE (sme.""SysPageSchemaUId""  IS NOT NULL AND ss1.""UId"" IS NULL)
                   OR (sme.""CardSchemaUId""      IS NOT NULL AND ss2.""UId"" IS NULL)
                   OR (sme.""MiniPageSchemaUId""  IS NOT NULL AND ss3.""UId"" IS NULL)
                   OR (sme.""SearchRowSchemaUId"" IS NOT NULL AND ss4.""UId"" IS NULL)
                   OR (sme.""SysModuleEntityId""  IS NOT NULL AND ss5.""UId"" IS NULL))",

            @"DELETE FROM ""SysDetail""
              WHERE ""Id"" IN (
                SELECT sd.""Id""
                FROM ""SysDetail"" sd
                LEFT JOIN ""SysSchema"" ss1 ON sd.""DetailSchemaUId"" = ss1.""UId""
                LEFT JOIN ""SysSchema"" ss2 ON sd.""EntitySchemaUId"" = ss2.""UId""
                WHERE (sd.""DetailSchemaUId"" IS NOT NULL AND ss1.""UId"" IS NULL)
                   OR (sd.""EntitySchemaUId"" IS NOT NULL AND ss2.""UId"" IS NULL))",

            @"DELETE FROM ""SysModuleEntity""
              WHERE ""Id"" IN (
                SELECT sme2.""Id""
                FROM ""SysModuleEntity"" sme2
                LEFT JOIN ""SysSchema""               ss   ON sme2.""SysEntitySchemaUId"" = ss.""UId""
                LEFT JOIN ""SysModuleEdit""            sme  ON sme.""SysModuleEntityId""   = sme2.""Id""
                LEFT JOIN ""Portal_SysModule""         psm  ON psm.""SysModuleEntityId""   = sme2.""Id""
                LEFT JOIN ""SysDcmSettings""           sds  ON sds.""SysModuleEntityId""   = sme2.""Id""
                LEFT JOIN ""SysModule""                sm   ON sm.""SysModuleEntityId""    = sme2.""Id""
                LEFT JOIN ""SysModuleDcmSettings""     smds ON smds.""SysModuleEntityId""  = sme2.""Id""
                LEFT JOIN ""SysModuleEntityInPortal""  smep ON smep.""SysModuleEntityId""  = sme2.""Id""
                LEFT JOIN ""SysModuleGrid""            smg  ON smg.""SysModuleEntityId""   = sme2.""Id""
                WHERE sme2.""SysEntitySchemaUId"" IS NOT NULL AND ss.""UId"" IS NULL
                  AND sme.""Id"" IS NULL AND psm.""SysModuleEntityId"" IS NULL
                  AND sds.""SysModuleEntityId"" IS NULL AND sm.""SysModuleEntityId"" IS NULL
                  AND smds.""SysModuleEntityId"" IS NULL AND smep.""SysModuleEntityId"" IS NULL
                  AND smg.""SysModuleEntityId"" IS NULL)",

            @"DELETE FROM ""SysDetailLcz""     WHERE ""RecordId"" NOT IN (SELECT ""Id"" FROM ""SysDetail"")",
            @"DELETE FROM ""SysModuleEditLcz"" WHERE ""RecordId"" NOT IN (SELECT ""Id"" FROM ""SysModuleEdit"")",
        ];

        int total = 0;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var sql in statements)
        {
            await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
            int rows = await cmd.ExecuteNonQueryAsync();
            if (rows > 0) { _output.WriteLine($"  {rows} row(s) deleted."); }
            total += rows;
        }
        return total;
    }

    private async Task<int> ExecuteOracleAsync(string connectionString)
    {
        // Oracle uses the same subquery-based DELETE as PostgreSQL; double-quoted identifiers are supported.
        string[] statements =
        [
            @"DELETE FROM ""SysModuleEdit""
              WHERE ""Id"" IN (
                SELECT sme.""Id""
                FROM ""SysModuleEdit"" sme
                LEFT JOIN ""SysSchema"" ss1 ON sme.""SysPageSchemaUId""    = ss1.""UId""
                LEFT JOIN ""SysSchema"" ss2 ON sme.""CardSchemaUId""       = ss2.""UId""
                LEFT JOIN ""SysSchema"" ss3 ON sme.""MiniPageSchemaUId""   = ss3.""UId""
                LEFT JOIN ""SysSchema"" ss4 ON sme.""SearchRowSchemaUId""  = ss4.""UId""
                LEFT JOIN ""SysModuleEntity"" sme2 ON sme.""SysModuleEntityId"" = sme2.""Id""
                LEFT JOIN ""SysSchema"" ss5 ON sme2.""SysEntitySchemaUId"" = ss5.""UId""
                WHERE (sme.""SysPageSchemaUId""  IS NOT NULL AND ss1.""UId"" IS NULL)
                   OR (sme.""CardSchemaUId""      IS NOT NULL AND ss2.""UId"" IS NULL)
                   OR (sme.""MiniPageSchemaUId""  IS NOT NULL AND ss3.""UId"" IS NULL)
                   OR (sme.""SearchRowSchemaUId"" IS NOT NULL AND ss4.""UId"" IS NULL)
                   OR (sme.""SysModuleEntityId""  IS NOT NULL AND ss5.""UId"" IS NULL))",

            @"DELETE FROM ""SysDetail""
              WHERE ""Id"" IN (
                SELECT sd.""Id""
                FROM ""SysDetail"" sd
                LEFT JOIN ""SysSchema"" ss1 ON sd.""DetailSchemaUId"" = ss1.""UId""
                LEFT JOIN ""SysSchema"" ss2 ON sd.""EntitySchemaUId"" = ss2.""UId""
                WHERE (sd.""DetailSchemaUId"" IS NOT NULL AND ss1.""UId"" IS NULL)
                   OR (sd.""EntitySchemaUId"" IS NOT NULL AND ss2.""UId"" IS NULL))",

            @"DELETE FROM ""SysModuleEntity""
              WHERE ""Id"" IN (
                SELECT sme2.""Id""
                FROM ""SysModuleEntity"" sme2
                LEFT JOIN ""SysSchema""               ss   ON sme2.""SysEntitySchemaUId"" = ss.""UId""
                LEFT JOIN ""SysModuleEdit""            sme  ON sme.""SysModuleEntityId""   = sme2.""Id""
                LEFT JOIN ""Portal_SysModule""         psm  ON psm.""SysModuleEntityId""   = sme2.""Id""
                LEFT JOIN ""SysDcmSettings""           sds  ON sds.""SysModuleEntityId""   = sme2.""Id""
                LEFT JOIN ""SysModule""                sm   ON sm.""SysModuleEntityId""    = sme2.""Id""
                LEFT JOIN ""SysModuleDcmSettings""     smds ON smds.""SysModuleEntityId""  = sme2.""Id""
                LEFT JOIN ""SysModuleEntityInPortal""  smep ON smep.""SysModuleEntityId""  = sme2.""Id""
                LEFT JOIN ""SysModuleGrid""            smg  ON smg.""SysModuleEntityId""   = sme2.""Id""
                WHERE sme2.""SysEntitySchemaUId"" IS NOT NULL AND ss.""UId"" IS NULL
                  AND sme.""Id"" IS NULL AND psm.""SysModuleEntityId"" IS NULL
                  AND sds.""SysModuleEntityId"" IS NULL AND sm.""SysModuleEntityId"" IS NULL
                  AND smds.""SysModuleEntityId"" IS NULL AND smep.""SysModuleEntityId"" IS NULL
                  AND smg.""SysModuleEntityId"" IS NULL)",

            @"DELETE FROM ""SysDetailLcz""     WHERE ""RecordId"" NOT IN (SELECT ""Id"" FROM ""SysDetail"")",
            @"DELETE FROM ""SysModuleEditLcz"" WHERE ""RecordId"" NOT IN (SELECT ""Id"" FROM ""SysModuleEdit"")",
        ];

        int total = 0;
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync();
        foreach (var sql in statements)
        {
            await using var cmd = new OracleCommand(sql, connection) { CommandTimeout = 120 };
            int rows = await cmd.ExecuteNonQueryAsync();
            if (rows > 0) { _output.WriteLine($"  {rows} row(s) deleted."); }
            total += rows;
        }
        return total;
    }

    private static string? ReadDbConnectionString(string configPath)
    {
        var xmlDoc = new XmlDocument();
        xmlDoc.Load(configPath);
        return xmlDoc.SelectSingleNode("/connectionStrings/add[@name='db']") is XmlElement el
            ? el.GetAttribute("connectionString")
            : null;
    }

    private static DbKind DetectDbKind(string cs)
    {
        if (cs.IndexOf("Initial Catalog", StringComparison.OrdinalIgnoreCase) >= 0) { return DbKind.MsSql; }
        if (cs.IndexOf("HOST=", StringComparison.OrdinalIgnoreCase) >= 0
            || cs.IndexOf("SERVICE_NAME=", StringComparison.OrdinalIgnoreCase) >= 0
            || cs.IndexOf("SID=", StringComparison.OrdinalIgnoreCase) >= 0) { return DbKind.Oracle; }
        return DbKind.Postgres;
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

    private enum DbKind { MsSql, Postgres, Oracle }
}
