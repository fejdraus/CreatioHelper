using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
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
    /// Advanced glob pattern matching compatible with Syncthing/gitignore behavior.
    /// Supports: *, **, ?, [abc], [!abc], [a-z]
    /// </summary>
    private bool MatchesSimple(string path, string pattern)
    {
        // Handle directory patterns ending with /
        if (pattern.EndsWith('/'))
        {
            var dirPattern = pattern[..^1]; // Remove trailing slash
            // Match if path starts with the directory pattern or equals it
            var comparison = IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return path.StartsWith(dirPattern + "/", comparison) ||
                   path.Equals(dirPattern, comparison) ||
                   GlobMatch(path, dirPattern);
        }

        // Try full path match first
        if (GlobMatch(path, pattern))
            return true;

        // For non-rooted patterns, also try matching against filename only
        // Use the already-normalized path parameter to extract filename
        if (!IsRooted && !pattern.Contains('/'))
        {
            var filename = path.Contains('/')
                ? path[(path.LastIndexOf('/') + 1)..]
                : path;
            if (GlobMatch(filename, pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Full gitignore/Syncthing-compatible glob pattern matching.
    /// Supports:
    /// - * matches zero or more characters (except /)
    /// - ** matches zero or more directories
    /// - ? matches exactly one character (except /)
    /// - [abc] matches any character in the set
    /// - [!abc] or [^abc] matches any character NOT in the set
    /// - [a-z] matches character ranges
    /// </summary>
    private bool GlobMatch(string input, string pattern)
    {
        var regex = GlobToRegex(pattern);
        var options = IsCaseInsensitive
            ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            : RegexOptions.CultureInvariant;

        try
        {
            return Regex.IsMatch(input, regex, options, TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException)
        {
            // Pattern took too long, treat as no match
            return false;
        }
    }

    /// <summary>
    /// Converts a gitignore-style glob pattern to a regular expression.
    /// </summary>
    private static string GlobToRegex(string pattern)
    {
        var sb = new StringBuilder();
        sb.Append('^');

        var i = 0;
        var len = pattern.Length;

        while (i < len)
        {
            var c = pattern[i];

            switch (c)
            {
                case '*':
                    // Check for **
                    if (i + 1 < len && pattern[i + 1] == '*')
                    {
                        // Check for **/ pattern (matches zero or more directories)
                        if (i + 2 < len && pattern[i + 2] == '/')
                        {
                            // **/ matches zero or more directories including empty
                            sb.Append("(?:.*/)?");
                            i += 3;
                        }
                        // Check for /** at the end (matches everything under a directory)
                        else if (i > 0 && pattern[i - 1] == '/')
                        {
                            // /** matches everything
                            sb.Append(".*");
                            i += 2;
                        }
                        else
                        {
                            // Standalone ** matches everything including /
                            sb.Append(".*");
                            i += 2;
                        }
                    }
                    else
                    {
                        // Single * matches anything except /
                        sb.Append("[^/]*");
                        i++;
                    }
                    break;

                case '?':
                    // ? matches any single character except /
                    sb.Append("[^/]");
                    i++;
                    break;

                case '[':
                    // Character class
                    var classEnd = ParseCharacterClass(pattern, i, sb);
                    if (classEnd > i)
                    {
                        i = classEnd;
                    }
                    else
                    {
                        // Invalid character class, treat [ as literal
                        sb.Append("\\[");
                        i++;
                    }
                    break;

                case '/':
                    // Path separator
                    sb.Append('/');
                    i++;
                    break;

                // Regex metacharacters that need escaping
                case '\\':
                case '.':
                case '+':
                case '^':
                case '$':
                case '|':
                case '(':
                case ')':
                case '{':
                case '}':
                    sb.Append('\\');
                    sb.Append(c);
                    i++;
                    break;

                default:
                    sb.Append(c);
                    i++;
                    break;
            }
        }

        sb.Append('$');
        return sb.ToString();
    }

    /// <summary>
    /// Parses a character class [abc], [!abc], [^abc], [a-z] and appends to StringBuilder.
    /// Returns the index after the closing ], or the original index if not a valid class.
    /// </summary>
    private static int ParseCharacterClass(string pattern, int start, StringBuilder sb)
    {
        var len = pattern.Length;
        if (start >= len || pattern[start] != '[')
            return start;

        var i = start + 1;
        if (i >= len)
            return start; // Unterminated [

        // Check for negation
        var negate = false;
        if (pattern[i] == '!' || pattern[i] == '^')
        {
            negate = true;
            i++;
        }

        // Allow ] as first character in class
        var classChars = new StringBuilder();
        if (i < len && pattern[i] == ']')
        {
            classChars.Append("\\]");
            i++;
        }

        // Parse until closing ]
        while (i < len && pattern[i] != ']')
        {
            var c = pattern[i];

            // Check for range (a-z)
            if (i + 2 < len && pattern[i + 1] == '-' && pattern[i + 2] != ']')
            {
                var rangeStart = c;
                var rangeEnd = pattern[i + 2];

                // Escape special regex chars in ranges
                if (NeedsEscapeInClass(rangeStart))
                    classChars.Append('\\');
                classChars.Append(rangeStart);

                classChars.Append('-');

                if (NeedsEscapeInClass(rangeEnd))
                    classChars.Append('\\');
                classChars.Append(rangeEnd);

                i += 3;
            }
            else
            {
                // Single character
                if (NeedsEscapeInClass(c))
                    classChars.Append('\\');
                classChars.Append(c);
                i++;
            }
        }

        // Check for valid closing ]
        if (i >= len || pattern[i] != ']')
            return start; // Unterminated character class

        // Don't match / in character classes (Syncthing behavior)
        if (negate)
        {
            // [^.../] - negated class that also excludes /
            sb.Append("[^/");
            sb.Append(classChars);
            sb.Append(']');
        }
        else
        {
            // Character class without /
            sb.Append('[');
            sb.Append(classChars);
            sb.Append(']');
        }

        return i + 1; // Return position after ]
    }

    /// <summary>
    /// Determines if a character needs escaping inside a regex character class.
    /// </summary>
    private static bool NeedsEscapeInClass(char c)
    {
        return c == '\\' || c == ']' || c == '^' || c == '-';
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
    /// Checks if a line is a comment (starts with // or # but not #include/#escape)
    /// </summary>
    private static bool IsComment(string line)
    {
        if (line.StartsWith("//"))
            return true;

        // # is a comment unless followed by specific directives
        if (line.StartsWith('#'))
        {
            // Not a comment if it's a directive
            if (line.StartsWith("#include", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#escape="))
            {
                return false;
            }
            return true;
        }

        return false;
    }

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
        if (string.IsNullOrEmpty(trimmedLine) || IsComment(trimmedLine))
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

    /// <summary>
    /// Unescapes a pattern string using the specified escape character.
    /// Handles escaped special characters like *, ?, [, and the escape char itself.
    /// </summary>
    public static string UnescapePattern(string pattern, char escapeChar)
    {
        if (string.IsNullOrEmpty(pattern) || !pattern.Contains(escapeChar))
            return pattern;

        var result = new StringBuilder(pattern.Length);
        var i = 0;

        while (i < pattern.Length)
        {
            if (pattern[i] == escapeChar && i + 1 < pattern.Length)
            {
                var nextChar = pattern[i + 1];
                // Unescape special glob characters and the escape char itself
                if (nextChar == '*' || nextChar == '?' || nextChar == '[' ||
                    nextChar == ']' || nextChar == escapeChar || nextChar == '!' ||
                    nextChar == '{' || nextChar == '}' || nextChar == '#')
                {
                    result.Append(nextChar);
                    i += 2;
                    continue;
                }
            }

            result.Append(pattern[i]);
            i++;
        }

        return result.ToString();
    }

    /// <summary>
    /// Checks if a character at the given position is escaped.
    /// </summary>
    public static bool IsEscaped(string pattern, int index, char escapeChar)
    {
        if (index <= 0)
            return false;

        // Count consecutive escape characters before this position
        int escapeCount = 0;
        int pos = index - 1;
        while (pos >= 0 && pattern[pos] == escapeChar)
        {
            escapeCount++;
            pos--;
        }

        // If odd number of escape chars, the character is escaped
        return escapeCount % 2 == 1;
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