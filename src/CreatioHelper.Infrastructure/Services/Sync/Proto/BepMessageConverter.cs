using Google.Protobuf;

namespace CreatioHelper.Infrastructure.Services.Sync.Proto;

/// <summary>
/// Converts between existing BepMessages classes and Protobuf types for wire compatibility.
/// This allows gradual migration from JSON to Protobuf serialization.
/// </summary>
public static class BepMessageConverter
{
    #region Hello

    public static Hello ToProto(BepHello hello)
    {
        var proto = new Hello
        {
            DeviceName = hello.DeviceName ?? string.Empty,
            ClientName = hello.ClientName ?? string.Empty,
            ClientVersion = hello.ClientVersion ?? string.Empty,
            NumConnections = hello.NumConnections,
            Timestamp = hello.Timestamp
        };

        // Add P2P Upgrade extensions if present
        if (hello.Extensions != null)
        {
            proto.Extensions = ToProto(hello.Extensions);
        }

        return proto;
    }

    public static BepHello FromProto(Hello hello) => new()
    {
        DeviceId = string.Empty, // Not in proto Hello, comes from TLS cert
        DeviceName = hello.DeviceName,
        ClientName = hello.ClientName,
        ClientVersion = hello.ClientVersion,
        NumConnections = hello.NumConnections,
        Timestamp = hello.Timestamp,
        Extensions = hello.Extensions != null ? FromProto(hello.Extensions) : null
    };

    #endregion

    #region ClusterConfig

    public static ClusterConfig ToProto(BepClusterConfig config)
    {
        var proto = new ClusterConfig();

        foreach (var folder in config.Folders ?? [])
        {
            // Map boolean folder flags to the new enum-based approach
            // ReadOnly maps to SEND_ONLY type
            var folderType = folder.ReadOnly ? FolderType.SendOnly : FolderType.SendReceive;
            var stopReason = folder.Paused ? FolderStopReason.Paused : FolderStopReason.Running;

            var protoFolder = new Folder
            {
                Id = folder.Id ?? string.Empty,
                Label = folder.Label ?? string.Empty,
                Type = folderType,
                StopReason = stopReason
            };

            foreach (var device in folder.Devices ?? [])
            {
                var protoDevice = new Device
                {
                    Id = ByteString.CopyFrom(ParseDeviceId(device.DeviceId)),
                    Name = device.Name ?? string.Empty,
                    Compression = (Compression)(int)device.Compression,
                    CertName = device.CertName ?? string.Empty,
                    MaxSequence = device.MaxSequence,
                    Introducer = device.Introducer,
                    IndexId = device.IndexId,
                    SkipIntroductionRemovals = device.SkipIntroductionRemovals,
                    EncryptionPasswordToken = device.EncryptionPasswordToken != null
                        ? ByteString.CopyFrom(device.EncryptionPasswordToken)
                        : ByteString.Empty
                };
                protoDevice.Addresses.AddRange(device.Addresses ?? []);
                protoFolder.Devices.Add(protoDevice);
            }

            proto.Folders.Add(protoFolder);
        }

        return proto;
    }

    public static BepClusterConfig FromProto(ClusterConfig config) => new()
    {
        Folders = config.Folders.Select(f => new BepFolder
        {
            Id = f.Id,
            Label = f.Label,
            // Map new enum fields back to legacy boolean fields
            ReadOnly = f.Type == FolderType.SendOnly || f.Type == FolderType.ReceiveEncrypted,
            IgnorePermissions = false, // No longer in protocol
            IgnoreDelete = false, // No longer in protocol
            DisableTempIndexes = false, // No longer in protocol
            Paused = f.StopReason == FolderStopReason.Paused,
            Devices = f.Devices.Select(d => new BepDevice
            {
                Id = d.Id.ToByteArray(),
                DeviceId = FormatDeviceId(d.Id.ToByteArray()),
                Name = d.Name,
                Addresses = d.Addresses.ToList(),
                Compression = (BepCompression)(int)d.Compression,
                CertName = d.CertName,
                MaxSequence = d.MaxSequence,
                Introducer = d.Introducer,
                IndexId = d.IndexId,
                SkipIntroductionRemovals = d.SkipIntroductionRemovals,
                EncryptionPasswordToken = d.EncryptionPasswordToken?.ToByteArray() ?? Array.Empty<byte>()
            }).ToList()
        }).ToList()
    };

