using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.Entities;
using System.Linq.Expressions;

namespace CreatioHelper.Domain.Specifications;

public class ServerCanBeStoppedSpecification : Specification<ServerInfo>
{
    private readonly DateTime _currentTime;

    public ServerCanBeStoppedSpecification(DateTime currentTime)
    {
        _currentTime = currentTime;
    }

    public override Expression<Func<ServerInfo, bool>> ToExpression()
    {
        // Бизнес-правило: сервер можно остановить только в рабочее время (9:00-18:00)
        var workingHourStart = 9;
        var workingHourEnd = 18;
        
        return server => _currentTime.Hour >= workingHourStart && 
                        _currentTime.Hour < workingHourEnd &&
                        !string.IsNullOrEmpty(server.PoolName);
    }
}

public class ServerIsHealthySpecification : Specification<ServerInfo>
{
    public override Expression<Func<ServerInfo, bool>> ToExpression()
    {
        return server => server.PoolStatus == "Running" && 
                        server.SiteStatus == "Running" &&
                        !server.IsStatusLoading;
    }
}

public class ServerRequiresMaintenanceSpecification : Specification<ServerInfo>
{
    public override Expression<Func<ServerInfo, bool>> ToExpression()
    {
        return server => server.PoolStatus == "Stopped" || 
                        server.SiteStatus == "Stopped" ||
                        server.ServiceStatus == "Stopped";
    }
}
