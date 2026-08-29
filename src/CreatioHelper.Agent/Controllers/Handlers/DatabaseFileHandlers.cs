using CreatioHelper.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CreatioHelper.Agent.Controllers.Handlers;

internal sealed class DatabaseFileHandlers
{
    private readonly ISyncEngine _syncEngine;

    public DatabaseFileHandlers(ISyncEngine syncEngine)
    {
        _syncEngine = syncEngine;
    }

    public async Task<ActionResult<object>> BrowseAsync(
        string folder, string? prefix, bool dirsonly)
    {
        if (string.IsNullOrEmpty(folder))
            return new BadRequestObjectResult(new { error = "folder parameter required" });

        if (!string.IsNullOrEmpty(prefix) && (prefix.Contains("..") || Path.IsPathRooted(prefix)))
            return new BadRequestObjectResult(new { error = "Invalid prefix path" });

        var folderInfo = await _syncEngine.GetFolderAsync(folder);
        if (folderInfo == null)
            return new NotFoundObjectResult(new { error = "folder not found" });

        var basePath = folderInfo.Path;
        var searchPath = string.IsNullOrEmpty(prefix) ? basePath : Path.Combine(basePath, prefix);

        var searchFullPath = Path.GetFullPath(searchPath);
        var folderFullPath = Path.GetFullPath(basePath);

        if (!folderFullPath.EndsWith(Path.DirectorySeparatorChar))
            folderFullPath += Path.DirectorySeparatorChar;

        if (!searchFullPath.Equals(folderFullPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            !searchFullPath.StartsWith(folderFullPath, StringComparison.OrdinalIgnoreCase))
            return new BadRequestObjectResult(new { error = "Invalid prefix path" });

        var entries = new List<object>();

        if (Directory.Exists(searchPath))
        {
            var directoryInfo = new DirectoryInfo(searchPath);

            foreach (var dir in directoryInfo.GetDirectories())
            {
                entries.Add(new
                {
                    name = dir.Name,
                    type = "directory",
                    size = 0,
                    modified = dir.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                });
            }

            if (!dirsonly)
            {
                foreach (var file in directoryInfo.GetFiles())
                {
                    entries.Add(new
                    {
                        name = file.Name,
                        type = "file",
                        size = file.Length,
                        modified = file.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                    });
                }
            }
        }

        return new OkObjectResult(entries.ToArray());
    }

    public async Task<ActionResult<object>> GetFileAsync(string folder, string file)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(file))
            return new BadRequestObjectResult(new { error = "folder and file parameters required" });

        if (file.Contains("..") || Path.IsPathRooted(file))
            return new BadRequestObjectResult(new { error = "Invalid file path" });

        var folderInfo = await _syncEngine.GetFolderAsync(folder);
        if (folderInfo == null)
            return new NotFoundObjectResult(new { error = "folder not found" });

        var filePath = Path.Combine(folderInfo.Path, file);

        var fullPath = Path.GetFullPath(filePath);
        var folderFullPath = Path.GetFullPath(folderInfo.Path);

        if (!folderFullPath.EndsWith(Path.DirectorySeparatorChar))
            folderFullPath += Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(folderFullPath, StringComparison.OrdinalIgnoreCase))
            return new BadRequestObjectResult(new { error = "Invalid file path" });

        if (!File.Exists(filePath))
            return new NotFoundObjectResult(new { error = "file not found" });

        var fileInfo = new FileInfo(filePath);

        return new OkObjectResult(new
        {
            availability = new[] { _syncEngine.DeviceId },
            blocksHash = Array.Empty<byte>(),
            deleted = false,
            invalid = false,
            localFlags = 0,
            modified = fileInfo.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            modifiedBy = _syncEngine.DeviceId,
            name = file,
            noPermissions = false,
            numBlocks = 1,
            permissions = "0644",
            platform = new { },
            sequence = 1000,
            size = fileInfo.Length,
            type = "file",
            version = new
            {
                counters = new[]
                {
                    new { id = 1, value = 1 }
                }
            }
        });
    }
}
