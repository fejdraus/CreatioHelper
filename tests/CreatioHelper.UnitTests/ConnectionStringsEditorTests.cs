using System.IO;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services;
using Xunit;

namespace CreatioHelper.UnitTests;

public class ConnectionStringsEditorTests
{
    private const string Sample = @"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""redis"" connectionString=""host=localhost; db=12; port=32782;"" />
  <add name=""defPackagesWorkingCopyPath"" connectionString=""%TEMP%\%APPLICATION%\Packages"" />
  <add name=""tempDirectoryPath"" connectionString=""%TEMP%\%APPLICATION%\"" />
  <add name=""sourceControlAuthPath"" connectionString=""%TEMP%\%APPLICATION%\Svn"" />
  <add name=""elasticsearchCredentials"" connectionString=""User=gs-es; Password=secret1;"" />
  <add name=""influx"" connectionString=""url=http://10.0.7.161:30359; user=; password=; batchIntervalMs=5000"" />
  <add name=""messageBroker"" connectionString=""amqp://guest:guest@localhost/BPMonlineSolution"" />
  <add name=""db"" connectionString=""Data Source=127.0.0.1,32783; Initial Catalog=CRM; Persist Security Info=True; MultipleActiveResultSets=True; User ID=admin; Password=admin; Pooling = true;"" />
  <add name=""s3Connection"" connectionString=""ServiceUrl=http://127.0.0.1:9005; AccessKey=appuser; SecretKey=k3y; ObjectBucketName=crm; RecycleBucketName=crmrecycle;"" />
</connectionStrings>";

