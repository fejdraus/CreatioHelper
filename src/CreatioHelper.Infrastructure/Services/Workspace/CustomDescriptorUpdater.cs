using System.Text.Json;
using System.Text.Json.Nodes;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Workspace;

public class CustomDescriptorUpdater : ICustomDescriptorUpdater
{
    private readonly IOutputWriter _output;

    public CustomDescriptorUpdater(IOutputWriter output)
    {
        _output = output;
    }

    public int RemoveDependencies(string sitePath, string packageNamesList)
    {
        if (string.IsNullOrWhiteSpace(packageNamesList))
        {
            return 0;
        }

        var names = packageNamesList
            .Split(new[] { ',', '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.Ordinal);

        if (names.Count == 0)
        {
            return 0;
        }

        var descriptorPath = Path.Combine(sitePath, "Terrasoft.WebApp", "Terrasoft.Configuration", "Pkg", "Custom", "descriptor.json");
        if (!File.Exists(descriptorPath))
        {
            _output.WriteLine($"[INFO] Custom descriptor.json not found at {descriptorPath}, skipping cleanup.");
            return 0;
        }

        try
        {
            var json = File.ReadAllText(descriptorPath);
            var root = JsonNode.Parse(json);
            if (root is not JsonObject rootObj)
            {
                _output.WriteLine("[WARNING] Custom descriptor.json root is not a JSON object.");
                return 0;
            }

            if (rootObj["Descriptor"] is not JsonObject descriptor)
            {
                _output.WriteLine("[WARNING] Custom descriptor.json has no 'Descriptor' object.");
                return 0;
            }

            if (descriptor["DependsOn"] is not JsonArray dependsOn)
            {
                return 0;
            }

            int removed = 0;
            var removedNames = new List<string>();
            for (int i = dependsOn.Count - 1; i >= 0; i--)
            {
                if (dependsOn[i] is JsonObject depObj && depObj["Name"]?.GetValue<string>() is string depName && names.Contains(depName))
                {
                    dependsOn.RemoveAt(i);
                    removedNames.Add(depName);
                    removed++;
                }
            }

            if (removed == 0)
            {
                return 0;
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var updatedJson = root.ToJsonString(options);
            File.WriteAllText(descriptorPath, updatedJson);

            _output.WriteLine($"[OK] Removed {removed} dependency entry(s) from Custom/descriptor.json:");
            foreach (var name in removedNames.OrderBy(n => n, StringComparer.Ordinal))
            {
                _output.WriteLine($"  - {name}");
            }
            return removed;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to update Custom/descriptor.json: {ex.Message}");
            return 0;
        }
    }
}
