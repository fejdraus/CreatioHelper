using CreatioHelper.Agent.Controllers;
using CreatioHelper.Application.DTOs;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;

namespace CreatioHelper.Agent.Tests;

public class SyncthingConfigControllerTests
{
    private readonly Mock<ISyncEngine> _syncEngineMock;
    private readonly Mock<ILogger<SyncthingConfigController>> _loggerMock;
    private readonly Mock<IConfigXmlService> _configXmlServiceMock;
    private readonly IConfiguration _configuration;
    private readonly SyncthingConfigController _controller;

    public SyncthingConfigControllerTests()
    {
        _syncEngineMock = new Mock<ISyncEngine>();
        _loggerMock = new Mock<ILogger<SyncthingConfigController>>();
        _configXmlServiceMock = new Mock<IConfigXmlService>();
        _configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        _syncEngineMock.Setup(s => s.DeviceId).Returns("TEST-DEVICE-ID");
        _syncEngineMock.Setup(s => s.GetDevicesAsync())
            .ReturnsAsync(new List<SyncDevice>());
        _syncEngineMock.Setup(s => s.GetFoldersAsync())
            .ReturnsAsync(new List<SyncFolder>());

        _controller = new SyncthingConfigController(
            _syncEngineMock.Object,
            _loggerMock.Object,
            _configuration,
            _configXmlServiceMock.Object);
    }

    #region LDAP Tests

