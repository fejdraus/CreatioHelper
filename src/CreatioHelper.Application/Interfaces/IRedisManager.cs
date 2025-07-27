namespace CreatioHelper.Application.Interfaces;

using Domain.Entities;

public interface IRedisManager
{
    RedisInfo ReadRedisConnectionInfo(string configFilePath);
    bool CheckStatus();
    void Clear();
}
