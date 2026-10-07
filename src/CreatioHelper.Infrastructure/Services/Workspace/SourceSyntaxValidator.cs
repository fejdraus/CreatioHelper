using System.Collections.Concurrent;
using System.Globalization;
using Acornima;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CreatioHelper.Infrastructure.Services.Workspace;

public class SourceSyntaxValidator
{
    private const int MaxFileBytes = 5 * 1024 * 1024;

    private static readonly string[] SkippedSegments =
    {
        "node_modules", "obj", "bin", ".vs", ".git", ".svn", "packages"
    };

    public List<string> Validate(string pkgPath)
    {
        var problems = new ConcurrentBag<(string Path, string Message)>();

        var files = EnumerateSourceFiles(pkgPath).ToList();
        if (files.Count == 0)
        {
            return new List<string>();
        }

        Parallel.ForEach(files, file =>
        {
            var problem = Inspect(file, pkgPath);
            if (problem != null)
            {
                problems.Add((file, problem));
            }
        });

        return problems
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Message)
            .ToList();
    }

    private static IEnumerable<string> EnumerateSourceFiles(string pkgPath)
    {
        foreach (var file in Directory.EnumerateFiles(pkgPath, "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            var isCSharp = extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);
            var isJavaScript = extension.Equals(".js", StringComparison.OrdinalIgnoreCase);
            if (!isCSharp && !isJavaScript)
            {
                continue;
            }

            if (IsSkipped(file, pkgPath))
            {
                continue;
            }

            if (isJavaScript && !IsSchemaSource(file, pkgPath))
            {
                continue;
            }

            yield return file;
        }
    }

    private static bool IsSchemaSource(string file, string pkgPath)
    {
        var segments = Path.GetRelativePath(pkgPath, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "Schemas", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSkipped(string file, string pkgPath)
    {
        var relative = Path.GetRelativePath(pkgPath, file);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            foreach (var skipped in SkippedSegments)
            {
                if (string.Equals(segments[i], skipped, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (IsMinified(file))
        {
            return true;
        }

        try
        {
            return new FileInfo(file).Length > MaxFileBytes;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static bool IsMinified(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        return name.EndsWith(".min", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".hash=", StringComparison.OrdinalIgnoreCase);
    }

    private string? Inspect(string file, string pkgPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (IOException ex)
        {
            return $"{Path.GetRelativePath(pkgPath, file)}: could not be read - {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"{Path.GetRelativePath(pkgPath, file)}: could not be read - {ex.Message}";
        }

        return Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase)
            ? InspectCSharp(text, file, pkgPath)
            : InspectJavaScript(text, file, pkgPath);
    }

    private static string? InspectCSharp(string text, string file, string pkgPath)
    {
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest));
        foreach (var diagnostic in tree.GetDiagnostics())
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error)
            {
                continue;
            }

            var line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
            return $"{Path.GetRelativePath(pkgPath, file)}({line}): {diagnostic.Id} {diagnostic.GetMessage(CultureInfo.InvariantCulture)}";
        }

        return null;
    }

    private static string? InspectJavaScript(string text, string file, string pkgPath)
    {
        var parser = new Parser(new ParserOptions { Tolerant = false });

        try
        {
            parser.ParseScript(text);
            return null;
        }
        catch (SyntaxErrorException)
        {
        }

        try
        {
            parser.ParseModule(text);
            return null;
        }
        catch (SyntaxErrorException ex)
        {
            if (HasStyleSibling(file))
            {
                return null;
            }

            var line = ex.Error.LineNumber;
            return $"{Path.GetRelativePath(pkgPath, file)}({line}): {ex.Error.Description}";
        }
    }

    private static bool HasStyleSibling(string file)
    {
        var withoutExtension = Path.Combine(
            Path.GetDirectoryName(file) ?? string.Empty,
            Path.GetFileNameWithoutExtension(file));
        return File.Exists(withoutExtension + ".less") || File.Exists(withoutExtension + ".css");
    }
}
