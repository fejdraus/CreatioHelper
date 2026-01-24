using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Configuration;

public class ConfigurationManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConfigurationManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ConfigManagerTest_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.xml");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task ConfigFileChanged_TriggersReload()
    {
        // Arrange
        var mockConfigXmlService = new Mock<IConfigXmlService>();
        var mockLogger = new Mock<ILogger<ConfigurationManager>>();

        var initialConfig = new ConfigXml { Version = 1, Folders = new(), Devices = new() };
        var updatedConfig = new ConfigXml { Version = 2, Folders = new(), Devices = new() };

        mockConfigXmlService.Setup(s => s.ConfigExists()).Returns(true);
        mockConfigXmlService.Setup(s => s.GetConfigDirectory()).Returns(_tempDir);
        mockConfigXmlService.SetupSequence(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(initialConfig)
            .ReturnsAsync(updatedConfig);

        var manager = new ConfigurationManager(mockConfigXmlService.Object, mockLogger.Object);
        await manager.InitializeAsync();

        var eventRaised = new TaskCompletionSource<bool>();
        manager.ConfigurationChanged += (s, e) =>
        {
            if (e.ChangeType == ConfigurationChangeType.FullReload)
                eventRaised.TrySetResult(true);
        };

        // Act - simulate file change by writing to config.xml
        File.WriteAllText(_configPath, "<configuration></configuration>");

        // Assert - wait for reload event (with timeout)
        var completedTask = await Task.WhenAny(eventRaised.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(completedTask == eventRaised.Task, "ConfigurationChanged event with FullReload was not raised within timeout");
        var eventResult = await eventRaised.Task;
        Assert.True(eventResult);
    }
}
