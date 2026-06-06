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

        if (Directory.Exists(Path.Combine(sitePath, "Terrasoft.Configuration")))
        {
            return ["Terrasoft.Configuration"];
        }

        if (Directory.Exists(Path.Combine(sitePath, "Terrasoft.WebApp", "Terrasoft.Configuration")))
        {
            return ["Terrasoft.WebApp/Terrasoft.Configuration", "Terrasoft.WebApp/conf"];
        }

        return [];
    }
}