    private static string CreateSite(string content = Sample)
    {
        var dir = Path.Combine(Path.GetTempPath(), "chcs_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ConnectionStrings.config"), content);
        return dir;
    }

    [Fact]
    public void Read_ParsesAllSections()
    {
        var site = CreateSite();
        var data = new ConnectionStringsEditor().Read(site);

        Assert.Equal("127.0.0.1", data.DbServer);
        Assert.Equal(32783, data.DbPort);
        Assert.Equal("CRM", data.DbCatalog);
        Assert.Equal("admin", data.DbUserId);
        Assert.Equal("admin", data.DbPassword);

        Assert.Equal("localhost", data.RedisHost);
        Assert.Equal(32782, data.RedisPort);
        Assert.Equal(12, data.RedisDb);

        Assert.Equal("guest", data.MqUser);
        Assert.Equal("guest", data.MqPassword);
        Assert.Equal("localhost", data.MqHost);
        Assert.Equal(0, data.MqPort);
        Assert.Equal("BPMonlineSolution", data.MqVirtualHost);

        Assert.Equal("gs-es", data.ElasticUser);
        Assert.Equal("secret1", data.ElasticPassword);

        Assert.Equal("http://10.0.7.161:30359", data.InfluxUrl);
        Assert.Equal(5000, data.InfluxBatchIntervalMs);

        Assert.Equal("http://127.0.0.1:9005", data.S3ServiceUrl);
        Assert.Equal("appuser", data.S3AccessKey);
        Assert.Equal("crm", data.S3ObjectBucketName);
        Assert.Equal("crmrecycle", data.S3RecycleBucketName);

        Assert.Equal(@"%TEMP%\%APPLICATION%\Packages", data.DefPackagesWorkingCopyPath);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_PreservesUnknownParameters()
    {
        var site = CreateSite();
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        data.DbServer = "sql01";
        data.DbPort = 1433;
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("Data Source=sql01,1433", raw);
        Assert.Contains("Persist Security Info=True", raw);
        Assert.Contains("MultipleActiveResultSets=True", raw);
        Assert.Contains("Pooling = true", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_RoundTripsWithoutDataLoss()
    {
        var site = CreateSite();
        var editor = new ConnectionStringsEditor();

        var first = editor.Read(site);
        editor.Write(site, first);
        var second = editor.Read(site);

        Assert.Equal(first.DbServer, second.DbServer);
        Assert.Equal(first.DbPort, second.DbPort);
        Assert.Equal(first.DbCatalog, second.DbCatalog);
        Assert.Equal(first.RedisHost, second.RedisHost);
        Assert.Equal(first.RedisPort, second.RedisPort);
        Assert.Equal(first.RedisDb, second.RedisDb);
        Assert.Equal(first.MqUser, second.MqUser);
        Assert.Equal(first.MqHost, second.MqHost);
        Assert.Equal(first.MqVirtualHost, second.MqVirtualHost);
        Assert.Equal(first.InfluxUrl, second.InfluxUrl);
        Assert.Equal(first.InfluxBatchIntervalMs, second.InfluxBatchIntervalMs);
        Assert.Equal(first.S3ServiceUrl, second.S3ServiceUrl);
        Assert.Equal(first.S3SecretKey, second.S3SecretKey);
        Assert.Equal(first.DefPackagesWorkingCopyPath, second.DefPackagesWorkingCopyPath);
        Assert.Equal(first.TempDirectoryPath, second.TempDirectoryPath);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_DoesNotCreateEntriesWhenFieldsEmpty()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""db"" connectionString=""Data Source=srv; Initial Catalog=X; User ID=u; Password=p;"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.DoesNotContain("redis", raw);
        Assert.DoesNotContain("s3Connection", raw);
        Assert.DoesNotContain("messageBroker", raw);
        Assert.Contains("Data Source=srv", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_CreatesMissingEntryWhenFieldsFilled()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""db"" connectionString=""Data Source=srv; Initial Catalog=X; User ID=u; Password=p;"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        data.RedisHost = "localhost";
        data.RedisPort = 6379;
        data.S3ServiceUrl = "http://minio:9000";
        data.S3AccessKey = "appuser";
        data.MqHost = "rabbit";
        data.MqUser = "guest";
        data.MqPassword = "guest";
        data.MqVirtualHost = "BPMonlineSolution";
        editor.Write(site, data);

        var reread = editor.Read(site);
        Assert.Equal("localhost", reread.RedisHost);
        Assert.Equal(6379, reread.RedisPort);
        Assert.Equal("http://minio:9000", reread.S3ServiceUrl);
        Assert.Equal("appuser", reread.S3AccessKey);
        Assert.Equal("rabbit", reread.MqHost);
        Assert.Equal("guest", reread.MqUser);
        Assert.Equal("BPMonlineSolution", reread.MqVirtualHost);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_CreatesDbEntryWithMssqlTemplate()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        data.DbServer = "sql01";
        data.DbPort = 1433;
        data.DbCatalog = "NewCRM";
        data.DbUserId = "sa";
        data.DbPassword = "pw";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("Data Source=sql01,1433", raw);
        Assert.Contains("Initial Catalog=NewCRM", raw);
        Assert.Contains("Pooling = true", raw);

        var reread = editor.Read(site);
        Assert.Equal("sql01", reread.DbServer);
        Assert.Equal("NewCRM", reread.DbCatalog);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Read_ParsesPostgresFormat()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""db"" connectionString=""server=pg01;port=5432;database=creatio;username=pguser;password=pgpass;Timeout=500"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal("pg01", data.DbServer);
        Assert.Equal(5432, data.DbPort);
        Assert.Equal("creatio", data.DbCatalog);
        Assert.Equal("pguser", data.DbUserId);
        Assert.Equal("pgpass", data.DbPassword);

        data.DbServer = "pg02";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("server=pg02", raw);
        Assert.Contains("Timeout=500", raw);
        Assert.DoesNotContain("Data Source", raw);
        Assert.DoesNotContain("Initial Catalog", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_DoesNotAddEmptyCredentialsToIntegratedSecurityString()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""db"" connectionString=""Data Source=srv; Initial Catalog=X; Integrated Security=SSPI;"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        data.DbCatalog = "Y";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("Initial Catalog=Y", raw);
        Assert.Contains("Integrated Security=SSPI", raw);
        Assert.DoesNotContain("User ID", raw);
        Assert.DoesNotContain("Password", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void RoundTrip_QuotedPasswordWithSemicolon()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""db"" connectionString=""Data Source=srv; Initial Catalog=X; User ID=u; Password='ab;cd'; Pooling=true;"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal("ab;cd", data.DbPassword);

        editor.Write(site, data);
        var reread = editor.Read(site);
        Assert.Equal("ab;cd", reread.DbPassword);
        Assert.Equal("X", reread.DbCatalog);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("Pooling=true", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_QuotesNewPasswordContainingSemicolon()
    {
        var site = CreateSite();
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        data.DbPassword = "p;w=d";
        editor.Write(site, data);

        var reread = editor.Read(site);
        Assert.Equal("p;w=d", reread.DbPassword);
        Assert.Equal("CRM", reread.DbCatalog);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_KeepsUnparsableMessageBrokerIntact()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""messageBroker"" connectionString=""host=rabbit;user=guest"" />
  <add name=""redis"" connectionString=""host=localhost; db=1; port=6379;"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.False(data.MqParsed);

        data.RedisPort = 6380;
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("host=rabbit;user=guest", raw);
        Assert.Contains("port=6380", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_PreservesDefaultVhostWithoutTrailingSlash()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""messageBroker"" connectionString=""amqp://guest:guest@rabbit"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal("", data.MqVirtualHost);

        data.MqHost = "rabbit02";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("amqp://guest:guest@rabbit02", raw);
        Assert.DoesNotContain("rabbit02/", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void RoundTrip_EscapedAmqpCredentials()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""messageBroker"" connectionString=""amqp://us%40er:p%2Fass@rabbit/vh"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal("us@er", data.MqUser);
        Assert.Equal("p/ass", data.MqPassword);

        editor.Write(site, data);
        var reread = editor.Read(site);
        Assert.Equal("us@er", reread.MqUser);
        Assert.Equal("p/ass", reread.MqPassword);

        Directory.Delete(site, true);
    }

    [Fact]
    public void RoundTrip_OracleTnsDescriptor()
    {
        const string tns = "(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=orasrv)(PORT=1521)))(CONNECT_DATA=(SERVICE_NAME=XE)))";
        var site = CreateSite($@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""db"" connectionString=""Data Source={tns}; User Id=SysUser; Password=orapass; Statement Cache Size=300"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal(tns, data.DbServer);
        Assert.Equal(0, data.DbPort);
        Assert.Equal("", data.DbCatalog);
        Assert.Equal("SysUser", data.DbUserId);
        Assert.Equal("orapass", data.DbPassword);

        data.DbPassword = "newpass";
        data.DbPort = 1521;
        data.DbCatalog = "typed-by-mistake";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains($"Data Source={tns};", raw);
        Assert.DoesNotContain("),1521", raw);
        Assert.DoesNotContain("Initial Catalog", raw);
        Assert.Contains("Password=newpass", raw);
        Assert.Contains("Statement Cache Size=300", raw);
        Assert.Contains("User Id=SysUser", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_OracleEasyConnectDoesNotAppendPort()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""db"" connectionString=""Data Source=orasrv:1521/XE; User Id=u; Password=p;"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal("orasrv:1521/XE", data.DbServer);

        data.DbPort = 1522;
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("Data Source=orasrv:1521/XE;", raw);
        Assert.DoesNotContain(",1522", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Read_DetectsDbType()
    {
        var editor = new ConnectionStringsEditor();

        var mssql = CreateSite();
        Assert.Equal("MS SQL Server", editor.Read(mssql).DbType);
        Directory.Delete(mssql, true);

        var pg = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""db"" connectionString=""Server=pg01;Port=5432;Database=creatio;User ID=pguser;password=pgpass;"" />
</connectionStrings>");
        Assert.Equal("PostgreSQL", editor.Read(pg).DbType);
        Directory.Delete(pg, true);

        var ora = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""db"" connectionString=""Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=o)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=XE)));User Id=u;Password=p;"" />
</connectionStrings>");
        Assert.Equal("Oracle", editor.Read(ora).DbType);
        Directory.Delete(ora, true);
    }

    [Fact]
    public void RoundTrip_RedisSentinel()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""redisSentinel"" connectionString=""sentinelHosts=localhost:26380,localhost:26381,localhost:26382;masterName=mymaster;scanForOtherSentinels=false;db=1;maxReadPoolSize=10;maxWritePoolSize=500"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal("localhost:26380,localhost:26381,localhost:26382", data.SentinelHosts);
        Assert.Equal("mymaster", data.SentinelMasterName);
        Assert.False(data.SentinelScanForOther);
        Assert.Equal(1, data.SentinelDb);

        data.SentinelHosts = "r1:26380,r2:26380";
        data.SentinelScanForOther = true;
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("sentinelHosts=r1:26380,r2:26380", raw);
        Assert.Contains("scanForOtherSentinels=true", raw);
        Assert.Contains("masterName=mymaster", raw);
        Assert.Contains("maxReadPoolSize=10", raw);
        Assert.Contains("maxWritePoolSize=500", raw);

        var reread = editor.Read(site);
        Assert.Equal("r1:26380,r2:26380", reread.SentinelHosts);
        Assert.True(reread.SentinelScanForOther);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Read_DbTypeFromWebConfigWinsOverEmptyString()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""db"" connectionString="""" />
</connectionStrings>");
        File.WriteAllText(Path.Combine(site, "Web.config"), @"<?xml version=""1.0""?>
<configuration>
  <terrasoft>
    <db>
      <general connectionStringName=""db"" executorType=""Terrasoft.DB.PostgreSql.PostgreSqlExecutor, Terrasoft.DB.PostgreSql"" />
    </db>
  </terrasoft>
</configuration>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal("PostgreSQL", data.DbType);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Read_DbTypeFromWebHostConfigWinsOverWebConfig()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""db"" connectionString=""Server=pg01;Port=5432;Database=creatio;User ID=u;password=p;"" />
</connectionStrings>");
        File.WriteAllText(Path.Combine(site, "Terrasoft.WebHost.dll.config"), @"<?xml version=""1.0""?>
<configuration>
  <terrasoft>
    <db>
      <general connectionStringName=""db"" executorType=""Terrasoft.DB.PostgreSql.PostgreSqlExecutor, Terrasoft.DB.PostgreSql"" />
    </db>
  </terrasoft>
</configuration>");
        File.WriteAllText(Path.Combine(site, "Web.config"), @"<?xml version=""1.0""?>
<configuration>
</configuration>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal("PostgreSQL", data.DbType);
        Assert.Equal("pg01", data.DbServer);
        Assert.Equal(5432, data.DbPort);

        Directory.Delete(site, true);
    }

    [Fact]
    public void RoundTrip_RedisCluster()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""redis"" connectionString=""clusterHosts=10.0.0.1:6379,10.0.0.2:6379,10.0.0.3:6379;password=rpass;useTls=true"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal(RedisConnectionMode.Cluster, data.RedisMode);
        Assert.Equal("10.0.0.1:6379,10.0.0.2:6379,10.0.0.3:6379", data.RedisClusterHosts);
        Assert.Equal("rpass", data.RedisPassword);

        data.RedisClusterHosts = "n1:6379,n2:6379";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("clusterHosts=n1:6379,n2:6379", raw);
        Assert.Contains("password=rpass", raw);
        Assert.Contains("useTls=true", raw);
        Assert.DoesNotContain("host=;", raw);
        Assert.DoesNotContain("port=0", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_ClassicRedisWithPassword()
    {
        var site = CreateSite();
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal(RedisConnectionMode.SingleNode, data.RedisMode);

        data.RedisPassword = "rp2";
        editor.Write(site, data);

        var reread = editor.Read(site);
        Assert.Equal("rp2", reread.RedisPassword);
        Assert.Equal("localhost", reread.RedisHost);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Read_ExtractsDbExtraParams()
    {
        var site = CreateSite();
        var data = new ConnectionStringsEditor().Read(site);

        Assert.Equal("Persist Security Info=True; MultipleActiveResultSets=True; Pooling = true", data.DbExtraParams);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_AppliesEditedExtraParams()
    {
        var site = CreateSite();
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        data.DbExtraParams = "Persist Security Info=True; Pooling=false; Max Pool Size=200; Async=true";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("Pooling=false", raw);
        Assert.Contains("Max Pool Size=200", raw);
        Assert.Contains("Async=true", raw);
        Assert.DoesNotContain("MultipleActiveResultSets", raw);
        Assert.Contains("Data Source=127.0.0.1,32783", raw);
        Assert.Contains("User ID=admin", raw);

        var reread = editor.Read(site);
        Assert.Equal("Persist Security Info=True; Pooling=false; Max Pool Size=200; Async=true", reread.DbExtraParams);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_ExtraParamsCannotOverrideKnownKeys()
    {
        var site = CreateSite();
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        data.DbExtraParams = "Password=hacked; Pooling = true; Persist Security Info=True; MultipleActiveResultSets=True";
        editor.Write(site, data);

        var reread = editor.Read(site);
        Assert.Equal("admin", reread.DbPassword);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_EditsRedisPoolSizes()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""redis"" connectionString=""host=localhost;db=1;port=6379;maxReadPoolSize=10;maxWritePoolSize=500"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal("maxReadPoolSize=10; maxWritePoolSize=500", data.RedisExtraParams);

        data.RedisExtraParams = "maxReadPoolSize=25; maxWritePoolSize=1000";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("maxReadPoolSize=25", raw);
        Assert.Contains("maxWritePoolSize=1000", raw);
        Assert.Contains("host=localhost", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_SwitchSingleNodeToCluster_RemovesSingleNodeKeys()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""redis"" connectionString=""host=localhost;db=1;port=6379;maxReadPoolSize=10"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        data.RedisMode = RedisConnectionMode.Cluster;
        data.RedisClusterHosts = "n1:6379,n2:6379,n3:6379";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("clusterHosts=n1:6379,n2:6379,n3:6379", raw);
        Assert.Contains("maxReadPoolSize=10", raw);
        Assert.DoesNotContain("host=localhost", raw);
        Assert.DoesNotContain("port=6379", raw);

        var reread = editor.Read(site);
        Assert.Equal(RedisConnectionMode.Cluster, reread.RedisMode);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_SwitchClusterToSingleNode_RemovesClusterHosts()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""redis"" connectionString=""clusterHosts=n1:6379,n2:6379;password=rp"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        data.RedisMode = RedisConnectionMode.SingleNode;
        data.RedisHost = "localhost";
        data.RedisPort = 6379;
        data.RedisDb = 2;
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.DoesNotContain("clusterHosts", raw);
        Assert.Contains("host=localhost", raw);
        Assert.Contains("port=6379", raw);
        Assert.Contains("db=2", raw);
        Assert.Contains("password=rp", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_EmptyRedisString_ConfiguresCluster()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""redis"" connectionString="""" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal(RedisConnectionMode.SingleNode, data.RedisMode);

        data.RedisMode = RedisConnectionMode.Cluster;
        data.RedisClusterHosts = "n1:6379,n2:6379";
        editor.Write(site, data);

        var reread = editor.Read(site);
        Assert.Equal(RedisConnectionMode.Cluster, reread.RedisMode);
        Assert.Equal("n1:6379,n2:6379", reread.RedisClusterHosts);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Read_DetectsSentinelInsideRedisEntry()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""redis"" connectionString=""sentinelHosts=localhost:26380,localhost:26381;masterName=myMaster;scanForOtherSentinels=false;db=1;maxReadPoolSize=10;maxWritePoolSize=500"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal(RedisConnectionMode.Sentinel, data.RedisMode);
        Assert.False(data.SentinelIsLegacyEntry);
        Assert.Equal("localhost:26380,localhost:26381", data.SentinelHosts);
        Assert.Equal("myMaster", data.SentinelMasterName);
        Assert.Equal(1, data.SentinelDb);

        data.SentinelMasterName = "master2";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("masterName=master2", raw);
        Assert.Contains("maxReadPoolSize=10", raw);
        Assert.DoesNotContain("redisSentinel", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_SwitchLegacySentinelToSingleNode_RemovesSentinelEntry()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""redis"" connectionString=""host=old;db=1;port=6379"" />
  <add name=""redisSentinel"" connectionString=""sentinelHosts=s1:26380;masterName=m;scanForOtherSentinels=false;db=1"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal(RedisConnectionMode.Sentinel, data.RedisMode);
        Assert.True(data.SentinelIsLegacyEntry);

        data.RedisMode = RedisConnectionMode.SingleNode;
        data.RedisHost = "localhost";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.DoesNotContain("redisSentinel", raw);
        Assert.Contains("host=localhost", raw);

        var reread = editor.Read(site);
        Assert.Equal(RedisConnectionMode.SingleNode, reread.RedisMode);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Read_MisconfiguredClusterAsHostList_ShownAsIsAndFixableViaClusterMode()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""redis"" connectionString=""host=node1,node2,node3;port=6379;db=0;maxReadPoolSize=10"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal(RedisConnectionMode.SingleNode, data.RedisMode);
        Assert.Equal("node1,node2,node3", data.RedisHost);
        Assert.Equal(6379, data.RedisPort);

        data.RedisMode = RedisConnectionMode.Cluster;
        data.RedisClusterHosts = "node1:6379,node2:6379,node3:6379";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("clusterHosts=node1:6379,node2:6379,node3:6379", raw);
        Assert.DoesNotContain("host=node1,node2,node3", raw);
        Assert.DoesNotContain("port=6379", raw);
        Assert.Contains("maxReadPoolSize=10", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Read_UnknownAndMisplacedKeys_SurfaceInExtraParams()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""redis"" connectionString=""hosst=localhost;db=1;port=6379;Initial Catalog=oops"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal("", data.RedisHost);
        Assert.Contains("hosst=localhost", data.RedisExtraParams);
        Assert.Contains("Initial Catalog=oops", data.RedisExtraParams);

        editor.Write(site, data);
        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("hosst=localhost", raw);

        Directory.Delete(site, true);
    }

    [Fact]
    public void Write_PreservesAmqpSchemeAndQuery()
    {
        var site = CreateSite(@"<?xml version=""1.0"" encoding=""utf-8""?>
<connectionStrings>
  <add name=""messageBroker"" connectionString=""amqps://u:p@rabbit:5671/vh?heartbeat=30"" />
</connectionStrings>");
        var editor = new ConnectionStringsEditor();

        var data = editor.Read(site);
        Assert.Equal("rabbit", data.MqHost);
        Assert.Equal(5671, data.MqPort);
        Assert.Equal("vh", data.MqVirtualHost);

        data.MqHost = "rabbit02";
        editor.Write(site, data);

        var raw = File.ReadAllText(Path.Combine(site, "ConnectionStrings.config"));
        Assert.Contains("amqps://u:p@rabbit02:5671/vh?heartbeat=30", raw);

        Directory.Delete(site, true);
    }
}
