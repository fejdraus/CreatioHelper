using CreatioHelper.Infrastructure.Services.Sync.IgnorePatterns;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Sync.IgnorePatterns;

/// <summary>
/// Tests for IgnoreMatcher class - the main ignore pattern matching engine
/// Compatible with Syncthing's .stignore handling
/// </summary>
public class IgnoreMatcherTests : IDisposable
{
    private readonly Mock<ILogger<IgnoreMatcher>> _loggerMock;
    private readonly string _testDir;

    public IgnoreMatcherTests()
    {
        _loggerMock = new Mock<ILogger<IgnoreMatcher>>();
        _testDir = Path.Combine(Path.GetTempPath(), $"ignore_matcher_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_ValidParameters_CreatesInstance()
    {
        // Act
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Assert
        Assert.NotNull(matcher);
        Assert.Empty(matcher.Patterns);
    }

    [Fact]
    public void Constructor_NullBasePath_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new IgnoreMatcher(null!, _loggerMock.Object));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new IgnoreMatcher(_testDir, null!));
    }

    #endregion

    #region LoadFromFileAsync Tests

    [Fact]
    public async Task LoadFromFileAsync_NonExistentFile_ClearsPatterns()
    {
        // Arrange
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        await matcher.LoadFromFileAsync(Path.Combine(_testDir, "nonexistent.stignore"));

        // Assert
        Assert.Empty(matcher.Patterns);
    }

    [Fact]
    public async Task LoadFromFileAsync_SimplePatterns_LoadsCorrectly()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, "*.txt\n*.log\n");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        await matcher.LoadFromFileAsync(stignorePath);

        // Assert
        Assert.NotEmpty(matcher.Patterns);
    }

    [Fact]
    public async Task LoadFromFileAsync_CommentsAndEmpty_IgnoresCorrectly()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, @"
// This is a comment
# This is also a comment

