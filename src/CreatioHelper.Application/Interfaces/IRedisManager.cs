namespace CreatioHelper.Application.Interfaces;

using CreatioHelper.Domain.Entities;

public interface IRedisManager
{
    RedisInfo ReadRedisConnectionInfo(string configFilePath);
    bool CheckStatus();
    void Clear();
}
