using CreatioHelper.Domain.Entities;
using System.Xml.Serialization;
using Xunit;

namespace CreatioHelper.UnitTests;

/// <summary>
/// Tests for ConfigXml serialization/deserialization (Syncthing config.xml format v51)
/// </summary>
public class ConfigXmlTests
{
    private const string SampleConfigXml = @"<configuration version=""51"">
	<folder id=""test-folder"" label=""Test Folder"" path=""C:\Test"" type=""sendreceive"" rescanIntervalS=""3600"" fsWatcherEnabled=""true"" fsWatcherDelayS=""10"" fsWatcherTimeoutS=""0"" ignorePerms=""false"" autoNormalize=""true"">
		<filesystemType>basic</filesystemType>
		<device id=""DEVICE-ID"" introducedBy="""">
			<encryptionPassword></encryptionPassword>
		</device>
		<minDiskFree unit=""%"">1</minDiskFree>
		<versioning>
			<cleanupIntervalS>3600</cleanupIntervalS>
			<fsPath></fsPath>
			<fsType>basic</fsType>
		</versioning>
		<copiers>0</copiers>
		<pullerMaxPendingKiB>0</pullerMaxPendingKiB>
		<hashers>0</hashers>
		<order>random</order>
		<ignoreDelete>false</ignoreDelete>
		<scanProgressIntervalS>0</scanProgressIntervalS>
		<pullerPauseS>0</pullerPauseS>
		<pullerDelayS>1</pullerDelayS>
		<maxConflicts>10</maxConflicts>
		<disableSparseFiles>false</disableSparseFiles>
		<paused>false</paused>
		<markerName>.stfolder</markerName>
		<copyOwnershipFromParent>false</copyOwnershipFromParent>
		<modTimeWindowS>0</modTimeWindowS>
		<maxConcurrentWrites>16</maxConcurrentWrites>
		<disableFsync>false</disableFsync>
		<blockPullOrder>standard</blockPullOrder>
		<copyRangeMethod>standard</copyRangeMethod>
		<caseSensitiveFS>false</caseSensitiveFS>
		<junctionsAsDirs>false</junctionsAsDirs>
		<syncOwnership>false</syncOwnership>
		<sendOwnership>false</sendOwnership>
		<syncXattrs>false</syncXattrs>
		<sendXattrs>false</sendXattrs>
		<xattrFilter>
			<maxSingleEntrySize>1024</maxSingleEntrySize>
			<maxTotalSize>4096</maxTotalSize>
		</xattrFilter>
	</folder>
	<device id=""DEVICE-ID"" name=""TestDevice"" compression=""metadata"" introducer=""false"" skipIntroductionRemovals=""false"" introducedBy="""">
		<address>dynamic</address>
		<paused>false</paused>
		<autoAcceptFolders>false</autoAcceptFolders>
		<maxSendKbps>0</maxSendKbps>
		<maxRecvKbps>0</maxRecvKbps>
		<maxRequestKiB>0</maxRequestKiB>
		<untrusted>false</untrusted>
		<remoteGUIPort>0</remoteGUIPort>
		<numConnections>0</numConnections>
	</device>
	<gui enabled=""true"" tls=""false"" sendBasicAuthPrompt=""false"">
		<address>127.0.0.1:8384</address>
		<user>admin</user>
		<password>hashed</password>
		<metricsWithoutAuth>false</metricsWithoutAuth>
		<apikey>test-api-key</apikey>
		<theme>default</theme>
	</gui>
	<ldap></ldap>
	<options>
		<listenAddress>default</listenAddress>
		<globalAnnounceServer>default</globalAnnounceServer>
		<globalAnnounceEnabled>true</globalAnnounceEnabled>
		<localAnnounceEnabled>true</localAnnounceEnabled>
		<localAnnouncePort>21027</localAnnouncePort>
		<localAnnounceMCAddr>[ff12::8384]:21027</localAnnounceMCAddr>
		<maxSendKbps>0</maxSendKbps>
		<maxRecvKbps>0</maxRecvKbps>
		<reconnectionIntervalS>60</reconnectionIntervalS>
		<relaysEnabled>true</relaysEnabled>
		<relayReconnectIntervalM>10</relayReconnectIntervalM>
		<startBrowser>true</startBrowser>
		<natEnabled>true</natEnabled>
		<natLeaseMinutes>60</natLeaseMinutes>
		<natRenewalMinutes>30</natRenewalMinutes>
		<natTimeoutSeconds>10</natTimeoutSeconds>
		<urAccepted>-1</urAccepted>
		<urSeen>3</urSeen>
		<urUniqueID></urUniqueID>
		<urURL>https://data.syncthing.net/newdata</urURL>
		<urPostInsecurely>false</urPostInsecurely>
		<urInitialDelayS>1800</urInitialDelayS>
		<autoUpgradeIntervalH>12</autoUpgradeIntervalH>
		<upgradeToPreReleases>false</upgradeToPreReleases>
		<keepTemporariesH>24</keepTemporariesH>
		<cacheIgnoredFiles>false</cacheIgnoredFiles>
		<progressUpdateIntervalS>5</progressUpdateIntervalS>
		<limitBandwidthInLan>false</limitBandwidthInLan>
		<minHomeDiskFree unit=""%"">1</minHomeDiskFree>
		<releasesURL>https://upgrades.syncthing.net/meta.json</releasesURL>
		<crashReportingURL>https://crash.syncthing.net/newcrash</crashReportingURL>
		<crashReportingEnabled>true</crashReportingEnabled>
		<stunKeepaliveStartS>180</stunKeepaliveStartS>
		<stunKeepaliveMinS>20</stunKeepaliveMinS>
		<stunServer>default</stunServer>
		<connectionPriorityTcpLan>10</connectionPriorityTcpLan>
		<connectionPriorityQuicLan>20</connectionPriorityQuicLan>
		<connectionPriorityTcpWan>30</connectionPriorityTcpWan>
		<connectionPriorityQuicWan>40</connectionPriorityQuicWan>
		<connectionPriorityRelay>50</connectionPriorityRelay>
		<connectionPriorityUpgradeThreshold>0</connectionPriorityUpgradeThreshold>
	</options>
	<defaults>
		<folder id="""" label="""" path="""" type=""sendreceive"" rescanIntervalS=""3600"" fsWatcherEnabled=""true"" fsWatcherDelayS=""10"" fsWatcherTimeoutS=""0"" ignorePerms=""false"" autoNormalize=""true"">
			<filesystemType>basic</filesystemType>
			<minDiskFree unit=""%"">1</minDiskFree>
			<versioning>
				<cleanupIntervalS>3600</cleanupIntervalS>
				<fsPath></fsPath>
				<fsType>basic</fsType>
			</versioning>
			<copiers>0</copiers>
			<hashers>0</hashers>
			<order>random</order>
			<paused>false</paused>
			<markerName>.stfolder</markerName>
			<xattrFilter>
				<maxSingleEntrySize>1024</maxSingleEntrySize>
				<maxTotalSize>4096</maxTotalSize>
			</xattrFilter>
		</folder>
		<device id="""" compression=""metadata"" introducer=""false"" skipIntroductionRemovals=""false"" introducedBy="""">
			<address>dynamic</address>
			<paused>false</paused>
			<autoAcceptFolders>false</autoAcceptFolders>
		</device>
		<ignores></ignores>
	</defaults>
</configuration>";

    [Fact]
    public void Deserialize_SampleConfig_ParsesCorrectly()
    {
        // Arrange
        var serializer = new XmlSerializer(typeof(ConfigXml));
        using var reader = new StringReader(SampleConfigXml);

        // Act
        var config = (ConfigXml?)serializer.Deserialize(reader);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(51, config.Version);
        Assert.Single(config.Folders);
        Assert.Single(config.Devices);
    }

    [Fact]
    public void Deserialize_Folder_ParsesAttributesAndElements()
    {
        // Arrange
        var serializer = new XmlSerializer(typeof(ConfigXml));
        using var reader = new StringReader(SampleConfigXml);

        // Act
        var config = (ConfigXml?)serializer.Deserialize(reader);
        var folder = config!.Folders[0];

        // Assert - Attributes
        Assert.Equal("test-folder", folder.Id);
        Assert.Equal("Test Folder", folder.Label);
        Assert.Equal("C:\\Test", folder.Path);
        Assert.Equal("sendreceive", folder.Type);
        Assert.Equal(3600, folder.RescanIntervalS);
        Assert.True(folder.FsWatcherEnabled);
        Assert.Equal(10, folder.FsWatcherDelayS);
        Assert.False(folder.IgnorePerms);
        Assert.True(folder.AutoNormalize);

        // Assert - Elements
        Assert.Equal("basic", folder.FilesystemType);
        Assert.Equal("random", folder.Order);
        Assert.Equal(1, folder.PullerDelayS);
        Assert.Equal(10, folder.MaxConflicts);
        Assert.Equal(16, folder.MaxConcurrentWrites);
        Assert.Equal(".stfolder", folder.MarkerName);
        Assert.Equal("standard", folder.BlockPullOrder);
        Assert.Equal("standard", folder.CopyRangeMethod);
    }

    [Fact]
    public void Deserialize_FolderDevice_ParsesEncryptionPasswordAsElement()
    {
        // Arrange
        var serializer = new XmlSerializer(typeof(ConfigXml));
        using var reader = new StringReader(SampleConfigXml);

        // Act
        var config = (ConfigXml?)serializer.Deserialize(reader);
        var folderDevice = config!.Folders[0].Devices[0];

        // Assert
        Assert.Equal("DEVICE-ID", folderDevice.Id);
        Assert.Equal("", folderDevice.IntroducedBy);
        Assert.Equal("", folderDevice.EncryptionPassword);
    }

    [Fact]
    public void Deserialize_XattrFilter_ParsesCorrectly()
    {
        // Arrange
        var serializer = new XmlSerializer(typeof(ConfigXml));
        using var reader = new StringReader(SampleConfigXml);

        // Act
        var config = (ConfigXml?)serializer.Deserialize(reader);
        var xattrFilter = config!.Folders[0].XattrFilter;

        // Assert
        Assert.Equal(1024, xattrFilter.MaxSingleEntrySize);
        Assert.Equal(4096, xattrFilter.MaxTotalSize);
    }

    [Fact]
    public void Deserialize_Versioning_ParsesElementsCorrectly()
    {
        // Arrange
        var serializer = new XmlSerializer(typeof(ConfigXml));
        using var reader = new StringReader(SampleConfigXml);

        // Act
        var config = (ConfigXml?)serializer.Deserialize(reader);
        var versioning = config!.Folders[0].Versioning;

        // Assert
        Assert.Equal(3600, versioning.CleanupIntervalS);
        Assert.Equal("", versioning.FsPath);
        Assert.Equal("basic", versioning.FsType);
    }

    [Fact]
    public void Deserialize_Device_ParsesAllFields()
    {
        // Arrange
        var serializer = new XmlSerializer(typeof(ConfigXml));
        using var reader = new StringReader(SampleConfigXml);

        // Act
        var config = (ConfigXml?)serializer.Deserialize(reader);
        var device = config!.Devices[0];

        // Assert - Attributes
        Assert.Equal("DEVICE-ID", device.Id);
        Assert.Equal("TestDevice", device.Name);
        Assert.Equal("metadata", device.Compression);
        Assert.False(device.Introducer);

        // Assert - Elements
        Assert.Contains("dynamic", device.Addresses);
        Assert.False(device.Paused);
        Assert.Equal(0, device.NumConnections);
    }

    [Fact]
    public void Deserialize_Gui_ParsesAllFields()
    {
        // Arrange
        var serializer = new XmlSerializer(typeof(ConfigXml));
        using var reader = new StringReader(SampleConfigXml);

        // Act
        var config = (ConfigXml?)serializer.Deserialize(reader);
        var gui = config!.Gui;

        // Assert
        Assert.True(gui.Enabled);
        Assert.False(gui.Tls);
        Assert.False(gui.SendBasicAuthPrompt);
        Assert.Equal("127.0.0.1:8384", gui.Address);
        Assert.Equal("admin", gui.User);
        Assert.Equal("hashed", gui.Password);
        Assert.False(gui.MetricsWithoutAuth);
        Assert.Equal("test-api-key", gui.ApiKey);
        Assert.Equal("default", gui.Theme);
    }

    [Fact]
    public void Deserialize_Options_ParsesAllFields()
    {
        // Arrange
        var serializer = new XmlSerializer(typeof(ConfigXml));
        using var reader = new StringReader(SampleConfigXml);

        // Act
        var config = (ConfigXml?)serializer.Deserialize(reader);
        var options = config!.Options;

        // Assert
        Assert.Contains("default", options.ListenAddresses);
        Assert.True(options.GlobalAnnounceEnabled);
        Assert.True(options.NatEnabled);
        Assert.Equal(60, options.NatLeaseMinutes);
        Assert.Equal(-1, options.UrAccepted);
        Assert.True(options.CrashReportingEnabled);
        Assert.Equal(180, options.StunKeepaliveStartS);
        Assert.Equal(10, options.ConnectionPriorityTcpLan);
        Assert.Equal(50, options.ConnectionPriorityRelay);
    }

    [Fact]
    public void Deserialize_Defaults_ParsesCorrectly()
    {
        // Arrange
        var serializer = new XmlSerializer(typeof(ConfigXml));
        using var reader = new StringReader(SampleConfigXml);

        // Act
        var config = (ConfigXml?)serializer.Deserialize(reader);

        // Assert
        Assert.NotNull(config!.Defaults);
        Assert.NotNull(config.Defaults.Folder);
        Assert.NotNull(config.Defaults.Device);
        Assert.NotNull(config.Defaults.Ignores);
        Assert.Equal("sendreceive", config.Defaults.Folder.Type);
        Assert.Equal("metadata", config.Defaults.Device.Compression);
    }

    [Fact]
    public void Serialize_DefaultConfig_ProducesValidXml()
    {
        // Arrange
        var config = new ConfigXml
        {
            Version = 51,
            Folders = new List<ConfigXmlFolder>
            {
                new ConfigXmlFolder
                {
                    Id = "test",
                    Label = "Test",
                    Path = "C:\\Test",
                    Devices = new List<ConfigXmlFolderDevice>
                    {
                        new ConfigXmlFolderDevice { Id = "DEV-123" }
                    }
                }
            },
            Devices = new List<ConfigXmlDevice>
            {
                new ConfigXmlDevice
                {
                    Id = "DEV-123",
                    Name = "MyDevice"
                }
            }
        };

        var serializer = new XmlSerializer(typeof(ConfigXml));
        using var writer = new StringWriter();

        // Act
        serializer.Serialize(writer, config);
        var xml = writer.ToString();

        // Assert
        Assert.Contains("version=\"51\"", xml);
        Assert.Contains("id=\"test\"", xml);
        Assert.Contains("<filesystemType>basic</filesystemType>", xml);
        Assert.Contains("<maxConcurrentWrites>16</maxConcurrentWrites>", xml);
        Assert.Contains("<xattrFilter>", xml);
    }

    [Fact]
    public void RoundTrip_SerializeDeserialize_PreservesData()
    {
        // Arrange
        var original = new ConfigXml
        {
            Version = 51,
            Folders = new List<ConfigXmlFolder>
            {
                new ConfigXmlFolder
                {
                    Id = "my-folder",
                    Label = "My Folder",
                    Path = "/data/sync",
                    Type = "sendonly",
                    MaxConcurrentWrites = 8,
                    XattrFilter = new ConfigXmlXattrFilter
                    {
                        MaxSingleEntrySize = 2048,
                        MaxTotalSize = 8192
                    },
                    Devices = new List<ConfigXmlFolderDevice>
                    {
                        new ConfigXmlFolderDevice
                        {
                            Id = "DEVICE-ABC",
                            EncryptionPassword = "secret"
                        }
                    }
                }
            },
            Devices = new List<ConfigXmlDevice>
            {
                new ConfigXmlDevice
                {
                    Id = "DEVICE-ABC",
                    Name = "Server",
                    NumConnections = 5
                }
            },
            Gui = new ConfigXmlGui
            {
                ApiKey = "my-api-key",
                MetricsWithoutAuth = true
            },
            Options = new ConfigXmlOptions
            {
                StunKeepaliveStartS = 200,
                ConnectionPriorityUpgradeThreshold = 10
            }
        };

        var serializer = new XmlSerializer(typeof(ConfigXml));
        using var writer = new StringWriter();
        serializer.Serialize(writer, original);
        var xml = writer.ToString();

        using var reader = new StringReader(xml);

        // Act
        var deserialized = (ConfigXml?)serializer.Deserialize(reader);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(51, deserialized.Version);
        Assert.Single(deserialized.Folders);
        Assert.Equal("my-folder", deserialized.Folders[0].Id);
        Assert.Equal("sendonly", deserialized.Folders[0].Type);
        Assert.Equal(8, deserialized.Folders[0].MaxConcurrentWrites);
        Assert.Equal(2048, deserialized.Folders[0].XattrFilter.MaxSingleEntrySize);
        Assert.Equal("secret", deserialized.Folders[0].Devices[0].EncryptionPassword);
        Assert.Equal(5, deserialized.Devices[0].NumConnections);
        Assert.True(deserialized.Gui.MetricsWithoutAuth);
        Assert.Equal(200, deserialized.Options.StunKeepaliveStartS);
        Assert.Equal(10, deserialized.Options.ConnectionPriorityUpgradeThreshold);
    }
}