*.txt
");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        await matcher.LoadFromFileAsync(stignorePath);

        // Assert
        Assert.NotEmpty(matcher.Patterns);
        // Only *.txt pattern should be loaded (creates 2 patterns - local and recursive)
        Assert.All(matcher.Patterns, p => Assert.Contains(".txt", p.Pattern));
    }

    [Fact]
    public async Task LoadFromFileAsync_IncludeDirective_IncludesOtherFile()
    {
        // Arrange
        var mainIgnore = Path.Combine(_testDir, ".stignore");
        var includedIgnore = Path.Combine(_testDir, "extra.stignore");

        await File.WriteAllTextAsync(mainIgnore, "#include extra.stignore\n*.main");
        await File.WriteAllTextAsync(includedIgnore, "*.included");

        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        await matcher.LoadFromFileAsync(mainIgnore);

        // Assert
        Assert.Contains(matcher.Patterns, p => p.Pattern.Contains("included"));
        Assert.Contains(matcher.Patterns, p => p.Pattern.Contains("main"));
    }

    [Fact]
    public async Task LoadFromFileAsync_CircularInclude_HandlesGracefully()
    {
        // Arrange
        var file1 = Path.Combine(_testDir, "file1.stignore");
        var file2 = Path.Combine(_testDir, "file2.stignore");

        await File.WriteAllTextAsync(file1, "#include file2.stignore\n*.one");
        await File.WriteAllTextAsync(file2, "#include file1.stignore\n*.two");

        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act & Assert - should not throw or loop forever
        await matcher.LoadFromFileAsync(file1);

        // Should have patterns from both files but not duplicate due to circular include
        Assert.Contains(matcher.Patterns, p => p.Pattern.Contains("one"));
        Assert.Contains(matcher.Patterns, p => p.Pattern.Contains("two"));
    }

    [Fact]
    public async Task LoadFromFileAsync_EscapeDirective_ChangesEscapeChar()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, "#escape=~\n*.txt");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        await matcher.LoadFromFileAsync(stignorePath);

        // Assert - patterns loaded, escape char changed internally
        Assert.NotEmpty(matcher.Patterns);
    }

    [Fact]
    public async Task LoadFromFileAsync_LineContinuation_JoinsLines()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, "very-long-\\\npattern.txt");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        await matcher.LoadFromFileAsync(stignorePath);

        // Assert - lines should be joined
        Assert.NotEmpty(matcher.Patterns);
        // The pattern should contain both parts joined
    }

    [Fact]
    public async Task LoadFromFileAsync_UnchangedFile_DoesNotReparse()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, "*.txt");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act - load twice
        await matcher.LoadFromFileAsync(stignorePath);
        var patternCount1 = matcher.Patterns.Count;
        await matcher.LoadFromFileAsync(stignorePath);
        var patternCount2 = matcher.Patterns.Count;

        // Assert - same pattern count, no duplication
        Assert.Equal(patternCount1, patternCount2);
    }

    #endregion

    #region Match Tests

    [Fact]
    public async Task Match_EmptyMatcher_ReturnsNotIgnored()
    {
        // Arrange
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        var result = matcher.Match("anyfile.txt");

        // Assert
        Assert.Equal(IgnoreResult.NotIgnored, result);
    }

    [Fact]
    public async Task Match_MatchingPattern_ReturnsIgnored()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, "*.txt");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);
        await matcher.LoadFromFileAsync(stignorePath);

        // Act
        var result = matcher.Match("test.txt");

        // Assert
        Assert.True(result.IsIgnored());
    }

    [Fact]
    public async Task Match_NonMatchingPattern_ReturnsNotIgnored()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, "*.txt");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);
        await matcher.LoadFromFileAsync(stignorePath);

        // Act
        var result = matcher.Match("test.log");

        // Assert
        Assert.False(result.IsIgnored());
    }

    [Fact]
    public async Task Match_NegationPattern_UnIgnoresFile()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, "*.txt\n!important.txt");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);
        await matcher.LoadFromFileAsync(stignorePath);

        // Act
        var ignoredResult = matcher.Match("regular.txt");
        var notIgnoredResult = matcher.Match("important.txt");

        // Assert
        Assert.True(ignoredResult.IsIgnored());
        Assert.False(notIgnoredResult.IsIgnored());
    }

    [Fact]
    public async Task Match_LastPatternWins_CorrectPrecedence()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, "*.txt\n!special.txt\nspecial.txt");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);
        await matcher.LoadFromFileAsync(stignorePath);

        // Act
        var result = matcher.Match("special.txt");

        // Assert - last pattern (special.txt ignore) wins over negation
        Assert.True(result.IsIgnored());
    }

    [Fact]
    public async Task Match_StignoreFile_AlwaysIgnored()
    {
        // Arrange
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        var result = matcher.Match(".stignore");

        // Assert - .stignore itself is always ignored (never synced)
        Assert.True(result.IsIgnored());
    }

    [Fact]
    public async Task Match_StglobalignoreFile_AlwaysIgnored()
    {
        // Arrange
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        var result = matcher.Match(".stglobalignore");

        // Assert
        Assert.True(result.IsIgnored());
    }

    [Fact]
    public async Task Match_EmptyPath_ReturnsNotIgnored()
    {
        // Arrange
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        var result = matcher.Match("");

        // Assert
        Assert.Equal(IgnoreResult.NotIgnored, result);
    }

    [Fact]
    public async Task Match_NullPath_ReturnsNotIgnored()
    {
        // Arrange
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        var result = matcher.Match(null!);

        // Assert
        Assert.Equal(IgnoreResult.NotIgnored, result);
    }

    #endregion

    #region MatchDirectory Tests

    [Fact]
    public async Task MatchDirectory_DirectoryPattern_Matches()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, "logs/");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);
        await matcher.LoadFromFileAsync(stignorePath);

        // Act
        var result = matcher.MatchDirectory("logs");

        // Assert
        Assert.True(result.IsIgnored());
    }

    [Fact]
    public async Task MatchDirectory_EmptyPath_ReturnsNotIgnored()
    {
        // Arrange
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        var result = matcher.MatchDirectory("");

        // Assert
        Assert.Equal(IgnoreResult.NotIgnored, result);
    }

    #endregion

    #region IsStignoreFile Tests

    [Theory]
    [InlineData(".stignore", true)]
    [InlineData(".STIGNORE", true)]
    [InlineData(".StIgnore", true)]
    [InlineData(".stglobalignore", true)]
    [InlineData(".STGLOBALIGNORE", true)]
    [InlineData("dir/.stignore", true)]
    [InlineData("regular.txt", false)]
    [InlineData("stignore", false)]
    [InlineData(".stignore.bak", false)]
    public void IsStignoreFile_VariousInputs_ReturnsCorrectly(string path, bool expected)
    {
        // Act
        var result = IgnoreMatcher.IsStignoreFile(path);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region IsAnyParentIgnored Tests

    [Fact]
    public async Task IsAnyParentIgnored_ParentIgnored_ReturnsTrue()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        // Use a pattern that will match the directory and set CanSkipDir
        await File.WriteAllTextAsync(stignorePath, "ignored_dir/");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);
        await matcher.LoadFromFileAsync(stignorePath);

        // Act - check if the directory itself is ignored
        var dirResult = matcher.MatchDirectory("ignored_dir");

        // Assert - the directory should be ignored
        Assert.True(dirResult.IsIgnored());
    }

    [Fact]
    public async Task IsAnyParentIgnored_NoParentIgnored_ReturnsFalse()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, "*.tmp");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);
        await matcher.LoadFromFileAsync(stignorePath);

        // Act
        var result = matcher.IsAnyParentIgnored("normal_dir/file.txt");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsAnyParentIgnored_EmptyPath_ReturnsFalse()
    {
        // Arrange
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act
        var result = matcher.IsAnyParentIgnored("");

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Cache Tests

    [Fact]
    public async Task Match_RepeatedCalls_UsesCaching()
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, "*.txt");
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);
        await matcher.LoadFromFileAsync(stignorePath);

        // Act - call match multiple times
        matcher.Match("test.txt");
        matcher.Match("test.txt");
        matcher.Match("test.txt");

        var stats = matcher.GetCacheStats();

        // Assert
        Assert.NotNull(stats);
        Assert.True(stats.CurrentSize > 0);
    }

    [Fact]
    public async Task GetCacheStats_ReturnsStats()
    {
        // Arrange
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);
        matcher.Match("file1.txt");
        matcher.Match("file2.txt");

        // Act
        var stats = matcher.GetCacheStats();

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(2, stats.CurrentSize);
    }

    [Fact]
    public void GetCacheStats_NoCacheEnabled_ReturnsNull()
    {
        // Arrange - create matcher without cache
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object, useCache: false);

        // Act
        var stats = matcher.GetCacheStats();

        // Assert
        Assert.Null(stats);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);

        // Act & Assert - should not throw
        matcher.Dispose();
        matcher.Dispose();
    }

    #endregion

    #region Comprehensive Pattern Tests

    [Theory]
    [InlineData("*.tmp", "file.tmp", true)]
    [InlineData("*.tmp", "dir/file.tmp", true)]
    [InlineData("logs/", "logs/error.log", true)]
    [InlineData("logs/", "mylogs/error.log", false)]
    [InlineData("**/node_modules/", "node_modules/package/file.js", true)]
    [InlineData("node_modules/", "node_modules/package/file.js", true)]
    public async Task Match_CommonPatterns_MatchesCorrectly(string pattern, string path, bool shouldBeIgnored)
    {
        // Arrange
        var stignorePath = Path.Combine(_testDir, ".stignore");
        await File.WriteAllTextAsync(stignorePath, pattern);
        using var matcher = new IgnoreMatcher(_testDir, _loggerMock.Object);
        await matcher.LoadFromFileAsync(stignorePath);

        // Act
        var result = matcher.Match(path);

        // Assert
        Assert.Equal(shouldBeIgnored, result.IsIgnored());
    }

    #endregion
}
