namespace CreatioHelper.Application.Interfaces;

public interface IRedisManagerFactory
{
    IRedisManager Create(string sitePath);
}
