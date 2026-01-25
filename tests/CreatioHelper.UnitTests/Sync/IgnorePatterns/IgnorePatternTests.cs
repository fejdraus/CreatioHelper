using CreatioHelper.Infrastructure.Services.Sync.IgnorePatterns;

namespace CreatioHelper.UnitTests.Sync.IgnorePatterns;

/// <summary>
/// Tests for IgnorePattern and IgnorePatternFactory classes
/// Compatible with Syncthing's .stignore pattern syntax
/// </summary>
public class IgnorePatternTests
{
    #region Basic Pattern Matching Tests

    [Theory]
    [InlineData("*.txt", "file.txt", true)]
    [InlineData("*.txt", "file.log", false)]
    [InlineData("*.txt", "dir/file.txt", true)]
    [InlineData("test.txt", "test.txt", true)]
    [InlineData("test.txt", "dir/test.txt", true)]
    public void Matches_BasicWildcard_MatchesCorrectly(string pattern, string path, bool expected)
    {
        // Arrange
        var ignorePattern = new IgnorePattern(pattern, IgnoreResult.Ignored, isRooted: false, isCaseInsensitive: false);

        // Act
        var result = ignorePattern.Matches(path);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("**/*.txt", "file.txt", true)]
    [InlineData("**/*.txt", "dir/file.txt", true)]
    [InlineData("**/*.txt", "dir/subdir/file.txt", true)]
    [InlineData("**/test.txt", "test.txt", true)]
    [InlineData("**/test.txt", "a/b/c/test.txt", true)]
    public void Matches_DoubleStarWildcard_MatchesAnyDirectory(string pattern, string path, bool expected)
    {
        // Arrange
        var ignorePattern = new IgnorePattern(pattern, IgnoreResult.Ignored, isRooted: false, isCaseInsensitive: false);

        // Act
        var result = ignorePattern.Matches(path);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "filea.txt", true)]
    [InlineData("file?.txt", "file.txt", false)]
    [InlineData("file?.txt", "file12.txt", false)]
    public void Matches_SingleCharWildcard_MatchesSingleChar(string pattern, string path, bool expected)
    {
        // Arrange
        var ignorePattern = new IgnorePattern(pattern, IgnoreResult.Ignored, isRooted: false, isCaseInsensitive: false);

        // Act
        var result = ignorePattern.Matches(path);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("[abc].txt", "a.txt", true)]
    [InlineData("[abc].txt", "b.txt", true)]
    [InlineData("[abc].txt", "d.txt", false)]
    [InlineData("[a-z].txt", "m.txt", true)]
    [InlineData("[a-z].txt", "5.txt", false)]
    [InlineData("[!abc].txt", "d.txt", true)]
    [InlineData("[!abc].txt", "a.txt", false)]
    [InlineData("[^abc].txt", "d.txt", true)]
    public void Matches_CharacterClass_MatchesCorrectChars(string pattern, string path, bool expected)
    {
        // Arrange
        var ignorePattern = new IgnorePattern(pattern, IgnoreResult.Ignored, isRooted: false, isCaseInsensitive: false);

        // Act
        var result = ignorePattern.Matches(path);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region Directory Pattern Tests

    [Theory]
    [InlineData("dir/", "dir", true)]
    [InlineData("dir/", "dir/file.txt", true)]
    [InlineData("dir/", "other/dir", false)]
    [InlineData("logs/", "logs/error.log", true)]
    [InlineData("logs/", "logs/subdir/file.txt", true)]
    public void Matches_DirectoryPattern_MatchesDirectoryAndContents(string pattern, string path, bool expected)
    {
        // Arrange
        var ignorePattern = new IgnorePattern(pattern, IgnoreResult.Ignored, isRooted: false, isCaseInsensitive: false);

        // Act
        var result = ignorePattern.Matches(path);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region Rooted Pattern Tests

    [Theory]
    [InlineData("test.txt", "test.txt", true)]
    [InlineData("test.txt", "dir/test.txt", false)]
    [InlineData("dir/*.txt", "dir/file.txt", true)]
    [InlineData("dir/*.txt", "other/dir/file.txt", false)]
    public void Matches_RootedPattern_MatchesOnlyFromRoot(string pattern, string path, bool expected)
    {
        // Arrange - rooted patterns start with / in .stignore, which is stripped
        var ignorePattern = new IgnorePattern(pattern, IgnoreResult.Ignored, isRooted: true, isCaseInsensitive: false);

        // Act
        var result = ignorePattern.Matches(path);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region Case Sensitivity Tests

    [Theory]
    [InlineData("Test.TXT", "test.txt", true)]
    [InlineData("TEST.txt", "Test.TXT", true)]
    [InlineData("*.TXT", "file.txt", true)]
    public void Matches_CaseInsensitive_IgnoresCase(string pattern, string path, bool expected)
    {
        // Arrange
        var ignorePattern = new IgnorePattern(pattern, IgnoreResult.Ignored, isRooted: false, isCaseInsensitive: true);

        // Act
        var result = ignorePattern.Matches(path);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Test.TXT", "test.txt", false)]
    [InlineData("TEST.txt", "Test.TXT", false)]
    public void Matches_CaseSensitive_RespectsCase(string pattern, string path, bool expected)
    {
        // Arrange
        var ignorePattern = new IgnorePattern(pattern, IgnoreResult.Ignored, isRooted: false, isCaseInsensitive: false);

        // Act
        var result = ignorePattern.Matches(path);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region ParseLine Tests

    [Fact]
    public void ParseLine_EmptyLine_ReturnsEmptyResult()
    {
        // Act
        var result = IgnorePatternFactory.ParseLine("");

        // Assert
        Assert.True(result.IsEmpty);
        Assert.Empty(result.Patterns);
    }

    [Theory]
    [InlineData("// This is a comment")]
    [InlineData("//comment")]
    [InlineData("# This is also a comment")]
    [InlineData("#comment")]
    public void ParseLine_Comment_ReturnsEmptyResult(string line)
    {
        // Act
        var result = IgnorePatternFactory.ParseLine(line);

        // Assert
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void ParseLine_IncludeDirective_ReturnsIncludeFile()
    {
        // Act
        var result = IgnorePatternFactory.ParseLine("#include other.stignore");

        // Assert
        Assert.Equal("other.stignore", result.IncludeFile);
        Assert.Empty(result.Patterns);
    }

    [Fact]
    public void ParseLine_EscapeDirective_ReturnsNewEscapeChar()
    {
        // Act
        var result = IgnorePatternFactory.ParseLine("#escape=~");

        // Assert
        Assert.Equal('~', result.NewEscapeChar);
        Assert.Empty(result.Patterns);
    }

    [Fact]
    public void ParseLine_SimplePattern_CreatesIgnorePattern()
    {
        // Act
        var result = IgnorePatternFactory.ParseLine("*.txt");

        // Assert
        Assert.False(result.IsEmpty);
        Assert.NotEmpty(result.Patterns);
        Assert.True(result.Patterns[0].Result.IsIgnored());
    }

    [Fact]
    public void ParseLine_NegationPattern_CreatesNonIgnoredPattern()
    {
        // Act
        var result = IgnorePatternFactory.ParseLine("!important.txt");

        // Assert
        Assert.False(result.IsEmpty);
        Assert.NotEmpty(result.Patterns);
        Assert.False(result.Patterns[0].Result.IsIgnored());
    }

    [Fact]
    public void ParseLine_CaseInsensitivePrefix_SetsFoldCase()
    {
        // Act
        var result = IgnorePatternFactory.ParseLine("(?i)*.TXT");

        // Assert
        Assert.NotEmpty(result.Patterns);
        Assert.True(result.Patterns[0].IsCaseInsensitive);
    }

    [Fact]
    public void ParseLine_DeletablePrefix_SetsDeletable()
    {
        // Act
        var result = IgnorePatternFactory.ParseLine("(?d)temp/");

        // Assert
        Assert.NotEmpty(result.Patterns);
        Assert.True(result.Patterns[0].Result.IsDeletable());
    }

    [Fact]
    public void ParseLine_CombinedPrefixes_SetsAllFlags()
    {
        // Act
        var result = IgnorePatternFactory.ParseLine("!(?i)(?d)important.txt");

        // Assert
        Assert.NotEmpty(result.Patterns);
        var pattern = result.Patterns[0];
        Assert.False(pattern.Result.IsIgnored()); // Negated
        Assert.True(pattern.IsCaseInsensitive); // Case insensitive
        Assert.True(pattern.Result.IsDeletable()); // Deletable
    }

    [Fact]
    public void ParseLine_RootedPattern_CreatesRootedPattern()
    {
        // Act
        var result = IgnorePatternFactory.ParseLine("/root-only.txt");

        // Assert
        Assert.NotEmpty(result.Patterns);
        Assert.True(result.Patterns[0].IsRooted);
    }

    [Fact]
    public void ParseLine_NonRootedPattern_CreatesLocalAndRecursivePatterns()
    {
        // Act
        var result = IgnorePatternFactory.ParseLine("*.log");

        // Assert - non-rooted creates both local and recursive patterns
        Assert.Equal(2, result.Patterns.Length);
        Assert.Contains(result.Patterns, p => p.Pattern == "*.log");
        Assert.Contains(result.Patterns, p => p.Pattern == "**/*.log");
    }

    #endregion

    #region Escape Character Tests

    [Theory]
    [InlineData(@"\*literal.txt", "*literal.txt")]
    [InlineData(@"\?question.txt", "?question.txt")]
    [InlineData(@"\[bracket.txt", "[bracket.txt")]
    [InlineData(@"\\backslash.txt", @"\backslash.txt")]
    [InlineData(@"\!notspecial.txt", "!notspecial.txt")]
    public void UnescapePattern_EscapedChars_ReturnsLiteral(string escaped, string expected)
    {
        // Act
        var result = IgnorePatternFactory.UnescapePattern(escaped, '\\');

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("normal.txt", "normal.txt")]
    [InlineData("*.txt", "*.txt")]
    [InlineData("dir/file.txt", "dir/file.txt")]
    public void UnescapePattern_NoEscapes_ReturnsOriginal(string pattern, string expected)
    {
        // Act
        var result = IgnorePatternFactory.UnescapePattern(pattern, '\\');

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("~*literal.txt", "*literal.txt", '~')]
    [InlineData("~~tilde.txt", "~tilde.txt", '~')]
    public void UnescapePattern_CustomEscapeChar_Works(string escaped, string expected, char escapeChar)
    {
        // Act
        var result = IgnorePatternFactory.UnescapePattern(escaped, escapeChar);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsEscaped_SingleEscapeChar_ReturnsTrue()
    {
        // Assert
        Assert.True(IgnorePatternFactory.IsEscaped(@"\*", 1, '\\'));
    }

    [Fact]
    public void IsEscaped_DoubleEscapeChar_ReturnsFalse()
    {
        // Assert - \\ means literal backslash, so * is not escaped
        Assert.False(IgnorePatternFactory.IsEscaped(@"\\*", 2, '\\'));
    }

    [Fact]
    public void IsEscaped_NoEscapeChar_ReturnsFalse()
    {
        // Assert
        Assert.False(IgnorePatternFactory.IsEscaped("*.txt", 0, '\\'));
    }

    #endregion

    #region IgnoreResult Tests

    [Fact]
    public void IgnoreResult_NotIgnored_HasCorrectValue()
    {
        // Assert
        Assert.Equal(0, (byte)IgnoreResult.NotIgnored);
        Assert.False(IgnoreResult.NotIgnored.IsIgnored());
    }

    [Fact]
    public void IgnoreResult_Ignored_HasCorrectValue()
    {
        // Assert
        Assert.True(IgnoreResult.Ignored.IsIgnored());
        Assert.False(IgnoreResult.Ignored.IsDeletable());
    }

    [Fact]
    public void IgnoreResult_IgnoredDeletable_HasBothFlags()
    {
        // Assert
        Assert.True(IgnoreResult.IgnoredDeletable.IsIgnored());
        Assert.True(IgnoreResult.IgnoredDeletable.IsDeletable());
    }

    [Fact]
    public void IgnoreResult_ToggleIgnored_TogglesCorrectly()
    {
        // Act & Assert
        Assert.True(IgnoreResult.NotIgnored.ToggleIgnored().IsIgnored());
        Assert.False(IgnoreResult.Ignored.ToggleIgnored().IsIgnored());
    }

    [Fact]
    public void IgnoreResult_WithFlags_AddsFlags()
    {
        // Act
        var result = IgnoreResult.NotIgnored
            .ToggleIgnored()
            .WithDeletable()
            .WithCanSkipDir()
            .WithFoldCase();

        // Assert
        Assert.True(result.IsIgnored());
        Assert.True(result.IsDeletable());
        Assert.True(result.CanSkipDir());
        Assert.True(result.ShouldFoldCase());
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Matches_EmptyPath_ReturnsFalse()
    {
        // Arrange
        var pattern = new IgnorePattern("*.txt", IgnoreResult.Ignored, false, false);

        // Act
        var result = pattern.Matches("");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Matches_NullPath_ReturnsFalse()
    {
        // Arrange
        var pattern = new IgnorePattern("*.txt", IgnoreResult.Ignored, false, false);

        // Act
        var result = pattern.Matches(null!);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Matches_WindowsPathSeparator_NormalizesCorrectly()
    {
        // Arrange
        var pattern = new IgnorePattern("dir/*.txt", IgnoreResult.Ignored, false, false);

        // Act
        var result = pattern.Matches(@"dir\file.txt");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Constructor_NullPattern_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new IgnorePattern(null!, IgnoreResult.Ignored, false, false));
    }

    [Fact]
    public void ToString_ReturnsReadableString()
    {
        // Arrange
        var pattern = new IgnorePattern("*.txt", IgnoreResult.Ignored, isRooted: true, isCaseInsensitive: true);

        // Act
        var result = pattern.ToString();

        // Assert
        Assert.Contains("*.txt", result);
        Assert.Contains("Rooted: True", result);
        Assert.Contains("CaseInsensitive: True", result);
    }

    #endregion
}
