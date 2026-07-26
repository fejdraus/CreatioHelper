using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CreatioHelper.UnitTests.Localization;

public class LocalizationCoverageTests
{
    private static readonly string[] ResourceFiles =
    {
        "Localization.resx",
        "Localization.ru.resx",
        "Localization.uk.resx"
    };

    private static readonly Regex KeyUsage = new(
        @"\bL\[\s*""(?<key>[A-Za-z0-9_]+)""",
        RegexOptions.Compiled);

    [Fact]
    public void EveryKeyUsedInMarkupExistsInEveryResourceFile()
    {
        var webUi = GetWebUiDirectory();
        var used = CollectUsedKeys(webUi);

        Assert.NotEmpty(used);

        var missing = new List<string>();

        foreach (var file in ResourceFiles)
        {
            var declared = ReadKeys(Path.Combine(webUi, "Resources", file));
            var gaps = used.Where(k => !declared.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

            if (gaps.Count > 0)
            {
                missing.Add($"{file}: {gaps.Count} missing -> {string.Join(", ", gaps)}");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void TranslationsDoNotDeclareKeysMissingFromTheBaseResource()
    {
        var webUi = GetWebUiDirectory();
        var baseKeys = ReadKeys(Path.Combine(webUi, "Resources", ResourceFiles[0]));
        var used = CollectUsedKeys(webUi);

        var orphans = new List<string>();

        foreach (var file in ResourceFiles.Skip(1))
        {
            var declared = ReadKeys(Path.Combine(webUi, "Resources", file));
            var extra = declared
                .Where(k => !baseKeys.Contains(k) && used.Contains(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            if (extra.Count > 0)
            {
                orphans.Add($"{file}: {extra.Count} keys are used by the UI but absent from the base resource -> {string.Join(", ", extra)}");
            }
        }

        Assert.True(orphans.Count == 0, string.Join(Environment.NewLine, orphans));
    }

    [Fact]
    public void ResourceKeysAreUniqueIgnoringCase()
    {
        var webUi = GetWebUiDirectory();
        var collisions = new List<string>();

        foreach (var file in ResourceFiles)
        {
            var duplicates = XDocument.Load(Path.Combine(webUi, "Resources", file))
                .Root!
                .Elements("data")
                .Select(e => e.Attribute("name")?.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .GroupBy(n => n!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => string.Join("/", g))
                .ToList();

            if (duplicates.Count > 0)
            {
                collisions.Add($"{file}: {string.Join(", ", duplicates)}");
            }
        }

        Assert.True(collisions.Count == 0, string.Join(Environment.NewLine, collisions));
    }

    private static HashSet<string> CollectUsedKeys(string webUi)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in EnumerateSourceFiles(webUi))
        {
            foreach (Match match in KeyUsage.Matches(File.ReadAllText(path)))
            {
                keys.Add(match.Groups["key"].Value);
            }
        }

        return keys;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string webUi)
    {
        return Directory
            .EnumerateFiles(webUi, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> ReadKeys(string resxPath)
    {
        Assert.True(File.Exists(resxPath), $"Resource file not found: {resxPath}");

        return XDocument.Load(resxPath)
            .Root!
            .Elements("data")
            .Select(e => e.Attribute("name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.Ordinal)!;
    }

    private static string GetWebUiDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CreatioHelper.WebUI");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/CreatioHelper.WebUI from " + AppContext.BaseDirectory);
    }
}
