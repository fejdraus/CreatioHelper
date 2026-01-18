using CreatioHelper.Infrastructure.Services.Sync.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Sync.Diagnostics;

public class DebugFacilitiesTests
{
    private readonly Mock<ILogger<DebugFacilities>> _loggerMock;
    private readonly DebugFacilities _debugFacilities;

    public DebugFacilitiesTests()
    {
        _loggerMock = new Mock<ILogger<DebugFacilities>>();
        _debugFacilities = new DebugFacilities(_loggerMock.Object);
    }

    [Fact]
    public void EnableCategory_AddsCategory()
    {
        // Act
        _debugFacilities.EnableCategory(DebugCategories.Model);

        // Assert
        Assert.True(_debugFacilities.IsCategoryEnabled(DebugCategories.Model));
    }

    [Fact]
    public void DisableCategory_RemovesCategory()
    {
        // Arrange
        _debugFacilities.EnableCategory(DebugCategories.Model);

        // Act
        _debugFacilities.DisableCategory(DebugCategories.Model);

        // Assert
        Assert.False(_debugFacilities.IsCategoryEnabled(DebugCategories.Model));
    }

    [Fact]
    public void IsCategoryEnabled_ReturnsFalse_WhenNotEnabled()
    {
        // Assert
        Assert.False(_debugFacilities.IsCategoryEnabled(DebugCategories.Scanner));
    }

    [Fact]
    public void IsCategoryEnabled_CaseInsensitive()
    {
        // Arrange
        _debugFacilities.EnableCategory("MODEL");

        // Assert
        Assert.True(_debugFacilities.IsCategoryEnabled("model"));
        Assert.True(_debugFacilities.IsCategoryEnabled("Model"));
    }

    [Fact]
    public void GetEnabledCategories_ReturnsAllEnabled()
    {
        // Arrange
        _debugFacilities.EnableCategory(DebugCategories.Model);
        _debugFacilities.EnableCategory(DebugCategories.Protocol);

        // Act
        var categories = _debugFacilities.GetEnabledCategories();

        // Assert
        Assert.Contains(DebugCategories.Model, categories);
        Assert.Contains(DebugCategories.Protocol, categories);
        Assert.Equal(2, categories.Count);
    }

    [Fact]
    public void ParseDebugString_EnablesMultipleCategories()
    {
        // Act
        _debugFacilities.ParseDebugString("model,protocol,scanner");

        // Assert
        Assert.True(_debugFacilities.IsCategoryEnabled(DebugCategories.Model));
        Assert.True(_debugFacilities.IsCategoryEnabled(DebugCategories.Protocol));
        Assert.True(_debugFacilities.IsCategoryEnabled(DebugCategories.Scanner));
    }

    [Fact]
    public void ParseDebugString_AllKeyword_EnablesAllCategories()
    {
        // Act
        _debugFacilities.ParseDebugString("all");

        // Assert
        foreach (var category in DebugCategories.All)
        {
            Assert.True(_debugFacilities.IsCategoryEnabled(category));
        }
    }

    [Fact]
    public void Debug_WhenCategoryEnabled_AddsEntry()
    {
        // Arrange
        _debugFacilities.EnableCategory(DebugCategories.Model);

        // Act
        _debugFacilities.Debug(DebugCategories.Model, "Test message {0}", "arg1");

        // Assert
        var entries = _debugFacilities.GetRecentEntries();
        Assert.Single(entries);
        Assert.Equal(DebugCategories.Model, entries[0].Category);
        Assert.Contains("arg1", entries[0].Message);
    }

    [Fact]
    public void Debug_WhenCategoryDisabled_DoesNotAddEntry()
    {
        // Act
        _debugFacilities.Debug(DebugCategories.Model, "Test message");

        // Assert
        var entries = _debugFacilities.GetRecentEntries();
        Assert.Empty(entries);
    }

    [Fact]
    public void GetRecentEntries_WithCategoryFilter_ReturnsOnlyMatchingEntries()
    {
        // Arrange
        _debugFacilities.EnableCategory(DebugCategories.Model);
        _debugFacilities.EnableCategory(DebugCategories.Protocol);
        _debugFacilities.Debug(DebugCategories.Model, "Model message");
        _debugFacilities.Debug(DebugCategories.Protocol, "Protocol message");

        // Act
        var modelEntries = _debugFacilities.GetRecentEntries(category: DebugCategories.Model);

        // Assert
        Assert.Single(modelEntries);
        Assert.Equal(DebugCategories.Model, modelEntries[0].Category);
    }

    [Fact]
    public void GetRecentEntries_WithCount_LimitsResults()
    {
        // Arrange
        _debugFacilities.EnableCategory(DebugCategories.Model);
        for (int i = 0; i < 10; i++)
        {
            _debugFacilities.Debug(DebugCategories.Model, $"Message {i}");
        }

        // Act
        var entries = _debugFacilities.GetRecentEntries(count: 5);

        // Assert
        Assert.Equal(5, entries.Count);
    }

    [Fact]
    public void ClearEntries_RemovesAllEntries()
    {
        // Arrange
        _debugFacilities.EnableCategory(DebugCategories.Model);
        _debugFacilities.Debug(DebugCategories.Model, "Test message");

        // Act
        _debugFacilities.ClearEntries();

        // Assert
        var entries = _debugFacilities.GetRecentEntries();
        Assert.Empty(entries);
    }

    [Fact]
    public void DebugCategories_All_ContainsExpectedCategories()
    {
        // Assert
        Assert.Contains(DebugCategories.Model, DebugCategories.All);
        Assert.Contains(DebugCategories.Scanner, DebugCategories.All);
        Assert.Contains(DebugCategories.Protocol, DebugCategories.All);
        Assert.Contains(DebugCategories.Database, DebugCategories.All);
        Assert.Contains(DebugCategories.Events, DebugCategories.All);
        Assert.Contains(DebugCategories.Connections, DebugCategories.All);
        Assert.Contains(DebugCategories.Encryption, DebugCategories.All);
    }
}