    #endregion

    #region Index

    public static Index ToProto(BepIndex index)
    {
        var proto = new Index
        {
            Folder = index.Folder ?? string.Empty,
            LastSequence = index.LastSequence
        };

        foreach (var file in index.Files)
        {
            proto.Files.Add(ToProtoFileInfo(file));
        }

        return proto;
    }

    public static BepIndex FromProto(Index index) => new()
    {
        Folder = index.Folder,
        LastSequence = index.LastSequence,
        Files = index.Files.Select(FromProtoFileInfo).ToList()
    };

    public static IndexUpdate ToProto(BepIndexUpdate indexUpdate)
    {
        var proto = new IndexUpdate
        {
            Folder = indexUpdate.Folder ?? string.Empty,
            LastSequence = indexUpdate.LastSequence
        };

        foreach (var file in indexUpdate.Files)
        {
            proto.Files.Add(ToProtoFileInfo(file));
        }

        return proto;
    }

    public static BepIndexUpdate FromProto(IndexUpdate indexUpdate) => new()
    {
        Folder = indexUpdate.Folder,
        LastSequence = indexUpdate.LastSequence,
        Files = indexUpdate.Files.Select(FromProtoFileInfo).ToList()
    };

    private static FileInfo ToProtoFileInfo(BepFileInfo file)
    {
        var proto = new FileInfo
        {
            Name = file.Name ?? string.Empty,
            Type = ToProtoFileType(file.Type),
            Size = file.Size,
            Permissions = file.Permissions,
            ModifiedS = file.ModifiedS,
            ModifiedNs = file.ModifiedNs,
            ModifiedBy = file.ModifiedBy,
            Deleted = file.Deleted,
            Invalid = file.Invalid,
            NoPermissions = file.NoPermissions,
            Sequence = file.Sequence,
            BlockSize = file.BlockSize,
            SymlinkTarget = ByteString.CopyFromUtf8(file.Symlink ?? string.Empty),
            BlocksHash = file.BlocksHash != null ? ByteString.CopyFrom(file.BlocksHash) : ByteString.Empty,
            VersionHash = file.VersionHash != null ? ByteString.CopyFrom(file.VersionHash) : ByteString.Empty
        };

        // Version vector
        if (file.Version != null)
        {
            proto.Version = ToProtoVector(file.Version);
        }

        // Blocks
        foreach (var block in file.Blocks ?? [])
        {
            proto.Blocks.Add(new BlockInfo
            {
                Offset = block.Offset,
                Size = block.Size,
                Hash = block.Hash != null ? ByteString.CopyFrom(block.Hash) : ByteString.Empty
                // WeakHash removed in current Syncthing protocol
            });
        }

        // Platform data
        if (file.Platform != null)
        {
            proto.Platform = ToProtoPlatformData(file.Platform);
        }

        return proto;
    }

    private static BepFileInfo FromProtoFileInfo(FileInfo file) => new()
    {
        Name = file.Name,
        Type = FromProtoFileType(file.Type),
        Size = file.Size,
        Permissions = file.Permissions,
        ModifiedS = file.ModifiedS,
        ModifiedNs = file.ModifiedNs,
        ModifiedBy = file.ModifiedBy,
        Deleted = file.Deleted,
        Invalid = file.Invalid,
        NoPermissions = file.NoPermissions,
        Version = FromProtoVector(file.Version),
        Sequence = file.Sequence,
        BlockSize = file.BlockSize,
        Blocks = file.Blocks.Select(b => new BepBlockInfo
        {
            Offset = b.Offset,
            Size = b.Size,
            Hash = b.Hash?.ToByteArray() ?? Array.Empty<byte>(),
            WeakHash = 0 // No longer in protocol
        }).ToList(),
        Symlink = file.SymlinkTarget?.ToStringUtf8() ?? string.Empty,
        BlocksHash = file.BlocksHash?.ToByteArray() ?? Array.Empty<byte>(),
        Encrypted = file.Encrypted != null && file.Encrypted.Length > 0,
        Platform = FromProtoPlatformData(file.Platform),
        VersionHash = file.VersionHash?.ToByteArray() ?? Array.Empty<byte>()
    };