    [Fact]
    public void GetLdapConfig_ReturnsOk()
    {
        var result = _controller.GetLdapConfig();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetLdapConfig_ReturnsLdapConfigStructure()
    {
        var result = _controller.GetLdapConfig();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var value = ok.Value!;

        // Check that the object has expected LDAP properties
        var addressProperty = value.GetType().GetProperty("address");
        var bindDNProperty = value.GetType().GetProperty("bindDN");
        var transportProperty = value.GetType().GetProperty("transport");

        Assert.NotNull(addressProperty);
        Assert.NotNull(bindDNProperty);
        Assert.NotNull(transportProperty);
    }

    [Fact]
    public void GetLdapConfig_ReturnsDefaultTransport()
    {
        var result = _controller.GetLdapConfig();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var value = ok.Value!;

        var transportProperty = value.GetType().GetProperty("transport");
        var transport = transportProperty?.GetValue(value) as string;

        Assert.Equal("plain", transport);
    }

    [Fact]
    public void UpdateLdapConfig_ReturnsOk()
    {
        var ldapConfig = JsonSerializer.SerializeToElement(new
        {
            address = "ldap://localhost:389",
            bindDN = "cn=admin,dc=example,dc=com",
            transport = "starttls"
        });

        var result = _controller.UpdateLdapConfig(ldapConfig);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void UpdateLdapConfig_ReturnsLdapConfig_AfterUpdate()
    {
        var ldapConfig = JsonSerializer.SerializeToElement(new
        {
            address = "ldap://localhost:389"
        });

        var result = _controller.UpdateLdapConfig(ldapConfig);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var value = ok.Value!;

        // Verify it returns a valid LDAP config structure
        var addressProperty = value.GetType().GetProperty("address");
        Assert.NotNull(addressProperty);
    }

    #endregion

    #region Config In Sync Tests

    [Fact]
    public void GetConfigInSync_ReturnsOk()
    {
#pragma warning disable CS0618 // Type or member is obsolete
        var result = _controller.GetConfigInSync();
#pragma warning restore CS0618

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetConfigInSync_ReturnsConfigInSyncProperty()
    {
#pragma warning disable CS0618 // Type or member is obsolete
        var result = _controller.GetConfigInSync();
#pragma warning restore CS0618

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var value = ok.Value!;

        var configInSyncProperty = value.GetType().GetProperty("configInSync");
        Assert.NotNull(configInSyncProperty);
    }

    [Fact]
    public void GetConfigInSync_ReturnsTrue()
    {
#pragma warning disable CS0618 // Type or member is obsolete
        var result = _controller.GetConfigInSync();
#pragma warning restore CS0618

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var value = ok.Value!;

        var configInSyncProperty = value.GetType().GetProperty("configInSync");
        var configInSync = (bool?)configInSyncProperty?.GetValue(value);

        Assert.True(configInSync);
    }

    #endregion

    #region Existing Config Endpoints Tests

    [Fact]
    public async Task GetConfig_ReturnsOk()
    {
        var result = await _controller.GetConfig();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetRestartRequired_ReturnsOk()
    {
        var result = _controller.GetRestartRequired();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetRestartRequired_ReturnsFalse()
    {
        var result = _controller.GetRestartRequired();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var value = ok.Value!;

        var requiresRestartProperty = value.GetType().GetProperty("requiresRestart");
        var requiresRestart = (bool?)requiresRestartProperty?.GetValue(value);

        Assert.False(requiresRestart);
    }

    [Fact]
    public async Task GetFolders_ReturnsOk()
    {
        var result = await _controller.GetFolders();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task GetDevices_ReturnsOk()
    {
        var result = await _controller.GetDevices();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetOptions_ReturnsOk()
    {
        var result = _controller.GetOptions();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetGuiConfig_ReturnsOk()
    {
        var result = _controller.GetGuiConfig();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetDefaultFolderConfig_ReturnsOk()
    {
        var result = _controller.GetDefaultFolderConfig();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetDefaultDeviceConfig_ReturnsOk()
    {
        var result = _controller.GetDefaultDeviceConfig();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetDefaultIgnoresConfig_ReturnsOk()
    {
        var result = _controller.GetDefaultIgnoresConfig();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    #region Configuration Apply Tests

    [Fact]
    public async Task LoadConfigFromFile_CallsApplyConfigurationAsync()
    {
        // Arrange
        var configXml = new ConfigXml
        {
            Folders = new List<ConfigXmlFolder> { new ConfigXmlFolder { Id = "folder1", Label = "Folder 1", Path = "/test" } },
            Devices = new List<ConfigXmlDevice> { new ConfigXmlDevice { Id = "device1", Name = "Device 1" } }
        };

        _configXmlServiceMock.Setup(s => s.ConfigExists()).Returns(true);
        _configXmlServiceMock.Setup(s => s.ConfigPath).Returns("/config/config.xml");
        _configXmlServiceMock.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(configXml);
        _configXmlServiceMock.Setup(s => s.Validate(It.IsAny<ConfigXml>()))
            .Returns(new ConfigValidationResult { IsValid = true });

        _syncEngineMock.Setup(s => s.ApplyConfigurationAsync(It.IsAny<ConfigXml>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.LoadConfigFromFile();

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        _syncEngineMock.Verify(s => s.ApplyConfigurationAsync(
            It.Is<ConfigXml>(c => c.Folders.Count == 1 && c.Devices.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoadConfigFromFile_ReturnsNotFound_WhenConfigNotExists()
    {
        // Arrange
        _configXmlServiceMock.Setup(s => s.ConfigExists()).Returns(false);
        _configXmlServiceMock.Setup(s => s.ConfigPath).Returns("/config/config.xml");

        // Act
        var result = await _controller.LoadConfigFromFile();

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
        _syncEngineMock.Verify(s => s.ApplyConfigurationAsync(
            It.IsAny<ConfigXml>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoadConfigFromFile_ReturnsBadRequest_WhenValidationFails()
    {
        // Arrange
        var configXml = new ConfigXml();

        _configXmlServiceMock.Setup(s => s.ConfigExists()).Returns(true);
        _configXmlServiceMock.Setup(s => s.ConfigPath).Returns("/config/config.xml");
        _configXmlServiceMock.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(configXml);
        _configXmlServiceMock.Setup(s => s.Validate(It.IsAny<ConfigXml>()))
            .Returns(new ConfigValidationResult
            {
                IsValid = false,
                Errors = new List<string> { "Invalid configuration" }
            });

        // Act
        var result = await _controller.LoadConfigFromFile();

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _syncEngineMock.Verify(s => s.ApplyConfigurationAsync(
            It.IsAny<ConfigXml>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region PATCH Folder Tests

    [Fact]
    public async Task PatchFolder_ReturnsNotFound_WhenFolderDoesNotExist()
    {
        // Arrange
        _syncEngineMock.Setup(s => s.GetFolderAsync("nonexistent"))
            .ReturnsAsync((SyncFolder?)null);

        var patchData = JsonSerializer.SerializeToElement(new { label = "New Label" });

        // Act
        var result = await _controller.PatchFolder("nonexistent", patchData);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task PatchFolder_ReturnsOk_WhenFolderExists()
    {
        // Arrange
        var existingFolder = new SyncFolder("folder1", "Original Label", "/test/path", "sendreceive");
        var updatedFolder = new SyncFolder("folder1", "New Label", "/test/path", "sendreceive");

        _syncEngineMock.Setup(s => s.GetFolderAsync("folder1"))
            .ReturnsAsync(existingFolder);
        _syncEngineMock.Setup(s => s.UpdateFolderAsync(It.IsAny<FolderConfiguration>()))
            .ReturnsAsync(updatedFolder);
        _syncEngineMock.Setup(s => s.GetConfigurationAsync())
            .ReturnsAsync(new SyncConfiguration { DeviceId = "TEST-DEVICE-ID" });
        _configXmlServiceMock.Setup(s => s.FromSyncConfiguration(
            It.IsAny<SyncConfiguration>(), It.IsAny<List<SyncDevice>>(), It.IsAny<List<SyncFolder>>()))
            .Returns(new ConfigXml());
        _configXmlServiceMock.Setup(s => s.SaveAsync(It.IsAny<ConfigXml>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var patchData = JsonSerializer.SerializeToElement(new { label = "New Label" });

        // Act
        var result = await _controller.PatchFolder("folder1", patchData);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    #region PATCH Device Tests

    [Fact]
    public async Task PatchDevice_ReturnsNotFound_WhenDeviceDoesNotExist()
    {
        // Arrange
        _syncEngineMock.Setup(s => s.GetDevicesAsync())
            .ReturnsAsync(new List<SyncDevice>());

        var patchData = JsonSerializer.SerializeToElement(new { paused = true });

        // Act
        var result = await _controller.PatchDevice("nonexistent", patchData);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task PatchDevice_ReturnsOk_WhenDeviceExists()
    {
        // Arrange
        var device = new SyncDevice("device1", "Test Device");

        _syncEngineMock.Setup(s => s.GetDevicesAsync())
            .ReturnsAsync(new List<SyncDevice> { device });

        var patchData = JsonSerializer.SerializeToElement(new { name = "Updated Device" });

        // Act
        var result = await _controller.PatchDevice("device1", patchData);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task PatchDevice_CallsPauseDevice_WhenPausedIsTrue()
    {
        // Arrange
        var device = new SyncDevice("device1", "Test Device");

        _syncEngineMock.Setup(s => s.GetDevicesAsync())
            .ReturnsAsync(new List<SyncDevice> { device });
        _syncEngineMock.Setup(s => s.PauseDeviceAsync("device1"))
            .Returns(Task.CompletedTask);

        var patchData = JsonSerializer.SerializeToElement(new { paused = true });

        // Act
        var result = await _controller.PatchDevice("device1", patchData);

        // Assert
        _syncEngineMock.Verify(s => s.PauseDeviceAsync("device1"), Times.Once);
    }

    [Fact]
    public async Task PatchDevice_CallsResumeDevice_WhenPausedIsFalse()
    {
        // Arrange
        var device = new SyncDevice("device1", "Test Device");
        device.SetPaused(true); // Set paused to true initially

        _syncEngineMock.Setup(s => s.GetDevicesAsync())
            .ReturnsAsync(new List<SyncDevice> { device });
        _syncEngineMock.Setup(s => s.ResumeDeviceAsync("device1"))
            .Returns(Task.CompletedTask);

        var patchData = JsonSerializer.SerializeToElement(new { paused = false });

        // Act
        var result = await _controller.PatchDevice("device1", patchData);

        // Assert
        _syncEngineMock.Verify(s => s.ResumeDeviceAsync("device1"), Times.Once);
    }

    #endregion

    #region PUT Defaults Tests

    [Fact]
    public void UpdateDefaultFolderConfig_ReturnsOk()
    {
        var folderConfig = JsonSerializer.SerializeToElement(new
        {
            rescanIntervalS = 7200,
            fsWatcherEnabled = false
        });

        var result = _controller.UpdateDefaultFolderConfig(folderConfig);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void UpdateDefaultDeviceConfig_ReturnsOk()
    {
        var deviceConfig = JsonSerializer.SerializeToElement(new
        {
            compression = "always",
            autoAcceptFolders = true
        });

        var result = _controller.UpdateDefaultDeviceConfig(deviceConfig);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void UpdateDefaultIgnoresConfig_ReturnsOk()
    {
        var ignoresConfig = JsonSerializer.SerializeToElement(new
        {
            lines = new[] { "*.tmp", "*.bak", ".git" }
        });

        var result = _controller.UpdateDefaultIgnoresConfig(ignoresConfig);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void UpdateDefaultIgnoresConfig_StoresIgnoreLines()
    {
        // First update
        var ignoresConfig = JsonSerializer.SerializeToElement(new
        {
            lines = new[] { "*.tmp", "*.bak" }
        });

        _controller.UpdateDefaultIgnoresConfig(ignoresConfig);

        // Verify stored by getting defaults
        var result = _controller.GetDefaultIgnoresConfig();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var value = ok.Value!;

        var linesProperty = value.GetType().GetProperty("lines");
        var lines = linesProperty?.GetValue(value) as string[];

        Assert.NotNull(lines);
        Assert.Contains("*.tmp", lines);
        Assert.Contains("*.bak", lines);
    }

    #endregion
}
