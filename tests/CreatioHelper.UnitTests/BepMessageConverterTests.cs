using CreatioHelper.Infrastructure.Services.Sync;
using CreatioHelper.Infrastructure.Services.Sync.Proto;
using Google.Protobuf;
using Xunit;

namespace CreatioHelper.UnitTests;

/// <summary>
/// Unit tests for BepMessageConverter - validates conversion between BepMessages and Protobuf types.
/// </summary>
public class BepMessageConverterTests
{
    #region Hello Conversion Tests

    [Fact]
    public void ConvertHello_ToProtobuf_RoundTrip()
    {
        // Arrange
        var bepHello = new BepHello
        {
            DeviceId = "DEVICE-ID-HERE",
            DeviceName = "TestDevice",
            ClientName = "TestClient",
            ClientVersion = "1.2.3",
            NumConnections = 5,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepHello);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Equal(bepHello.DeviceName, backToOriginal.DeviceName);
        Assert.Equal(bepHello.ClientName, backToOriginal.ClientName);
        Assert.Equal(bepHello.ClientVersion, backToOriginal.ClientVersion);
        Assert.Equal(bepHello.NumConnections, backToOriginal.NumConnections);
        Assert.Equal(bepHello.Timestamp, backToOriginal.Timestamp);
        // Note: DeviceId is not in proto Hello, comes from TLS cert
        Assert.Empty(backToOriginal.DeviceId);
    }

    [Fact]
    public void ConvertHello_NullStrings_ConvertToEmpty()
    {
        // Arrange
        var bepHello = new BepHello
        {
            DeviceName = null!,
            ClientName = null!,
            ClientVersion = null!
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepHello);

        // Assert - null strings should become empty strings
        Assert.Equal(string.Empty, proto.DeviceName);
        Assert.Equal(string.Empty, proto.ClientName);
        Assert.Equal(string.Empty, proto.ClientVersion);
    }

    #endregion

    #region ClusterConfig Conversion Tests