    private static FileInfoType ToProtoFileType(BepFileInfoType type) => type switch
    {
        BepFileInfoType.File => FileInfoType.File,
        BepFileInfoType.Directory => FileInfoType.Directory,
        BepFileInfoType.Symlink => FileInfoType.Symlink,
        _ => FileInfoType.File
    };

    private static BepFileInfoType FromProtoFileType(FileInfoType type) => type switch
    {
        FileInfoType.File => BepFileInfoType.File,
        FileInfoType.Directory => BepFileInfoType.Directory,
        FileInfoType.Symlink => BepFileInfoType.Symlink,
#pragma warning disable CS0612 // Type or member is obsolete
        FileInfoType.SymlinkFile => BepFileInfoType.Symlink,
        FileInfoType.SymlinkDirectory => BepFileInfoType.Symlink,
#pragma warning restore CS0612
        _ => BepFileInfoType.File
    };

    private static Vector ToProtoVector(BepVector vector)
    {
        var proto = new Vector();
        foreach (var counter in vector.Counters ?? [])
        {
            proto.Counters.Add(new Counter
            {
                Id = counter.Id,
                Value = counter.Value
            });
        }
        return proto;
    }

    private static BepVector FromProtoVector(Vector? vector)
    {
        if (vector == null) return new BepVector { Counters = [] };
        return new BepVector
        {
            Counters = vector.Counters.Select(c => new BepCounter
            {
                Id = c.Id,
                Value = c.Value
            }).ToList()
        };
    }

    private static PlatformData ToProtoPlatformData(BepPlatform platform)
    {
        var proto = new PlatformData();

        if (platform.Unix != null)
        {
            proto.Unix = new UnixData
            {
                OwnerName = platform.Unix.OwnerName ?? string.Empty,
                GroupName = platform.Unix.GroupName ?? string.Empty,
                Uid = platform.Unix.Uid,
                Gid = platform.Unix.Gid
            };
        }

        if (platform.Windows != null)
        {
            proto.Windows = new WindowsData
            {
                OwnerName = platform.Windows.OwnerName ?? string.Empty,
                OwnerIsGroup = platform.Windows.OwnerIsGroup
            };
        }

        if (platform.Linux != null && platform.Linux.Xattrs.Count > 0)
        {
            proto.Linux = ToProtoXattrData(platform.Linux.Xattrs);
        }

        if (platform.Darwin != null && platform.Darwin.Xattrs.Count > 0)
        {
            proto.Darwin = ToProtoXattrData(platform.Darwin.Xattrs);
        }

        if (platform.Freebsd != null && platform.Freebsd.Xattrs.Count > 0)
        {
            proto.Freebsd = ToProtoXattrData(platform.Freebsd.Xattrs);
        }

        if (platform.Netbsd != null && platform.Netbsd.Xattrs.Count > 0)
        {
            proto.Netbsd = ToProtoXattrData(platform.Netbsd.Xattrs);
        }

        return proto;
    }

    private static BepPlatform FromProtoPlatformData(PlatformData? platform)
    {
        var result = new BepPlatform();
        if (platform == null) return result;

        if (platform.Unix != null)
        {
            result.Unix = new BepUnixData
            {
                OwnerName = platform.Unix.OwnerName,
                GroupName = platform.Unix.GroupName,
                Uid = platform.Unix.Uid,
                Gid = platform.Unix.Gid
            };
        }

        if (platform.Windows != null)
        {
            result.Windows = new BepWindowsData
            {
                OwnerName = platform.Windows.OwnerName,
                OwnerIsGroup = platform.Windows.OwnerIsGroup
            };
        }

        if (platform.Linux != null)
        {
            result.Linux = new BepLinuxData
            {
                Xattrs = FromProtoXattrData(platform.Linux)
            };
        }

        if (platform.Darwin != null)
        {
            result.Darwin = new BepDarwinData
            {
                Xattrs = FromProtoXattrData(platform.Darwin)
            };
        }

        if (platform.Freebsd != null)
        {
            result.Freebsd = new BepFreebsdData
            {
                Xattrs = FromProtoXattrData(platform.Freebsd)
            };
        }

        if (platform.Netbsd != null)
        {
            result.Netbsd = new BepNetbsdData
            {
                Xattrs = FromProtoXattrData(platform.Netbsd)
            };
        }

        return result;
    }

