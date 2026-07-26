using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CreatioHelper.UnitTests.Localization;

/// <summary>
/// A link to a route that does not exist compiles, deploys and only shows itself
/// as "page not found" once somebody clicks it. The dashboard pointed at /events
/// while the page has always been served from /monitoring/events.
/// </summary>
public class NavigationLinkTests
{
    private static readonly Regex PageDirective = new(@"@page\s+""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex HrefAttribute = new(@"[Hh]ref=""(/[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex NavigateToCall = new(@"NavigateTo\(""(/[^""]*)""", RegexOptions.Compiled);

    private static readonly string[] NonRoutePrefixes =
    {
        "/rest", "/api", "/_framework", "/_content", "/css", "/js", "/images", "/favicon", "/manifest"
    };

    [Fact]
    public void EveryInternalLinkPointsAtADeclaredRoute()
    {
        var files = SourceFiles();
        var routes = DeclaredRoutes(files);

        Assert.NotEmpty(routes);

        var broken = new List<string>();

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var target in Targets(lines[i]))
                {
                    if (IsNonRoute(target) || Matches(target, routes))
                    {
                        continue;
                    }

                    broken.Add($"{Path.GetFileName(file)}:{i + 1} -> {target}");
                }
            }
        }

        Assert.True(broken.Count == 0,
            "Links pointing at routes that do not exist:" + Environment.NewLine +
            string.Join(Environment.NewLine, broken));
    }

    private static IEnumerable<string> Targets(string line)
    {
        foreach (Match m in HrefAttribute.Matches(line))
        {
            yield return m.Groups[1].Value;
        }

        foreach (Match m in NavigateToCall.Matches(line))
        {
            yield return m.Groups[1].Value;
        }
    }

    private static bool IsNonRoute(string target)
    {
        return NonRoutePrefixes.Any(p => target.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(string target, HashSet<string> routes)
    {
        var clean = Normalise(target.Split('?')[0].Split('#')[0]);

        return routes.Contains(clean)
               || routes.Any(r => r != "/" && clean.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalise(string route)
    {
        var withoutParameters = route.Split('{')[0].TrimEnd('/');
        return withoutParameters.Length == 0 ? "/" : withoutParameters;
    }

    private static HashSet<string> DeclaredRoutes(IReadOnlyCollection<string> files)
    {
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            foreach (Match m in PageDirective.Matches(File.ReadAllText(file)))
            {
                routes.Add(Normalise(m.Groups[1].Value));
            }
        }

        return routes;
    }

    private static List<string> SourceFiles()
    {
        var webUi = GetWebUiDirectory();

        return Directory
            .EnumerateFiles(webUi, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();
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
