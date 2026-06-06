using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Site;

internal static class SyncFolderResolver
{
    internal static IReadOnlyList<string> Resolve(ServerInfo server, string sitePath)
    {
        if (server.FileCopyFolderPaths.Count > 0)
        {
            return server.FileCopyFolderPaths;
        }

        return ["."];
    }
}