    private static XattrData ToProtoXattrData(List<BepXattr> xattrs)
    {
        var proto = new XattrData();
        foreach (var x in xattrs)
        {
            proto.Xattrs.Add(new Xattr
            {
                Name = x.Name ?? string.Empty,
                Value = x.Value != null ? ByteString.CopyFrom(x.Value) : ByteString.Empty
            });
        }
        return proto;
    }

    private static List<BepXattr> FromProtoXattrData(XattrData? xattr)
    {
        if (xattr == null) return [];
        return xattr.Xattrs.Select(x => new BepXattr
        {
            Name = x.Name,
            Value = x.Value?.ToByteArray() ?? Array.Empty<byte>()
        }).ToList();
    }

    #endregion

    #region Request/Response

    public static Request ToProto(BepRequest request) => new()
    {
        Id = request.Id,
        Folder = request.Folder ?? string.Empty,
        Name = request.Name ?? string.Empty,
        Offset = request.Offset,
        Size = request.Size,
        Hash = request.Hash != null ? ByteString.CopyFrom(request.Hash) : ByteString.Empty,
        FromTemporary = request.FromTemporary,
        // WeakHash removed in current Syncthing protocol
        BlockNo = request.BlockNo
    };

    public static BepRequest FromProto(Request request) => new()
    {
        Id = request.Id,
        Folder = request.Folder,
        Name = request.Name,
        Offset = request.Offset,
        Size = request.Size,
        Hash = request.Hash?.ToByteArray() ?? Array.Empty<byte>(),
        FromTemporary = request.FromTemporary,
        WeakHash = 0, // No longer in protocol
        BlockNo = request.BlockNo
    };

    public static Response ToProto(BepResponse response) => new()
    {
        Id = response.Id,
        Data = response.Data != null ? ByteString.CopyFrom(response.Data) : ByteString.Empty,
        Code = (ErrorCode)(int)response.Code
    };

    public static BepResponse FromProto(Response response) => new()
    {
        Id = response.Id,
        Data = response.Data?.ToByteArray() ?? Array.Empty<byte>(),
        Code = (BepErrorCode)(int)response.Code
    };

    #endregion

    #region DownloadProgress

    public static DownloadProgress ToProto(BepDownloadProgress progress)
    {
        var proto = new DownloadProgress
        {
            Folder = progress.Folder ?? string.Empty
        };

        foreach (var update in progress.Updates ?? [])
        {
            var protoUpdate = new FileDownloadProgressUpdate
            {
                UpdateType = (FileDownloadProgressUpdateType)(int)update.UpdateType,
                Name = update.Name ?? string.Empty,
                BlockSize = update.BlockSize
            };

            if (update.Version != null)
            {
                protoUpdate.Version = ToProtoVector(update.Version);
            }

            protoUpdate.BlockIndexes.AddRange(update.BlockIndexes ?? []);
            proto.Updates.Add(protoUpdate);
        }

        return proto;
    }

    public static BepDownloadProgress FromProto(DownloadProgress progress) => new()
    {
        Folder = progress.Folder,
        Updates = progress.Updates.Select(u => new BepFileDownloadProgressUpdate
        {
            UpdateType = (BepUpdateType)(int)u.UpdateType,
            Name = u.Name,
            Version = FromProtoVector(u.Version),
            BlockIndexes = u.BlockIndexes.ToList(),
            BlockSize = u.BlockSize
        }).ToList()
    };

    #endregion

    #region Ping/Close

    public static Ping ToProtoPing() => new();

    public static BepPing FromProtoPing() => new();

