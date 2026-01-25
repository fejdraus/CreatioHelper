using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Configuration;

/// <summary>
/// Tests for FeatureFlagsService.
/// </summary>
public class FeatureFlagsServiceTests : IDisposable
{
    private readonly Mock<ILogger<FeatureFlagsService>> _loggerMock;
    private readonly string _tempStoragePath;
    private FeatureFlagsService _service;

    public FeatureFlagsServiceTests()
    {
        _loggerMock = new Mock<ILogger<FeatureFlagsService>>();
        _tempStoragePath = Path.Combine(Path.GetTempPath(), $"feature-flags-test-{Guid.NewGuid()}.json");
        _service = new FeatureFlagsService(_loggerMock.Object, _tempStoragePath);
    }

    public void Dispose()
    {
        _service?.Dispose();
        if (File.Exists(_tempStoragePath))
        {
            File.Delete(_tempStoragePath);
        }
    }

    #region IsEnabled Tests

    [Fact]
    public void IsEnabled_UnknownFeature_ReturnsFalse()
    {
        var result = _service.IsEnabled("unknown-feature");

        Assert.False(result);
    }

    [Fact]
    public async Task IsEnabled_EnabledFeature_ReturnsTrue()
    {
        await _service.SetEnabledAsync("test-feature", true);

        var result = _service.IsEnabled("test-feature");

        Assert.True(result);
    }

    [Fact]
    public async Task IsEnabled_DisabledFeature_ReturnsFalse()
    {
        await _service.SetEnabledAsync("test-feature", false);

        var result = _service.IsEnabled("test-feature");

        Assert.False(result);
    }

    [Fact]
    public void IsEnabled_RegisteredFeatureWithDefault_ReturnsDefault()
    {
        _service.RegisterFeature("new-feature", new FeatureFlagDefinition
        {
            DefaultEnabled = true
        });

        var result = _service.IsEnabled("new-feature");

        Assert.True(result);
    }

    [Fact]
    public async Task IsEnabledAsync_ReturnsCorrectValue()
    {
        await _service.SetEnabledAsync("test-feature", true);

        var result = await _service.IsEnabledAsync("test-feature");

        Assert.True(result);
    }

    #endregion

    #region Device Override Tests

    [Fact]
    public async Task IsEnabled_WithDeviceOverride_ReturnsOverrideValue()
    {
        await _service.SetEnabledAsync("test-feature", true);
        await _service.AddDeviceOverrideAsync("test-feature", "device1", false);

        var globalResult = _service.IsEnabled("test-feature");
        var deviceResult = _service.IsEnabled("test-feature", "device1");

        Assert.True(globalResult);
        Assert.False(deviceResult);
    }

    [Fact]
    public async Task RemoveDeviceOverride_RemovesOverride()
    {
        await _service.SetEnabledAsync("test-feature", true);
        await _service.AddDeviceOverrideAsync("test-feature", "device1", false);
        await _service.RemoveDeviceOverrideAsync("test-feature", "device1");

        var result = _service.IsEnabled("test-feature", "device1");

        Assert.True(result);
    }

    #endregion

    #region Rollout Percentage Tests

    [Fact]
    public async Task SetRolloutPercentage_SetsPercentage()
    {
        await _service.SetEnabledAsync("test-feature", true);
        await _service.SetRolloutPercentageAsync("test-feature", 50);

        var flag = await _service.GetFlagAsync("test-feature");

        Assert.NotNull(flag);
        Assert.Equal(50, flag.RolloutPercentage);
    }

    [Fact]
    public async Task SetRolloutPercentage_ClampsToBounds()
    {
        await _service.SetEnabledAsync("test-feature", true);
        await _service.SetRolloutPercentageAsync("test-feature", 150);

        var flag = await _service.GetFlagAsync("test-feature");

        Assert.NotNull(flag);
        Assert.Equal(100, flag.RolloutPercentage);
    }

    [Fact]
    public async Task SetRolloutPercentage_ClampsNegativeTo0()
    {
        await _service.SetEnabledAsync("test-feature", true);
        await _service.SetRolloutPercentageAsync("test-feature", -10);

        var flag = await _service.GetFlagAsync("test-feature");

        Assert.NotNull(flag);
        Assert.Equal(0, flag.RolloutPercentage);
    }

    [Fact]
    public async Task IsEnabled_RolloutPercentage0_AlwaysFalse()
    {
        await _service.SetEnabledAsync("test-feature", true);
        await _service.SetRolloutPercentageAsync("test-feature", 0);

        // Test multiple contexts - all should be false
        var results = new List<bool>();
        for (int i = 0; i < 10; i++)
        {
            results.Add(_service.IsEnabled("test-feature", $"context{i}"));
        }

        Assert.All(results, r => Assert.False(r));
    }

