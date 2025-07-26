using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Redis;

public class RedisManagerFactory(IOutputWriter output) : IRedisManagerFactory
{
    private readonly IOutputWriter _output = output;

    public IRedisManager Create(string sitePath)
    {
        return new RedisManager(_output, sitePath);
    }
}