    public static Close ToProto(BepClose close) => new()
    {
        Reason = close.Reason ?? string.Empty
    };

    public static BepClose FromProto(Close close) => new()
    {
        Reason = close.Reason
    };

    #endregion

    #region P2P Upgrade Extensions

    public static HelloExtensions ToProto(BepHelloExtensions extensions) => new()
    {
        AgentBinaryHash = extensions.AgentBinaryHash ?? string.Empty,
        AgentBinarySize = extensions.AgentBinarySize,
        AgentPlatform = extensions.AgentPlatform ?? string.Empty
    };

    public static BepHelloExtensions FromProto(HelloExtensions extensions) => new()
    {
        AgentBinaryHash = extensions.AgentBinaryHash,
        AgentBinarySize = extensions.AgentBinarySize,
        AgentPlatform = extensions.AgentPlatform
    };

    public static AgentUpdateRequest ToProto(BepAgentUpdateRequest request) => new()
    {
        FromVersion = request.FromVersion ?? string.Empty,
        ToVersion = request.ToVersion ?? string.Empty,
        Platform = request.Platform ?? string.Empty
    };

    public static BepAgentUpdateRequest FromProto(AgentUpdateRequest request) => new()
    {
        FromVersion = request.FromVersion,
        ToVersion = request.ToVersion,
        Platform = request.Platform
    };

    public static AgentUpdateResponse ToProto(BepAgentUpdateResponse response) => new()
    {
        Version = response.Version ?? string.Empty,
        Platform = response.Platform ?? string.Empty,
        Data = response.Data != null ? ByteString.CopyFrom(response.Data) : ByteString.Empty,
        Hash = response.Hash ?? string.Empty,
        TotalSize = response.TotalSize,
        ChunkIndex = response.ChunkIndex,
        TotalChunks = response.TotalChunks,
        IsComplete = response.IsComplete,
        Error = (ErrorCode)(int)response.Error
    };

    public static BepAgentUpdateResponse FromProto(AgentUpdateResponse response) => new()
    {
        Version = response.Version,
        Platform = response.Platform,
        Data = response.Data?.ToByteArray() ?? Array.Empty<byte>(),
        Hash = response.Hash,
        TotalSize = response.TotalSize,
        ChunkIndex = response.ChunkIndex,
        TotalChunks = response.TotalChunks,
        IsComplete = response.IsComplete,
        Error = (BepErrorCode)(int)response.Error
    };

    #endregion

    #region Device ID Helpers

    /// <summary>
    /// Parses a Syncthing device ID string to bytes.
    /// Format: XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX (Luhn-encoded base32)
    /// </summary>
    private static byte[] ParseDeviceId(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return [];
        }

        // Remove hyphens
        var clean = deviceId.Replace("-", "");

        // Decode base32 (Syncthing uses custom alphabet without padding)
        return DecodeBase32(clean);
    }

    /// <summary>
    /// Formats a device ID bytes to Syncthing string format.
    /// </summary>
    private static string FormatDeviceId(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return string.Empty;
        }

        // Encode to base32
        var base32 = EncodeBase32(bytes);

        // Insert hyphens every 7 characters
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < base32.Length; i++)
        {
            if (i > 0 && i % 7 == 0)
            {
                result.Append('-');
            }
            result.Append(base32[i]);
        }

        return result.ToString();
    }

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static byte[] DecodeBase32(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return [];
        }

        input = input.ToUpperInvariant().TrimEnd('=');

        var bits = 0;
        var value = 0;
        var bytes = new List<byte>();

        foreach (var c in input)
        {
            var index = Base32Alphabet.IndexOf(c);
            if (index < 0)
            {
                continue; // Skip invalid characters
            }

            value = (value << 5) | index;
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }

        return bytes.ToArray();
    }

    private static string EncodeBase32(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return string.Empty;
        }

        var result = new System.Text.StringBuilder();
        var bits = 0;
        var value = 0;

        foreach (var b in bytes)
        {
            value = (value << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                result.Append(Base32Alphabet[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            result.Append(Base32Alphabet[(value << (5 - bits)) & 31]);
        }

        return result.ToString();
    }

    #endregion
}