    [Fact]
    public async Task IsEnabled_RolloutPercentage100_AlwaysTrue()
    {
        await _service.SetEnabledAsync("test-feature", true);
        await _service.SetRolloutPercentageAsync("test-feature", 100);

        // Test multiple contexts - all should be true
        var results = new List<bool>();
        for (int i = 0; i < 10; i++)
        {
            results.Add(_service.IsEnabled("test-feature", $"context{i}"));
        }

        Assert.All(results, r => Assert.True(r));
    }

    [Fact]
    public async Task IsEnabled_RolloutPercentage_ConsistentForSameContext()
    {
        await _service.SetEnabledAsync("test-feature", true);
        await _service.SetRolloutPercentageAsync("test-feature", 50);

        var context = "consistent-context";
        var firstResult = _service.IsEnabled("test-feature", context);

        // Should return same result for same context
        for (int i = 0; i < 10; i++)
        {
            var result = _service.IsEnabled("test-feature", context);
            Assert.Equal(firstResult, result);
        }
    }

    #endregion

    #region GetValue Tests

    [Fact]
    public void GetValue_NotSet_ReturnsDefault()
    {
        var result = _service.GetValue("test-feature", 42);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task GetValue_SetValue_ReturnsValue()
    {
        await _service.SetValueAsync("test-feature", 100);

        var result = _service.GetValue("test-feature", 42);

        Assert.Equal(100, result);
    }

    [Fact]
    public async Task GetValue_StringValue_ReturnsValue()
    {
        await _service.SetValueAsync("test-feature", "hello");

        var result = _service.GetValue("test-feature", "default");

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task GetValue_TypeMismatch_ReturnsDefault()
    {
        await _service.SetValueAsync("test-feature", "not-a-number");

        var result = _service.GetValue("test-feature", 42);

        Assert.Equal(42, result);
    }

    #endregion

    #region Flag Management Tests

    [Fact]
    public async Task GetAllFlagsAsync_ReturnsAllFlags()
    {
        await _service.SetEnabledAsync("feature1", true);
        await _service.SetEnabledAsync("feature2", false);
        await _service.SetEnabledAsync("feature3", true);

        var flags = await _service.GetAllFlagsAsync();

        Assert.Equal(3, flags.Count());
    }

    [Fact]
    public async Task GetFlagAsync_ExistingFlag_ReturnsFlag()
    {
        await _service.SetEnabledAsync("test-feature", true);

        var flag = await _service.GetFlagAsync("test-feature");

        Assert.NotNull(flag);
        Assert.Equal("test-feature", flag.Name);
        Assert.True(flag.Enabled);
    }

    [Fact]
    public async Task GetFlagAsync_NonExistingFlag_ReturnsNull()
    {
        var flag = await _service.GetFlagAsync("non-existent");

        Assert.Null(flag);
    }

    [Fact]
    public async Task DeleteFlagAsync_RemovesFlag()
    {
        await _service.SetEnabledAsync("test-feature", true);
        await _service.DeleteFlagAsync("test-feature");

        var flag = await _service.GetFlagAsync("test-feature");

        Assert.Null(flag);
    }

    #endregion

    #region Registration Tests

    [Fact]
    public void RegisterFeature_AddsDefinition()
    {
        _service.RegisterFeature("custom-feature", new FeatureFlagDefinition
        {
            Description = "Test feature",
            DefaultEnabled = true,
            Category = "test"
        });

        var features = _service.GetRegisteredFeatures();
        var feature = features.FirstOrDefault(f => f.Name == "custom-feature");

        Assert.NotNull(feature);
        Assert.Equal("Test feature", feature.Description);
        Assert.True(feature.DefaultEnabled);
        Assert.Equal("test", feature.Category);
    }

    [Fact]
    public void GetRegisteredFeatures_IncludesBuiltInFeatures()
    {
        var features = _service.GetRegisteredFeatures();

        Assert.Contains(features, f => f.Name == "local-discovery");
        Assert.Contains(features, f => f.Name == "global-discovery");
        Assert.Contains(features, f => f.Name == "nat-traversal");
        Assert.Contains(features, f => f.Name == "delta-sync");
    }

    #endregion

    #region Event Tests

    [Fact]
    public async Task SetEnabledAsync_RaisesFlagChangedEvent()
    {
        FeatureFlagChangedEventArgs? receivedArgs = null;
        _service.FlagChanged += (sender, args) => receivedArgs = args;

        await _service.SetEnabledAsync("test-feature", true);

        Assert.NotNull(receivedArgs);
        Assert.Equal("test-feature", receivedArgs.FeatureName);
        Assert.True(receivedArgs.NewEnabled);
        Assert.Equal(FeatureFlagChangeType.EnabledChanged, receivedArgs.ChangeType);
    }

    [Fact]
    public async Task SetValueAsync_RaisesFlagChangedEvent()
    {
        FeatureFlagChangedEventArgs? receivedArgs = null;
        _service.FlagChanged += (sender, args) => receivedArgs = args;

        await _service.SetValueAsync("test-feature", 42);

        Assert.NotNull(receivedArgs);
        Assert.Equal("test-feature", receivedArgs.FeatureName);
        Assert.Equal(42, receivedArgs.NewValue);
        Assert.Equal(FeatureFlagChangeType.ValueChanged, receivedArgs.ChangeType);
    }

    [Fact]
    public async Task SetRolloutPercentageAsync_RaisesFlagChangedEvent()
    {
        await _service.SetEnabledAsync("test-feature", true);

        FeatureFlagChangedEventArgs? receivedArgs = null;
        _service.FlagChanged += (sender, args) => receivedArgs = args;

        await _service.SetRolloutPercentageAsync("test-feature", 75);

        Assert.NotNull(receivedArgs);
        Assert.Equal("test-feature", receivedArgs.FeatureName);
        Assert.Equal(75, receivedArgs.NewValue);
        Assert.Equal(FeatureFlagChangeType.RolloutChanged, receivedArgs.ChangeType);
    }

    [Fact]
    public async Task DeleteFlagAsync_RaisesFlagChangedEvent()
    {
        await _service.SetEnabledAsync("test-feature", true);

        FeatureFlagChangedEventArgs? receivedArgs = null;
        _service.FlagChanged += (sender, args) => receivedArgs = args;

        await _service.DeleteFlagAsync("test-feature");

        Assert.NotNull(receivedArgs);
        Assert.Equal("test-feature", receivedArgs.FeatureName);
        Assert.Equal(FeatureFlagChangeType.Deleted, receivedArgs.ChangeType);
    }

    #endregion

    #region Statistics Tests

    [Fact]
    public async Task GetStatistics_ReturnsCorrectCounts()
    {
        await _service.SetEnabledAsync("enabled1", true);
        await _service.SetEnabledAsync("enabled2", true);
        await _service.SetEnabledAsync("disabled1", false);
        await _service.SetRolloutPercentageAsync("enabled1", 50);

        // Perform some evaluations
        _service.IsEnabled("enabled1");
        _service.IsEnabled("enabled2");
        _service.IsEnabled("disabled1");

        var stats = _service.GetStatistics();

        Assert.Equal(3, stats.TotalFlags);
        Assert.Equal(2, stats.EnabledFlags);
        Assert.Equal(1, stats.DisabledFlags);
        Assert.Equal(1, stats.PartialRolloutFlags);
        Assert.Equal(3, stats.TotalEvaluations);
    }

    [Fact]
    public async Task GetStatistics_TracksCacheHitRate()
    {
        await _service.SetEnabledAsync("test-feature", true);

        // First call creates cache entry
        _service.IsEnabled("test-feature");
        // Subsequent calls are cache hits
        _service.IsEnabled("test-feature");
        _service.IsEnabled("test-feature");

        var stats = _service.GetStatistics();

        Assert.True(stats.CacheHitRate > 0);
    }

    #endregion

    #region Persistence Tests

    [Fact]
    public async Task Flags_PersistedToFile()
    {
        await _service.SetEnabledAsync("persistent-feature", true);

        Assert.True(File.Exists(_tempStoragePath));
        var content = await File.ReadAllTextAsync(_tempStoragePath);
        Assert.Contains("persistent-feature", content);
    }

    [Fact]
    public async Task Flags_LoadedFromFile()
    {
        // Create and save a flag
        await _service.SetEnabledAsync("loaded-feature", true);
        _service.Dispose();

        // Create new service instance - should load from file
        _service = new FeatureFlagsService(_loggerMock.Object, _tempStoragePath);

        // Wait for async loading to complete
        await Task.Delay(200);

        var flag = await _service.GetFlagAsync("loaded-feature");

        Assert.NotNull(flag);
        Assert.True(flag.Enabled);
    }

    #endregion

    #region Time-Based Tests

    [Fact]
    public async Task IsEnabled_BeforeStartDate_ReturnsFalse()
    {
        await _service.SetEnabledAsync("scheduled-feature", true);
        var flag = await _service.GetFlagAsync("scheduled-feature");
        flag!.StartDate = DateTime.UtcNow.AddDays(1);

        var result = _service.IsEnabled("scheduled-feature");

        Assert.False(result);
    }

    [Fact]
    public async Task IsEnabled_AfterEndDate_ReturnsFalse()
    {
        await _service.SetEnabledAsync("expired-feature", true);
        var flag = await _service.GetFlagAsync("expired-feature");
        flag!.EndDate = DateTime.UtcNow.AddDays(-1);

        var result = _service.IsEnabled("expired-feature");

        Assert.False(result);
    }

    #endregion
}
