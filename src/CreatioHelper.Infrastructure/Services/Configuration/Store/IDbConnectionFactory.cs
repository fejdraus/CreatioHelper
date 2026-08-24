using System.Data;

namespace CreatioHelper.Infrastructure.Services.Configuration.Store;

public interface IDbConnectionFactory
{
    StorageProvider Provider { get; }

    string ConnectionString { get; }

    IDbConnection Create();
}
