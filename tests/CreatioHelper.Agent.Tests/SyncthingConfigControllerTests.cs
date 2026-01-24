using CreatioHelper.Agent.Controllers;
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
}
