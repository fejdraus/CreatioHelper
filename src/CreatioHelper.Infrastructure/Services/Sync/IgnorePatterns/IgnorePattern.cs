using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace CreatioHelper.Infrastructure.Services.Sync.IgnorePatterns;

/// <summary>
/// Represents a single ignore pattern with compiled matcher and result flags
/// Compatible with Syncthing's Pattern struct
/// </summary>
public class IgnorePattern
{
    /// <summary>
    /// Original pattern string as read from .stignore
    /// </summary>
    public string Pattern { get; }
    
    /// <summary>
    /// Compiled glob matcher for performance
    /// </summary>
    public Matcher Matcher { get; }
    
    /// <summary>
    /// Result flags for this pattern
    /// </summary>
    public IgnoreResult Result { get; }
    
    /// <summary>
    /// Whether this pattern applies only to root directory
    /// </summary>
    public bool IsRooted { get; }
    
    /// <summary>
    /// Whether this pattern uses case-insensitive matching
    /// </summary>
    public bool IsCaseInsensitive { get; }

    public IgnorePattern(string pattern, IgnoreResult result, bool isRooted, bool isCaseInsensitive)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Result = result;
        IsRooted = isRooted;
        IsCaseInsensitive = isCaseInsensitive;
        Matcher = CreateMatcher(pattern, isCaseInsensitive);
    }
    
    /// <summary>
    /// Creates a glob matcher for the pattern
    /// </summary>
    private static Matcher CreateMatcher(string pattern, bool caseInsensitive)
    {
        var matcher = new Matcher(caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        
        // Handle different pattern types
        if (pattern.EndsWith('/'))
        {
            // Directory pattern - match directory and all its contents
            matcher.AddInclude(pattern + "**");
        }
        else
        {
            matcher.AddInclude(pattern);
        }
        
        return matcher;
    }
    
    /// <summary>
    /// Tests if this pattern matches the given path
    /// </summary>
    public bool Matches(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
            
        // Normalize path separators to forward slashes (Syncthing standard)
        var normalizedPath = path.Replace('\\', '/');
        
        // Simple pattern matching compatible with Syncthing
        return MatchesSimple(normalizedPath, Pattern);
    }
    
    /// <summary>
    /// Simple glob pattern matching similar to Syncthing behavior
    /// </summary>
    private bool MatchesSimple(string path, string pattern)
    {
        // Handle directory patterns ending with /
        if (pattern.EndsWith('/'))
        {
            var dirPattern = pattern[..^1]; // Remove trailing slash
            // Match if path starts with the directory pattern
            return path.StartsWith(dirPattern + "/", IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                   path.Equals(dirPattern, IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        
        // Simple glob patterns
        if (pattern.Contains('*'))
        {
            return IsGlobMatch(path, pattern);
        }
        
        // Exact match
        return path.Equals(pattern, IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
               (!IsRooted && Path.GetFileName(path).Equals(pattern, IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }
    
    /// <summary>
    /// Simple glob matching for * patterns
    /// </summary>
    private bool IsGlobMatch(string input, string pattern)
    {
        var comparison = IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        
        if (pattern == "*")
            return true;
            
        if (pattern.StartsWith("*."))
        {
            var extension = pattern[2..];
            return input.EndsWith("." + extension, comparison) ||
                   (!IsRooted && Path.GetFileName(input).EndsWith("." + extension, comparison));
        }
        
        // Convert glob to regex for complex patterns
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
            
        var options = IsCaseInsensitive ? 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase : 
            System.Text.RegularExpressions.RegexOptions.None;
            
        var matches = System.Text.RegularExpressions.Regex.IsMatch(input, regexPattern, options);
        if (matches) return true;
        
        // Try filename only for non-rooted patterns
        if (!IsRooted)
        {
            var filename = Path.GetFileName(input);
            return System.Text.RegularExpressions.Regex.IsMatch(filename, regexPattern, options);
        }
        
        return false;
    }
    
    public override string ToString()
        => $"Pattern: {Pattern}, Result: {Result}, Rooted: {IsRooted}, CaseInsensitive: {IsCaseInsensitive}";
}

/// <summary>
/// Factory for creating ignore patterns from .stignore file lines
/// Compatible with Syncthing's pattern parsing logic
/// </summary>
public static class IgnorePatternFactory
{
    private static readonly Regex EscapeCharPattern = new(@"^#escape=(.)", RegexOptions.Compiled);
    private static readonly Regex IncludePattern = new(@"^#include\s+(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    /// <summary>
    /// Parses a single line from .stignore file into ignore patterns
    /// Handles prefixes: !, (?i), (?d) and creates appropriate patterns
    /// </summary>
    public static ParsedLine ParseLine(string line, char escapeChar = '\\')
    {
        if (string.IsNullOrEmpty(line))
            return new ParsedLine();
            
        var trimmedLine = line.Trim();
        
        // Skip empty lines and comments
        if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//"))
            return new ParsedLine();
        
        // Handle escape character definition
        var escapeMatch = EscapeCharPattern.Match(trimmedLine);
        if (escapeMatch.Success)
        {
            return new ParsedLine(newEscapeChar: escapeMatch.Groups[1].Value[0]);
        }
        
        // Handle include directive
        var includeMatch = IncludePattern.Match(trimmedLine);
        if (includeMatch.Success)
        {
            return new ParsedLine(includeFile: includeMatch.Groups[1].Value.Trim());
        }
        
        return ParsePattern(trimmedLine, escapeChar);
    }
    
    private static ParsedLine ParsePattern(string line, char escapeChar)
    {
        var result = IgnoreResult.Ignored; // Default: ignored
        var patterns = new List<IgnorePattern>();
        var originalLine = line;
        
        // Track which prefixes we've seen to prevent duplicates
        var seenNegate = false;
        var seenCaseInsensitive = false;
        var seenDeletable = false;
        
        // Process prefixes in any order but only once each
        bool hasPrefix;
        do
        {
            hasPrefix = false;
            
            // Handle negation (!)
            if (line.StartsWith("!") && !seenNegate)
            {
                seenNegate = true;
                hasPrefix = true;
                result = result.ToggleIgnored();
                line = line[1..];
            }
            // Handle case insensitive (?i)
            else if (line.StartsWith("(?i)") && !seenCaseInsensitive)
            {
                seenCaseInsensitive = true;
                hasPrefix = true;
                result = result.WithFoldCase();
                line = line[4..];
            }
            // Handle deletable (?d)
            else if (line.StartsWith("(?d)") && !seenDeletable)
            {
                seenDeletable = true;
                hasPrefix = true;
                result = result.WithDeletable();
                line = line[4..];
            }
        } while (hasPrefix && !string.IsNullOrEmpty(line));
        
        if (string.IsNullOrEmpty(line))
            return new ParsedLine(); // Invalid pattern after prefix processing
        
        // Determine if pattern is case insensitive
        var isCaseInsensitive = seenCaseInsensitive || 
                               RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                               RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        
        // Process the actual pattern
        if (line.StartsWith("/"))
        {
            // Rooted pattern - matches only in root directory
            var pattern = line[1..]; // Remove leading slash
            patterns.Add(new IgnorePattern(pattern, result, isRooted: true, isCaseInsensitive));
        }
        else if (line.StartsWith("**/"))
        {
            // Explicitly rooted recursive pattern
            var pattern = line[3..]; // Remove "**/""
            patterns.Add(new IgnorePattern(pattern, result, isRooted: false, isCaseInsensitive));
            patterns.Add(new IgnorePattern("**/" + pattern, result, isRooted: false, isCaseInsensitive));
        }
        else
        {
            // Non-rooted pattern - create both local and recursive versions
            // This matches Syncthing's behavior of creating duplicate patterns
            patterns.Add(new IgnorePattern(line, result, isRooted: false, isCaseInsensitive));
            
            // Also create a recursive version unless pattern already starts with **
            if (!line.StartsWith("**"))
            {
                patterns.Add(new IgnorePattern("**/" + line, result, isRooted: false, isCaseInsensitive));
            }
        }
        
        return new ParsedLine(patterns.ToArray());
    }
}

/// <summary>
/// Result of parsing a single line from .stignore file
/// </summary>
public class ParsedLine
{
    /// <summary>
    /// Patterns parsed from this line
    /// </summary>
    public IgnorePattern[] Patterns { get; }
    
    /// <summary>
    /// New escape character defined by #escape directive
    /// </summary>
    public char? NewEscapeChar { get; }
    
    /// <summary>
    /// File to include via #include directive
    /// </summary>
    public string? IncludeFile { get; }
    
    /// <summary>
    /// Whether this line contained any meaningful content
    /// </summary>
    public bool IsEmpty => Patterns.Length == 0 && NewEscapeChar == null && IncludeFile == null;
    
    public ParsedLine(IgnorePattern[]? patterns = null, char? newEscapeChar = null, string? includeFile = null)
    {
        Patterns = patterns ?? Array.Empty<IgnorePattern>();
        NewEscapeChar = newEscapeChar;
        IncludeFile = includeFile;
    }
}