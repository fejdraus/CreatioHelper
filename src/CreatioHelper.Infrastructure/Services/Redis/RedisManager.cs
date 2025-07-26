using System;
using System.IO;
using System.Xml;
using StackExchange.Redis;
using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Redis
{
    public class RedisManager : IRedisManager
    {
        private readonly IOutputWriter _output;
        private readonly ConnectionMultiplexer _redis;

        public RedisManager(IOutputWriter output, string sitePath)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            if (string.IsNullOrWhiteSpace(sitePath)) throw new ArgumentNullException(nameof(sitePath));
            var connectionStrings = Path.Combine(sitePath, "ConnectionStrings.config");
            var redisInfo = ReadRedisConnectionInfo(connectionStrings);
            var options = new ConfigurationOptions
            {
                Password = redisInfo.Password,
                DefaultDatabase = int.TryParse(redisInfo.DataBase, out var db) ? db : null,
                Ssl = redisInfo.UseTls,
                AbortOnConnectFail = false,
                AllowAdmin = true
            };

            if (redisInfo.Hosts != null)
                foreach (var host in redisInfo.Hosts)
                {
                    options.EndPoints.Add(host);
                }

            if (!string.IsNullOrEmpty(redisInfo.CertificatePath))
            {
                options.CertificateSelection += delegate
                {
                    return new System.Security.Cryptography.X509Certificates.X509Certificate2(redisInfo.CertificatePath, redisInfo.CertificatePassword);
                };
            }

            _redis = ConnectionMultiplexer.Connect(options);
        }
        
        public RedisInfo ReadRedisConnectionInfo(string configFilePath)
        {
            var xmlDoc = new XmlDocument();
            xmlDoc.Load(configFilePath);

            var node = xmlDoc.SelectSingleNode("/connectionStrings/add[@name='redis']");
            if (node is XmlElement element)
            {
                var connectionString = element.GetAttribute("connectionString");
                return ParseRedisConnectionString(connectionString);
            }

            return new RedisInfo();
        }

        public bool CheckStatus()
        {
            try
            {
                var db = _redis.GetDatabase();
                var pong = db.Ping();
                _output.WriteLine($"Redis status: PONG in {pong.TotalMilliseconds} ms");
                return true;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] Failed to check Redis status: {ex.Message}");
                return false;
            }
        }

        public void Clear()
        {
            try
            {
                foreach (var endpoint in _redis.GetEndPoints())
                {
                    var server = _redis.GetServer(endpoint);
                    if (!server.IsConnected)
                    {
                        _output.WriteLine($"[WARN] Server {endpoint} is not connected. Skipping.");
                        continue;
                    }

                    if (server.IsReplica)
                    {
                        _output.WriteLine($"[INFO] Skipping replica server: {endpoint}");
                        continue;
                    }

                    server.FlushDatabase();
                    _output.WriteLine($"Redis database flushed on {endpoint}.");
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] Failed to clear Redis: {ex.Message}");
            }
        }
        private RedisInfo ParseRedisConnectionString(string connectionString)
        {
            var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
            string host = "localhost";
            string[] clusterHosts = [];
            string port = "6379";
            string db = "0";
            string password = "";
            bool useTls = false;
            string certificatePath = "";
            string certificatePassword = "";

            foreach (var part in parts)
            {
                var keyValue = part.Split('=', 2);
                if (keyValue.Length != 2) continue;

                string key = keyValue[0].Trim().ToLowerInvariant();
                string value = keyValue[1].Trim();

                switch (key)
                {
                    case "host":
                        host = value;
                        break;
                    case "clusterhosts":
                        clusterHosts = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                        break;
                    case "port":
                        port = value;
                        break;
                    case "db":
                        db = value;
                        break;
                    case "password":
                        password = value;
                        break;
                    case "usetls":
                        useTls = bool.TryParse(value, out bool tls) && tls;
                        break;
                    case "certificatepath":
                        certificatePath = value;
                        break;
                    case "certificatepassword":
                        certificatePassword = value;
                        break;
                }
            }

            return new RedisInfo
            {
                Hosts = clusterHosts.Length > 0 ? clusterHosts : new[] { $"{host}:{port}" },
                DataBase = db,
                Password = password,
                UseTls = useTls,
                CertificatePath = certificatePath,
                CertificatePassword = certificatePassword
            };
        }
    }
}