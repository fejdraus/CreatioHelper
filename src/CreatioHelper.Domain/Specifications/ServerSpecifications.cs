using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.Entities;
using System.Linq.Expressions;

namespace CreatioHelper.Domain.Specifications;

public class ServerCanBeStoppedSpecification : Specification<ServerInfo>
{
    public override Expression<Func<ServerInfo, bool>> ToExpression()
    {
        return server => !string.IsNullOrEmpty(server.PoolName);
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