    [Fact]
    public void ConvertClusterConfig_ToProtobuf_RoundTrip()
    {
        // Arrange
        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test-folder",
                    Label = "Test Folder",
                    ReadOnly = true,
                    IgnorePermissions = false,
                    IgnoreDelete = true,
                    DisableTempIndexes = false,
                    Paused = false,
                    Devices =
                    [
                        new BepDevice
                        {
                            DeviceId = "MRIW7OK-NETT3M4-N6SBWME-N7XGODL-VMBUXKV-TQPBQCE-XKS4K3W-LO2SLQE",
                            Name = "Device1",
                            Addresses = ["tcp://192.168.1.1:22000", "tcp://192.168.1.2:22000"],
                            Compression = BepCompression.Always,
                            CertName = "cert-name",
                            MaxSequence = 100,
                            Introducer = false,
                            IndexId = 12345,
                            SkipIntroductionRemovals = true
                        }
                    ]
                }
            ]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepConfig);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Single(backToOriginal.Folders);
        var folder = backToOriginal.Folders[0];
        Assert.Equal("test-folder", folder.Id);
        Assert.Equal("Test Folder", folder.Label);
        Assert.True(folder.ReadOnly);
        Assert.False(folder.IgnorePermissions);
        Assert.True(folder.IgnoreDelete);
        Assert.False(folder.Paused);

        Assert.Single(folder.Devices);
        var device = folder.Devices[0];
        Assert.Equal("Device1", device.Name);
        Assert.Equal(2, device.Addresses.Count);
        Assert.Equal(BepCompression.Always, device.Compression);
        Assert.Equal(100, device.MaxSequence);
        Assert.True(device.SkipIntroductionRemovals);
    }

    [Fact]
    public void ConvertClusterConfig_EmptyFolders_RoundTrip()
    {
        // Arrange
        var bepConfig = new BepClusterConfig { Folders = [] };

        // Act
        var proto = BepMessageConverter.ToProto(bepConfig);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Empty(backToOriginal.Folders);
    }

    #endregion

    #region Index/IndexUpdate Conversion Tests

    [Fact]
    public void ConvertIndex_ToProtobuf_RoundTrip()
    {
        // Arrange
        var bepIndex = new BepIndex
        {
            Folder = "sync-folder",
            LastSequence = 42,
            Files =
            [
                CreateTestBepFileInfo("test-file.txt", 1024, BepFileInfoType.File),
                CreateTestBepFileInfo("test-dir", 0, BepFileInfoType.Directory)
            ]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepIndex);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Equal("sync-folder", backToOriginal.Folder);
        Assert.Equal(42, backToOriginal.LastSequence);
        Assert.Equal(2, backToOriginal.Files.Count);
        Assert.Equal("test-file.txt", backToOriginal.Files[0].Name);
        Assert.Equal(BepFileInfoType.File, backToOriginal.Files[0].Type);
        Assert.Equal("test-dir", backToOriginal.Files[1].Name);
        Assert.Equal(BepFileInfoType.Directory, backToOriginal.Files[1].Type);
    }

    [Fact]
    public void ConvertIndexUpdate_ToProtobuf_RoundTrip()
    {
        // Arrange
        var bepIndexUpdate = new BepIndexUpdate
        {
            Folder = "update-folder",
            LastSequence = 100,
            Files = [CreateTestBepFileInfo("updated.txt", 2048, BepFileInfoType.File)]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepIndexUpdate);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Equal("update-folder", backToOriginal.Folder);
        Assert.Equal(100, backToOriginal.LastSequence);
        Assert.Single(backToOriginal.Files);
        Assert.Equal("updated.txt", backToOriginal.Files[0].Name);
    }

    #endregion

    #region FileInfo Conversion Tests

    [Fact]
    public void ConvertFileInfo_ToProtobuf_AllFields_RoundTrip()
    {
        // Arrange
        var bepFileInfo = new BepFileInfo
        {
            Name = "test-file.txt",
            Type = BepFileInfoType.File,
            Size = 1024,
            Permissions = 0755,
            ModifiedS = 1700000000,
            ModifiedNs = 123456789,
            ModifiedBy = 0xABCDEF,
            Deleted = false,
            Invalid = false,
            NoPermissions = true,
            Sequence = 42,
            BlockSize = 131072,
            BlocksHash = new byte[] { 1, 2, 3, 4 },
            VersionHash = new byte[] { 5, 6, 7, 8 },
            Version = new BepVector
            {
                Counters =
                [
                    new BepCounter { Id = 1, Value = 10 },
                    new BepCounter { Id = 2, Value = 20 }
                ]
            },
            Blocks =
            [
                new BepBlockInfo { Offset = 0, Size = 512, Hash = new byte[] { 10, 20, 30 }, WeakHash = 12345 },
                new BepBlockInfo { Offset = 512, Size = 512, Hash = new byte[] { 40, 50, 60 }, WeakHash = 67890 }
            ]
        };

        var bepIndex = new BepIndex
        {
            Folder = "test",
            Files = [bepFileInfo]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepIndex);
        var backToOriginal = BepMessageConverter.FromProto(proto);
        var result = backToOriginal.Files[0];

        // Assert
        Assert.Equal("test-file.txt", result.Name);
        Assert.Equal(BepFileInfoType.File, result.Type);
        Assert.Equal(1024, result.Size);
        Assert.Equal(0755u, result.Permissions);
        Assert.Equal(1700000000, result.ModifiedS);
        Assert.Equal(123456789, result.ModifiedNs);
        Assert.Equal(0xABCDEFu, result.ModifiedBy);
        Assert.False(result.Deleted);
        Assert.False(result.Invalid);
        Assert.True(result.NoPermissions);
        Assert.Equal(42, result.Sequence);
        Assert.Equal(131072, result.BlockSize);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.BlocksHash);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, result.VersionHash);
    }

    [Theory]
    [InlineData(BepFileInfoType.File)]
    [InlineData(BepFileInfoType.Directory)]
    [InlineData(BepFileInfoType.Symlink)]
    public void ConvertFileInfo_AllFileTypes_RoundTrip(BepFileInfoType fileType)
    {
        // Arrange
        var bepIndex = new BepIndex
        {
            Folder = "test",
            Files = [CreateTestBepFileInfo("test", 0, fileType)]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepIndex);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Equal(fileType, backToOriginal.Files[0].Type);
    }

    [Fact]
    public void ConvertFileInfo_Symlink_PreservesTarget()
    {
        // Arrange
        var bepFileInfo = new BepFileInfo
        {
            Name = "link",
            Type = BepFileInfoType.Symlink,
            Symlink = "/path/to/target"
        };

        var bepIndex = new BepIndex { Folder = "test", Files = [bepFileInfo] };

        // Act
        var proto = BepMessageConverter.ToProto(bepIndex);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Equal("/path/to/target", backToOriginal.Files[0].Symlink);
    }

    #endregion

    #region BlockInfo Conversion Tests

    [Fact]
    public void ConvertBlockInfo_ToProtobuf_RoundTrip()
    {
        // Arrange
        var bepFileInfo = new BepFileInfo
        {
            Name = "test.txt",
            Type = BepFileInfoType.File,
            Size = 1024,
            Blocks =
            [
                new BepBlockInfo
                {
                    Offset = 0,
                    Size = 512,
                    Hash = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
                    WeakHash = 0xCAFEBABE
                },
                new BepBlockInfo
                {
                    Offset = 512,
                    Size = 512,
                    Hash = new byte[] { 0xFE, 0xED, 0xFA, 0xCE },
                    WeakHash = 0xDEADC0DE
                }
            ]
        };

        var bepIndex = new BepIndex { Folder = "test", Files = [bepFileInfo] };

        // Act
        var proto = BepMessageConverter.ToProto(bepIndex);
        var backToOriginal = BepMessageConverter.FromProto(proto);
        var blocks = backToOriginal.Files[0].Blocks;

        // Assert
        Assert.Equal(2, blocks.Count);

        Assert.Equal(0, blocks[0].Offset);
        Assert.Equal(512, blocks[0].Size);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, blocks[0].Hash);
        Assert.Equal(0xCAFEBABEu, blocks[0].WeakHash);

        Assert.Equal(512, blocks[1].Offset);
        Assert.Equal(512, blocks[1].Size);
        Assert.Equal(new byte[] { 0xFE, 0xED, 0xFA, 0xCE }, blocks[1].Hash);
        Assert.Equal(0xDEADC0DEu, blocks[1].WeakHash);
    }

    #endregion

    #region VectorClock Conversion Tests

    [Fact]
    public void VectorClock_Conversion_Preserves()
    {
        // Arrange
        var bepVector = new BepVector
        {
            Counters =
            [
                new BepCounter { Id = 1234567890, Value = 100 },
                new BepCounter { Id = 9876543210, Value = 200 },
                new BepCounter { Id = 5555555555, Value = 300 }
            ]
        };

        var bepFileInfo = new BepFileInfo
        {
            Name = "test.txt",
            Type = BepFileInfoType.File,
            Version = bepVector
        };

        var bepIndex = new BepIndex { Folder = "test", Files = [bepFileInfo] };

        // Act
        var proto = BepMessageConverter.ToProto(bepIndex);
        var backToOriginal = BepMessageConverter.FromProto(proto);
        var result = backToOriginal.Files[0].Version;

        // Assert
        Assert.Equal(3, result.Counters.Count);
        Assert.Equal(1234567890u, result.Counters[0].Id);
        Assert.Equal(100u, result.Counters[0].Value);
        Assert.Equal(9876543210u, result.Counters[1].Id);
        Assert.Equal(200u, result.Counters[1].Value);
        Assert.Equal(5555555555u, result.Counters[2].Id);
        Assert.Equal(300u, result.Counters[2].Value);
    }

    [Fact]
    public void VectorClock_EmptyCounters_RoundTrip()
    {
        // Arrange
        var bepFileInfo = new BepFileInfo
        {
            Name = "test.txt",
            Version = new BepVector { Counters = [] }
        };

        var bepIndex = new BepIndex { Folder = "test", Files = [bepFileInfo] };

        // Act
        var proto = BepMessageConverter.ToProto(bepIndex);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Empty(backToOriginal.Files[0].Version.Counters);
    }

    #endregion

    #region PlatformData Conversion Tests

    [Fact]
    public void PlatformData_Unix_Conversion()
    {
        // Arrange
        var bepFileInfo = new BepFileInfo
        {
            Name = "test.txt",
            Platform = new BepPlatform
            {
                Unix = new BepUnixData
                {
                    OwnerName = "testuser",
                    GroupName = "testgroup",
                    Uid = 1000,
                    Gid = 1000
                }
            }
        };

        var bepIndex = new BepIndex { Folder = "test", Files = [bepFileInfo] };

        // Act
        var proto = BepMessageConverter.ToProto(bepIndex);
        var backToOriginal = BepMessageConverter.FromProto(proto);
        var unix = backToOriginal.Files[0].Platform.Unix;

        // Assert
        Assert.Equal("testuser", unix.OwnerName);
        Assert.Equal("testgroup", unix.GroupName);
        Assert.Equal(1000, unix.Uid);
        Assert.Equal(1000, unix.Gid);
    }

    [Fact]
    public void PlatformData_Windows_Conversion()
    {
        // Arrange
        var bepFileInfo = new BepFileInfo
        {
            Name = "test.txt",
            Platform = new BepPlatform
            {
                Windows = new BepWindowsData
                {
                    OwnerName = @"DOMAIN\User",
                    OwnerIsGroup = false
                }
            }
        };

        var bepIndex = new BepIndex { Folder = "test", Files = [bepFileInfo] };

        // Act
        var proto = BepMessageConverter.ToProto(bepIndex);
        var backToOriginal = BepMessageConverter.FromProto(proto);
        var windows = backToOriginal.Files[0].Platform.Windows;

        // Assert
        Assert.Equal(@"DOMAIN\User", windows.OwnerName);
        Assert.False(windows.OwnerIsGroup);
    }

    [Fact]
    public void PlatformData_Linux_Xattrs_Conversion()
    {
        // Arrange
        var bepFileInfo = new BepFileInfo
        {
            Name = "test.txt",
            Platform = new BepPlatform
            {
                Linux = new BepLinuxData
                {
                    Xattrs =
                    [
                        new BepXattr { Name = "user.custom", Value = new byte[] { 1, 2, 3 } },
                        new BepXattr { Name = "security.selinux", Value = new byte[] { 4, 5, 6 } }
                    ]
                }
            }
        };

        var bepIndex = new BepIndex { Folder = "test", Files = [bepFileInfo] };

        // Act
        var proto = BepMessageConverter.ToProto(bepIndex);
        var backToOriginal = BepMessageConverter.FromProto(proto);
        var xattrs = backToOriginal.Files[0].Platform.Linux.Xattrs;

        // Assert
        Assert.Equal(2, xattrs.Count);
        Assert.Equal("user.custom", xattrs[0].Name);
        Assert.Equal(new byte[] { 1, 2, 3 }, xattrs[0].Value);
        Assert.Equal("security.selinux", xattrs[1].Name);
        Assert.Equal(new byte[] { 4, 5, 6 }, xattrs[1].Value);
    }

    #endregion

    #region Request/Response Conversion Tests

    [Fact]
    public void ConvertRequest_ToProtobuf_RoundTrip()
    {
        // Arrange
        var bepRequest = new BepRequest
        {
            Id = 12345,
            Folder = "sync-folder",
            Name = "requested-file.txt",
            Offset = 131072,
            Size = 65536,
            Hash = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            FromTemporary = true,
            WeakHash = 0xCAFEBABE,
            BlockNo = 5
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepRequest);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Equal(12345, backToOriginal.Id);
        Assert.Equal("sync-folder", backToOriginal.Folder);
        Assert.Equal("requested-file.txt", backToOriginal.Name);
        Assert.Equal(131072, backToOriginal.Offset);
        Assert.Equal(65536, backToOriginal.Size);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, backToOriginal.Hash);
        Assert.True(backToOriginal.FromTemporary);
        Assert.Equal(0xCAFEBABEu, backToOriginal.WeakHash);
        Assert.Equal(5, backToOriginal.BlockNo);
    }

    [Fact]
    public void ConvertResponse_ToProtobuf_RoundTrip()
    {
        // Arrange
        var testData = new byte[1024];
        new Random(42).NextBytes(testData);

        var bepResponse = new BepResponse
        {
            Id = 12345,
            Data = testData,
            Code = BepErrorCode.NoError
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepResponse);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Equal(12345, backToOriginal.Id);
        Assert.Equal(testData, backToOriginal.Data);
        Assert.Equal(BepErrorCode.NoError, backToOriginal.Code);
    }

    [Theory]
    [InlineData(BepErrorCode.NoError)]
    [InlineData(BepErrorCode.Generic)]
    [InlineData(BepErrorCode.NoSuchFile)]
    [InlineData(BepErrorCode.InvalidFile)]
    public void ConvertResponse_AllErrorCodes_RoundTrip(BepErrorCode code)
    {
        // Arrange
        var bepResponse = new BepResponse { Id = 1, Data = [], Code = code };

        // Act
        var proto = BepMessageConverter.ToProto(bepResponse);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Equal(code, backToOriginal.Code);
    }

    #endregion

    #region DownloadProgress Conversion Tests

    [Fact]
    public void ConvertDownloadProgress_ToProtobuf_RoundTrip()
    {
        // Arrange
        var bepProgress = new BepDownloadProgress
        {
            Folder = "sync-folder",
            Updates =
            [
                new BepFileDownloadProgressUpdate
                {
                    UpdateType = BepUpdateType.Append,
                    Name = "downloading.txt",
                    Version = new BepVector
                    {
                        Counters = [new BepCounter { Id = 1, Value = 10 }]
                    },
                    BlockIndexes = [0, 1, 2, 3],
                    BlockSize = 131072
                }
            ]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepProgress);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Equal("sync-folder", backToOriginal.Folder);
        Assert.Single(backToOriginal.Updates);
        var update = backToOriginal.Updates[0];
        Assert.Equal(BepUpdateType.Append, update.UpdateType);
        Assert.Equal("downloading.txt", update.Name);
        Assert.Single(update.Version.Counters);
        Assert.Equal(4, update.BlockIndexes.Count);
        Assert.Equal(131072, update.BlockSize);
    }

    [Theory]
    [InlineData(BepUpdateType.Append)]
    [InlineData(BepUpdateType.Forget)]
    public void ConvertDownloadProgress_AllUpdateTypes_RoundTrip(BepUpdateType updateType)
    {
        // Arrange
        var bepProgress = new BepDownloadProgress
        {
            Folder = "test",
            Updates = [new BepFileDownloadProgressUpdate { UpdateType = updateType, Name = "test.txt" }]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepProgress);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Equal(updateType, backToOriginal.Updates[0].UpdateType);
    }

    #endregion

    #region Ping/Close Conversion Tests

    [Fact]
    public void ConvertPing_RoundTrip()
    {
        // Act
        var proto = BepMessageConverter.ToProtoPing();
        var backToOriginal = BepMessageConverter.FromProtoPing();

        // Assert
        Assert.NotNull(proto);
        Assert.NotNull(backToOriginal);
    }

    [Fact]
    public void ConvertClose_ToProtobuf_RoundTrip()
    {
        // Arrange
        var bepClose = new BepClose { Reason = "Connection terminated by user" };

        // Act
        var proto = BepMessageConverter.ToProto(bepClose);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Equal("Connection terminated by user", backToOriginal.Reason);
    }

    [Fact]
    public void ConvertClose_NullReason_ConvertedToEmpty()
    {
        // Arrange
        var bepClose = new BepClose { Reason = null! };

        // Act
        var proto = BepMessageConverter.ToProto(bepClose);

        // Assert
        Assert.Equal(string.Empty, proto.Reason);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ConvertFileInfo_NullCollections_HandledGracefully()
    {
        // Arrange
        var bepFileInfo = new BepFileInfo
        {
            Name = "test.txt",
            Blocks = null!,
            Version = null!
        };

        var bepIndex = new BepIndex { Folder = "test", Files = [bepFileInfo] };

        // Act - should not throw
        var proto = BepMessageConverter.ToProto(bepIndex);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.NotNull(backToOriginal.Files[0]);
        Assert.Empty(backToOriginal.Files[0].Blocks);
        Assert.Empty(backToOriginal.Files[0].Version.Counters);
    }

    [Fact]
    public void ConvertFileInfo_EmptyByteArrays_Preserved()
    {
        // Arrange
        var bepFileInfo = new BepFileInfo
        {
            Name = "test.txt",
            BlocksHash = [],
            VersionHash = []
        };

        var bepIndex = new BepIndex { Folder = "test", Files = [bepFileInfo] };

        // Act
        var proto = BepMessageConverter.ToProto(bepIndex);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Empty(backToOriginal.Files[0].BlocksHash);
        Assert.Empty(backToOriginal.Files[0].VersionHash);
    }

    [Fact]
    public void ConvertDevice_EmptyAddresses_Preserved()
    {
        // Arrange
        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test",
                    Devices = [new BepDevice { Name = "Device", Addresses = [] }]
                }
            ]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepConfig);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Empty(backToOriginal.Folders[0].Devices[0].Addresses);
    }

    #endregion

    #region Helper Methods

    private static BepFileInfo CreateTestBepFileInfo(string name, long size, BepFileInfoType type)
    {
        return new BepFileInfo
        {
            Name = name,
            Type = type,
            Size = size,
            Permissions = 0644,
            ModifiedS = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ModifiedNs = 0,
            Deleted = false,
            Invalid = false,
            Sequence = 1,
            BlockSize = 131072,
            Version = new BepVector { Counters = [new BepCounter { Id = 1, Value = 1 }] }
        };
    }

    #endregion
}
