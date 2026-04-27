using System.Text.Json;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Workspace;

public class PackageCleaner : IPackageCleaner
{
    private readonly IOutputWriter _output;

    public PackageCleaner(IOutputWriter output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public PackageCleanResult CleanPackages(string packagesPath)
    {
        var result = new PackageCleanResult();

        if (!Directory.Exists(packagesPath))
        {
            _output.WriteLine($"[WARNING] Packages path does not exist: {packagesPath}");
            return result;
        }

        var pkgPath = packagesPath;

        // 0a. Validate all JSON files
        result.InvalidOtherJsonFiles = ValidateAllJsonFiles(pkgPath);
        if (result.HasInvalidOtherJson)
        {
            foreach (var file in result.InvalidOtherJsonFiles)
            {
                _output.WriteLine($"  [ERROR] {file}");
            }
            _output.WriteLine($"Found {result.InvalidOtherJsonFiles.Count} invalid JSON file(s).");
        }

        // 0b. Validate descriptor.json files (structure)
        result.InvalidJsonFiles = ValidateDescriptorJsonFiles(pkgPath);
        if (result.HasInvalidJson)
        {
            foreach (var file in result.InvalidJsonFiles)
            {
                _output.WriteLine($"  [ERROR] {file}");
            }
            _output.WriteLine($"Found {result.InvalidJsonFiles.Count} invalid descriptor.json file(s).");
        }

        // 1. Clean orphan Resources folders
        result.OrphanResourcesDeleted = CleanOrphanResources(pkgPath);
        if (result.OrphanResourcesDeleted > 0)
        {
            _output.WriteLine($"Cleaned orphan Resources folders: {result.OrphanResourcesDeleted}");
        }

        // 2. Remove empty directories
        result.EmptyDirectoriesDeleted = RemoveEmptyDirectories(pkgPath);
        if (result.EmptyDirectoriesDeleted > 0)
        {
            _output.WriteLine($"Removed empty directories: {result.EmptyDirectoriesDeleted}");
        }

        // 3. Clean non-matching .sql files
        result.NonMatchingSqlFilesDeleted = CleanNonMatchingSqlFiles(pkgPath);
        if (result.NonMatchingSqlFilesDeleted > 0)
        {
            _output.WriteLine($"Cleaned non-matching .sql files: {result.NonMatchingSqlFilesDeleted}");
        }

        // 4. Delete folders without descriptor.json
        result.FoldersWithoutDescriptorDeleted = DeleteFoldersWithoutDescriptor(pkgPath);
        if (result.FoldersWithoutDescriptorDeleted > 0)
        {
            _output.WriteLine($"Deleted folders without descriptor.json: {result.FoldersWithoutDescriptorDeleted}");
        }

        // 5. Check circular dependencies
        result.CircularDependencies = CheckCircularDependencies(pkgPath);
        if (result.HasCircularDependencies)
        {
            _output.WriteLine("[WARNING] Circular dependencies detected:");
            foreach (var cycle in result.CircularDependencies)
                _output.WriteLine($"  CYCLE: {cycle}");
        }

        _output.WriteLine("[OK] Package cleaning & validation completed.");

        return result;
    }

    private static readonly char[] MetadataDiffOperators = { '=', '+', '-', '~' };

    private static bool IsExcludedPath(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase) ||
               path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase) ||
               path.Contains($"{sep}.", StringComparison.Ordinal);
    }

    // ============================================================
    // 0a. Validate all JSON files
    // ============================================================
    private List<string> ValidateAllJsonFiles(string pkgPath)
    {
        var invalidFiles = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(pkgPath, "*.json", SearchOption.AllDirectories))
            {
                if (IsExcludedPath(file))
                {
                    continue;
                }
                var error = ValidateJsonFile(file);
                if (error != null)
                {
                    invalidFiles.Add($"{file}: {error}");
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[WARNING] Error scanning JSON files: {ex.Message}");
        }
        return invalidFiles;
    }

    private static string? ValidateJsonFile(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);

            // Empty files are valid (e.g. filter.json with no filter)
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            // Check for merge conflict markers in any file
            if (content.Contains("<<<<<<<") || content.Contains(">>>>>>>"))
            {
                return "File contains merge conflict markers.";
            }

            // Strip BOM if present
            var trimmed = content.TrimStart('\uFEFF').TrimStart();

            // Creatio metadata.json diff/delta format starts with operators: = + - ~
            if (trimmed.Length > 0 && MetadataDiffOperators.Contains(trimmed[0]))
            {
                return ValidateMetadataDiffFormat(trimmed);
            }

            // Creatio JS object literal format (e.g. filter.json with unquoted keys like {_isFilter:false,...})
            if (trimmed.StartsWith('{') && !trimmed.StartsWith("{\""))
            {
                return ValidateBracesBalance(trimmed);
            }

            // Standard JSON
            using var doc = JsonDocument.Parse(content);
            return null;
        }
        catch (JsonException ex)
        {
            return ex.Message;
        }
    }

    private static string? ValidateBracesBalance(string content)
    {
        int braceDepth = 0;
        int bracketDepth = 0;
        bool inString = false;
        char prev = '\0';

        for (int i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (inString)
            {
                if (ch == '"' && prev != '\\')
                {
                    inString = false;
                }
            }
            else
            {
                switch (ch)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        braceDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        bracketDepth--;
                        break;
                }
            }
            if (braceDepth < 0)
            {
                return "Unexpected closing brace '}'.";
            }
            if (bracketDepth < 0)
            {
                return "Unexpected closing bracket ']'.";
            }
            prev = ch;
        }

        if (braceDepth != 0)
        {
            return "Unbalanced braces '{}'.";
        }
        if (bracketDepth != 0)
        {
            return "Unbalanced brackets '[]'.";
        }
        return null;
    }

    private static string? ValidateMetadataDiffFormat(string content)
    {
        var lines = content.Split('\n');
        bool inMultiLineValue = false;
        int braceDepth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (inMultiLineValue)
            {
                foreach (var ch in line)
                {
                    if (ch == '{' || ch == '[')
                    {
                        braceDepth++;
                    }
                    else if (ch == '}' || ch == ']')
                    {
                        braceDepth--;
                    }
                }
                if (braceDepth <= 0)
                {
                    inMultiLineValue = false;
                    braceDepth = 0;
                }
                continue;
            }

            if (!MetadataDiffOperators.Contains(line[0]))
            {
                return $"Line {i + 1}: expected operator (= + - ~), got '{line[0]}'.";
            }

            // Count opening/closing braces to detect multi-line values
            foreach (var ch in line)
            {
                if (ch == '{' || ch == '[')
                {
                    braceDepth++;
                }
                else if (ch == '}' || ch == ']')
                {
                    braceDepth--;
                }
            }
            if (braceDepth > 0)
            {
                inMultiLineValue = true;
            }
            else
            {
                braceDepth = 0;
            }
        }

        if (inMultiLineValue)
        {
            return "Unexpected end of file: unclosed braces in multi-line value.";
        }

        return null;
    }

    // ============================================================
    // 0b. Validate descriptor.json files (structure)
    // ============================================================
    private List<string> ValidateDescriptorJsonFiles(string pkgPath)
    {
        var invalidFiles = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(pkgPath, "descriptor.json", SearchOption.AllDirectories))
            {
                if (IsExcludedPath(file))
                {
                    continue;
                }
                try
                {
                    var content = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(content);
                }
                catch (JsonException ex)
                {
                    invalidFiles.Add($"{file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[WARNING] Error scanning descriptor.json files: {ex.Message}");
        }
        return invalidFiles;
    }

    // ============================================================
    // 1. Clean orphan Resources folders
    // ============================================================
    private int CleanOrphanResources(string pkgPath)
    {
        int deleted = 0;
        try
        {
            var filesSeparator = $"{Path.DirectorySeparatorChar}Files{Path.DirectorySeparatorChar}";
            var resourceDirs = Directory.EnumerateDirectories(pkgPath, "Resources", SearchOption.AllDirectories)
                .Where(r => !r.Contains(filesSeparator, StringComparison.OrdinalIgnoreCase) && !IsExcludedPath(r));

            foreach (var resourcesPath in resourceDirs.ToList())
            {
                var parentPath = Path.GetDirectoryName(resourcesPath)!;
                var schemasPath = Path.Combine(parentPath, "Schemas");

                if (!Directory.Exists(schemasPath))
                {
                    _output.WriteLine($"  Deleting orphan Resources folder: {resourcesPath}");
                    Directory.Delete(resourcesPath, true);
                    deleted++;
                }
                else
                {
                    foreach (var subDir in Directory.EnumerateDirectories(resourcesPath).ToList())
                    {
                        var folderName = Path.GetFileName(subDir);
                        // Remove last extension (e.g., "MySchema.en-US" -> "MySchema")
                        var nameWithoutCulture = Path.GetFileNameWithoutExtension(folderName);
                        var schemaFolderPath = Path.Combine(schemasPath, nameWithoutCulture);
                        if (!Directory.Exists(schemaFolderPath))
                        {
                            _output.WriteLine($"  Deleting orphan resource subfolder: {subDir}");
                            Directory.Delete(subDir, true);
                            deleted++;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[WARNING] Error cleaning orphan Resources: {ex.Message}");
        }
        return deleted;
    }

    // ============================================================
    // 2. Remove empty directories (iteratively until stable)
    // ============================================================
    private int RemoveEmptyDirectories(string pkgPath)
    {
        int totalDeleted = 0;
        bool deletedAny;
        var specialFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Assemblies", "Localization", "Autogenerated", "Lib", "Src" };

        do
        {
            deletedAny = false;
            var directories = Directory.EnumerateDirectories(pkgPath, "*", SearchOption.AllDirectories)
                .Where(d => !IsExcludedPath(d))
                .OrderByDescending(d => d.Length)
                .ToList();

            var toDelete = new List<string>();
            var specialToCheck = new List<(string FullName, string ParentPath)>();

            foreach (var dir in directories)
            {
                if (!Directory.Exists(dir)) continue;
                bool isEmpty = !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();
                var dirName = Path.GetFileName(dir);

                if (specialFolderNames.Contains(dirName) && isEmpty)
                {
                    specialToCheck.Add((dir, Path.GetDirectoryName(dir)!));
                }
                else if (isEmpty)
                {
                    toDelete.Add(dir);
                }
            }

            var toDeleteSet = new HashSet<string>(toDelete, StringComparer.OrdinalIgnoreCase);
            foreach (var (fullName, parentPath) in specialToCheck)
            {
                if (toDeleteSet.Contains(parentPath))
                {
                    toDelete.Add(fullName);
                    toDeleteSet.Add(fullName);
                }
            }

            foreach (var dirPath in toDelete)
            {
                if (Directory.Exists(dirPath))
                {
                    _output.WriteLine($"  Deleted empty folder: {dirPath}");
                    Directory.Delete(dirPath, true);
                    totalDeleted++;
                    deletedAny = true;
                }
            }
        } while (deletedAny);

        return totalDeleted;
    }

    // ============================================================
    // 3. Clean non-matching .sql files
    // ============================================================
    private int CleanNonMatchingSqlFiles(string pkgPath)
    {
        int deleted = 0;
        try
        {
            foreach (var descriptorFile in Directory.EnumerateFiles(pkgPath, "descriptor.json", SearchOption.AllDirectories))
            {
                if (IsExcludedPath(descriptorFile))
                {
                    continue;
                }
                try
                {
                    var json = File.ReadAllText(descriptorFile);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("SqlScript", out var sqlScript))
                        continue;
                    if (!sqlScript.TryGetProperty("Name", out var nameElement))
                        continue;

                    var sqlFileName = nameElement.GetString();
                    if (string.IsNullOrWhiteSpace(sqlFileName))
                        continue;

                    var folderPath = Path.GetDirectoryName(descriptorFile)!;
                    foreach (var sqlFile in Directory.EnumerateFiles(folderPath, "*.sql"))
                    {
                        if (!Path.GetFileNameWithoutExtension(sqlFile)
                                .Equals(sqlFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            _output.WriteLine($"  Deleting non-matching .sql file: {sqlFile}");
                            File.Delete(sqlFile);
                            deleted++;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _output.WriteLine($"  [WARNING] Invalid JSON in {descriptorFile}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[WARNING] Error cleaning .sql files: {ex.Message}");
        }
        return deleted;
    }

    // ============================================================
    // 4. Delete folders without descriptor.json
    // ============================================================
    private int DeleteFoldersWithoutDescriptor(string pkgPath)
    {
        int deleted = 0;
        try
        {
            foreach (var packageDir in Directory.EnumerateDirectories(pkgPath))
            {
                if (IsExcludedPath(packageDir))
                {
                    continue;
                }
                foreach (var subFolderName in new[] { "Data", "Schemas" })
                {
                    var subFolderPath = Path.Combine(packageDir, subFolderName);
                    if (!Directory.Exists(subFolderPath)) continue;

                    foreach (var itemDir in Directory.EnumerateDirectories(subFolderPath).ToList())
                    {
                        var descriptorPath = Path.Combine(itemDir, "descriptor.json");
                        if (!File.Exists(descriptorPath))
                        {
                            _output.WriteLine($"  Deleted folder without descriptor.json: {itemDir}");
                            Directory.Delete(itemDir, true);
                            deleted++;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[WARNING] Error deleting folders without descriptor: {ex.Message}");
        }
        return deleted;
    }

    // ============================================================
    // 5. Check circular dependencies
    // ============================================================
    private List<string> CheckCircularDependencies(string pkgPath)
    {
        var allCycles = new List<string>();
        var packageDirs = Directory.EnumerateDirectories(pkgPath)
            .Where(d => !IsExcludedPath(d))
            .ToList();

        // 5a. Package dependencies
        var (pkgGraph, pkgMap) = BuildPackageGraph(packageDirs);
        var pkgCycles = FindGraphCycles(pkgGraph, pkgMap);
        allCycles.AddRange(pkgCycles);

        // 5b. Schema parent chains
        var (schemaParent, schemaMap, schemaCount) = BuildSchemaParentMap(packageDirs);
        var schemaCycles = FindChainCycles(schemaParent, schemaMap);
        allCycles.AddRange(schemaCycles);

        // 5c. SQL script dependencies
        var (sqlGraph, sqlMap) = BuildSqlGraph(packageDirs);
        var sqlCycles = FindGraphCycles(sqlGraph, sqlMap);
        allCycles.AddRange(sqlCycles);
        return allCycles;
    }

    private (Dictionary<string, List<string>> Graph, Dictionary<string, string> NameMap) BuildPackageGraph(List<string> packageDirs)
    {
        var graph = new Dictionary<string, List<string>>();
        var nameMap = new Dictionary<string, string>();

        foreach (var dir in packageDirs)
        {
            var descPath = Path.Combine(dir, "descriptor.json");
            if (!File.Exists(descPath)) continue;

            try
            {
                var json = File.ReadAllText(descPath);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Descriptor", out var descriptor)) continue;
                if (!descriptor.TryGetProperty("UId", out var uidEl)) continue;

                var uid = uidEl.GetString()!;
                var name = descriptor.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? uid : uid;
                nameMap[uid] = name;

                var deps = new List<string>();
                if (descriptor.TryGetProperty("DependsOn", out var dependsOn) && dependsOn.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dep in dependsOn.EnumerateArray())
                    {
                        if (dep.TryGetProperty("UId", out var depUid))
                            deps.Add(depUid.GetString()!);
                    }
                }
                graph[uid] = deps;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  [WARNING] Failed to parse {descPath}: {ex.Message}");
            }
        }

        return (graph, nameMap);
    }

    private (Dictionary<string, string> ParentMap, Dictionary<string, string> NameMap, int Count) BuildSchemaParentMap(List<string> packageDirs)
    {
        var parentMap = new Dictionary<string, string>();
        var nameMap = new Dictionary<string, string>();
        int count = 0;

        foreach (var dir in packageDirs)
        {
            var schemasPath = Path.Combine(dir, "Schemas");
            if (!Directory.Exists(schemasPath)) continue;

            foreach (var schemaDir in Directory.EnumerateDirectories(schemasPath))
            {
                var descPath = Path.Combine(schemaDir, "descriptor.json");
                if (!File.Exists(descPath)) continue;

                try
                {
                    var json = File.ReadAllText(descPath);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("Descriptor", out var descriptor)) continue;
                    if (!descriptor.TryGetProperty("UId", out var uidEl)) continue;

                    var uid = uidEl.GetString()!;
                    var name = descriptor.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? uid : uid;
                    nameMap[uid] = name;
                    count++;

                    if (descriptor.TryGetProperty("Parent", out var parent) &&
                        parent.TryGetProperty("UId", out var parentUid))
                    {
                        parentMap[uid] = parentUid.GetString()!;
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"  [WARNING] Failed to parse {descPath}: {ex.Message}");
                }
            }
        }

        return (parentMap, nameMap, count);
    }

    private (Dictionary<string, List<string>> Graph, Dictionary<string, string> NameMap) BuildSqlGraph(List<string> packageDirs)
    {
        var graph = new Dictionary<string, List<string>>();
        var nameMap = new Dictionary<string, string>();

        foreach (var dir in packageDirs)
        {
            var sqlPath = Path.Combine(dir, "SqlScripts");
            if (!Directory.Exists(sqlPath)) continue;

            foreach (var sqlDir in Directory.EnumerateDirectories(sqlPath))
            {
                var descPath = Path.Combine(sqlDir, "descriptor.json");
                if (!File.Exists(descPath)) continue;

                try
                {
                    var json = File.ReadAllText(descPath);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("SqlScript", out var sqlScript)) continue;
                    if (!sqlScript.TryGetProperty("UId", out var uidEl)) continue;

                    var uid = uidEl.GetString()!;
                    var name = sqlScript.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? uid : uid;
                    nameMap[uid] = name;

                    var deps = new List<string>();
                    if (sqlScript.TryGetProperty("DependsOn", out var dependsOn) && dependsOn.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var dep in dependsOn.EnumerateArray())
                        {
                            if (dep.TryGetProperty("UId", out var depUid))
                                deps.Add(depUid.GetString()!);
                        }
                    }
                    graph[uid] = deps;
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"  [WARNING] Failed to parse {descPath}: {ex.Message}");
                }
            }
        }

        return (graph, nameMap);
    }

    /// <summary>
    /// DFS cycle detection for directed graphs (UId -> [UId])
    /// </summary>
    private List<string> FindGraphCycles(Dictionary<string, List<string>> graph, Dictionary<string, string> nameMap)
    {
        const int White = 0, Gray = 1, Black = 2;
        var color = graph.Keys.ToDictionary(k => k, _ => White);
        var cycles = new List<string>();
        var path = new List<string>();

        void Dfs(string node)
        {
            color[node] = Gray;
            path.Add(node);

            if (graph.TryGetValue(node, out var neighbors))
            {
                foreach (var next in neighbors)
                {
                    if (!graph.ContainsKey(next)) continue;

                    if (color.TryGetValue(next, out var c) && c == Gray)
                    {
                        var cycleStart = path.IndexOf(next);
                        var cycleNodes = path.Skip(cycleStart).Select(n => nameMap.GetValueOrDefault(n, n));
                        var first = nameMap.GetValueOrDefault(next, next);
                        cycles.Add(string.Join(" -> ", cycleNodes) + " -> " + first);
                    }
                    else if (color.TryGetValue(next, out var c2) && c2 == White)
                    {
                        Dfs(next);
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            color[node] = Black;
        }

        foreach (var node in graph.Keys)
        {
            if (color[node] == White)
                Dfs(node);
        }

        return cycles;
    }

    /// <summary>
    /// Chain-walk cycle detection for single-parent graphs (UId -> UId)
    /// </summary>
    private List<string> FindChainCycles(Dictionary<string, string> parentMap, Dictionary<string, string> nameMap)
    {
        var cycles = new List<string>();
        var visited = new HashSet<string>();

        foreach (var uid in parentMap.Keys)
        {
            if (visited.Contains(uid)) continue;

            var chain = new List<string>();
            var inChain = new HashSet<string>();
            var current = uid;

            while (current != null && !visited.Contains(current))
            {
                if (inChain.Contains(current))
                {
                    var cycleStart = chain.IndexOf(current);
                    var cycleNodes = chain.Skip(cycleStart).Select(n => nameMap.GetValueOrDefault(n, n));
                    var first = nameMap.GetValueOrDefault(current, current);
                    cycles.Add(string.Join(" -> ", cycleNodes) + " -> " + first);
                    break;
                }

                chain.Add(current);
                inChain.Add(current);
                parentMap.TryGetValue(current, out current!);
            }

            foreach (var node in chain)
                visited.Add(node);
        }

        return cycles;
    }
}