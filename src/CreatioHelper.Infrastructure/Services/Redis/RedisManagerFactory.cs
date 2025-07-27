using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Redis;

public class RedisManagerFactory(IOutputWriter output) : IRedisManagerFactory
{
    public IRedisManager Create(string sitePath)
    {
        return new RedisManager(output, sitePath);
    }
}
