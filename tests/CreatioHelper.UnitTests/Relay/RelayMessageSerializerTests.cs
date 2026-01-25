using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Sync.Relay;
using Xunit;

namespace CreatioHelper.UnitTests.Relay;

/// <summary>
/// Relay protocol unit tests following Syncthing's lib/relay/protocol patterns.
/// Tests XDR encoding, message serialization/deserialization, and protocol compliance.
/// </summary>
public class RelayMessageSerializerTests
{
    #region Protocol Constants Verification

    /// <summary>
    /// Verify the relay magic number constant matches Syncthing (0x9E79BC40)
    /// Reference: lib/relay/protocol/protocol.go - const Magic = 0x9E79BC40
    /// </summary>
    [Fact]
    public void Magic_MatchesSyncthingConstant()
    {
        Assert.Equal(0x9E79BC40u, RelayProtocol.Magic);

        // Verify big-endian byte order: 0x9E, 0x79, 0xBC, 0x40
        var magicBytes = new byte[] { 0x9E, 0x79, 0xBC, 0x40 };
        var reconstructed = (uint)((magicBytes[0] << 24) | (magicBytes[1] << 16) | (magicBytes[2] << 8) | magicBytes[3]);
        Assert.Equal(RelayProtocol.Magic, reconstructed);
    }

    /// <summary>
    /// Verify the protocol name matches Syncthing ("bep-relay")
    /// Reference: lib/relay/protocol/protocol.go - const protocolName = "bep-relay"
    /// </summary>
    [Fact]
    public void ProtocolName_MatchesSyncthing()
    {
        Assert.Equal("bep-relay", RelayProtocol.ProtocolName);
    }

    /// <summary>
    /// Verify the maximum message length matches Syncthing (1024 bytes)
    /// Reference: lib/relay/protocol/protocol.go - messageLength > 1024 returns bad length
    /// </summary>
    [Fact]
    public void MaxMessageLength_Is1024Bytes()
    {
        Assert.Equal(1024, RelayProtocol.MaxMessageLength);
    }

    /// <summary>
    /// Verify TLS handshake record type constant (0x16)
    /// Reference: TLS 1.2/1.3 spec - ContentType.Handshake = 22 = 0x16
    /// </summary>
    [Fact]
    public void TlsHandshakeRecordType_Is0x16()
    {
        Assert.Equal(0x16, RelayProtocol.TlsHandshakeRecordType);
    }

    #endregion

    #region Message Type Constants Verification

    /// <summary>
    /// Verify all message type values match Syncthing's iota-based constants
    /// Reference: lib/relay/protocol/packets.go
    /// </summary>
    [Theory]
    [InlineData(RelayProtocol.MessageType.Ping, 0)]
    [InlineData(RelayProtocol.MessageType.Pong, 1)]
    [InlineData(RelayProtocol.MessageType.JoinRelayRequest, 2)]
    [InlineData(RelayProtocol.MessageType.JoinSessionRequest, 3)]
    [InlineData(RelayProtocol.MessageType.Response, 4)]
    [InlineData(RelayProtocol.MessageType.ConnectRequest, 5)]
    [InlineData(RelayProtocol.MessageType.SessionInvitation, 6)]
    [InlineData(RelayProtocol.MessageType.RelayFull, 7)]
    public void MessageType_ValuesMatchSyncthing(RelayProtocol.MessageType messageType, int expectedValue)
    {
        Assert.Equal(expectedValue, (int)messageType);
    }

    #endregion

    #region Response Code Constants Verification

    /// <summary>
    /// Verify response code constants match Syncthing
    /// Reference: lib/relay/protocol/protocol.go - ResponseSuccess, ResponseNotFound, etc.
    /// </summary>
    [Theory]
    [InlineData(RelayProtocol.ResponseCode.Success, 0)]
    [InlineData(RelayProtocol.ResponseCode.NotFound, 1)]
    [InlineData(RelayProtocol.ResponseCode.AlreadyConnected, 2)]
    [InlineData(RelayProtocol.ResponseCode.WrongToken, 3)]
    [InlineData(RelayProtocol.ResponseCode.UnexpectedMessage, 100)]
    public void ResponseCode_ValuesMatchSyncthing(int responseCode, int expectedValue)
    {
        Assert.Equal(expectedValue, responseCode);
    }

    #endregion

    #region Header Size Verification

    /// <summary>
    /// Verify header size is 12 bytes (magic + type + length)
    /// Reference: XDR header = 4B magic + 4B type + 4B length = 12 bytes
    /// </summary>
    [Fact]
    public void HeaderSize_Is12Bytes()
    {
        Assert.Equal(12, RelayHeader.Size);
    }

    #endregion

    #region TLS Detection Tests

    /// <summary>
    /// Test IsTlsConnection correctly identifies TLS handshake byte
    /// </summary>
    [Theory]
    [InlineData(0x16, true)]  // TLS handshake record type
    [InlineData(0x15, false)] // Alert
    [InlineData(0x17, false)] // Application data
    [InlineData(0x00, false)] // Not TLS
    [InlineData(0xFF, false)] // Not TLS
    public void IsTlsConnection_DetectsCorrectly(byte firstByte, bool expectedResult)
    {
        var result = RelayProtocol.IsTlsConnection(firstByte);
        Assert.Equal(expectedResult, result);
    }

    #endregion

    #region ALPN Validation Tests

    /// <summary>
    /// Test ALPN protocol validation with correct protocol
    /// </summary>
    [Fact]
    public void ValidateAlpnProtocol_AcceptsCorrectProtocol()
    {
        var protocol = new SslApplicationProtocol(RelayProtocol.ProtocolName);
        Assert.True(RelayProtocol.ValidateAlpnProtocol(protocol));
    }

    /// <summary>
    /// Test ALPN protocol validation rejects incorrect protocol
    /// </summary>
    [Theory]
    [InlineData("bep/1.0")]
    [InlineData("http/1.1")]
    [InlineData("h2")]
    [InlineData("x")]
    public void ValidateAlpnProtocol_RejectsIncorrectProtocol(string protocolName)
    {
        var protocol = new SslApplicationProtocol(protocolName);
        Assert.False(RelayProtocol.ValidateAlpnProtocol(protocol));
    }

    /// <summary>
    /// Test ALPN validation handles default protocol
    /// </summary>
    [Fact]
    public void ValidateAlpnProtocol_RejectsDefaultProtocol()
    {
        var protocol = default(SslApplicationProtocol);
        Assert.False(RelayProtocol.ValidateAlpnProtocol(protocol));
    }

    #endregion

    #region XDR Padding Calculation Tests

    /// <summary>
    /// Test XDR padding calculation formula: (4 - (length % 4)) % 4
    /// Reference: RFC 4506 and Syncthing's github.com/calmh/xdr.Padding()
    /// </summary>
    [Theory]
    [InlineData(0, 0)]  // Already aligned
    [InlineData(1, 3)]  // Need 3 bytes to reach 4
    [InlineData(2, 2)]  // Need 2 bytes to reach 4
    [InlineData(3, 1)]  // Need 1 byte to reach 4
    [InlineData(4, 0)]  // Already aligned
    [InlineData(5, 3)]  // Need 3 bytes to reach 8
    [InlineData(6, 2)]  // Need 2 bytes to reach 8
    [InlineData(7, 1)]  // Need 1 byte to reach 8
    [InlineData(8, 0)]  // Already aligned
    [InlineData(32, 0)] // Common device ID size - aligned
    [InlineData(33, 3)] // 33 bytes needs 3 padding
    public void XdrPadding_CalculatesCorrectly(int length, int expectedPadding)
    {
        // XDR padding formula: (4 - (length % 4)) % 4
        var actualPadding = (4 - (length % 4)) % 4;
        Assert.Equal(expectedPadding, actualPadding);
    }

    #endregion

    #region Ping/Pong Message Tests

    /// <summary>
    /// Test Ping message round-trip serialization
    /// </summary>
    [Fact]
    public async Task Ping_RoundTrip_PreservesType()
    {
        // Arrange
        var ping = new Ping();
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, ping);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream);

        // Assert
        Assert.IsType<Ping>(deserialized);
        Assert.Equal(RelayProtocol.MessageType.Ping, deserialized.Type);
    }

    /// <summary>
    /// Test Pong message round-trip serialization
    /// </summary>
    [Fact]
    public async Task Pong_RoundTrip_PreservesType()
    {
        // Arrange
        var pong = new Pong();
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, pong);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream);

        // Assert
        Assert.IsType<Pong>(deserialized);
        Assert.Equal(RelayProtocol.MessageType.Pong, deserialized.Type);
    }

    /// <summary>
    /// Test Ping message wire format (header only, no payload)
    /// </summary>
    [Fact]
    public async Task Ping_WireFormat_IsCorrect()
    {
        // Arrange
        var ping = new Ping();
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, ping);
        var bytes = stream.ToArray();

        // Assert - Should be exactly 12 bytes (header only)
        Assert.Equal(12, bytes.Length);

        // Verify magic number (big-endian)
        var magic = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
        Assert.Equal(RelayProtocol.Magic, magic);

        // Verify message type
        var messageType = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4, 4));
        Assert.Equal((int)RelayProtocol.MessageType.Ping, messageType);

        // Verify message length (0 for Ping)
        var messageLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8, 4));
        Assert.Equal(0, messageLength);
    }

    #endregion

    #region RelayFull Message Tests

    /// <summary>
    /// Test RelayFull message round-trip serialization
    /// </summary>
    [Fact]
    public async Task RelayFull_RoundTrip_PreservesType()
    {
        // Arrange
        var relayFull = new RelayFull();
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, relayFull);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream);

        // Assert
        Assert.IsType<RelayFull>(deserialized);
        Assert.Equal(RelayProtocol.MessageType.RelayFull, deserialized.Type);
    }

    #endregion

    #region JoinRelayRequest Message Tests

    /// <summary>
    /// Test JoinRelayRequest with empty token (prior protocol version)
    /// </summary>
    [Fact]
    public async Task JoinRelayRequest_EmptyToken_RoundTrip()
    {
        // Arrange
        var request = new JoinRelayRequest("");
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, request);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream) as JoinRelayRequest;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("", deserialized.Token);
    }

    /// <summary>
    /// Test JoinRelayRequest with token
    /// </summary>
    [Fact]
    public async Task JoinRelayRequest_WithToken_RoundTrip()
    {
        // Arrange
        var token = "my-relay-token-12345";
        var request = new JoinRelayRequest(token);
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, request);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream) as JoinRelayRequest;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(token, deserialized.Token);
    }

    /// <summary>
    /// Test JoinRelayRequest XDR encoding (string with padding)
    /// </summary>
    [Fact]
    public async Task JoinRelayRequest_XdrEncoding_HasCorrectPadding()
    {
        // Arrange - Token of length 5 should have 3 bytes padding
        var token = "12345";
        var request = new JoinRelayRequest(token);
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, request);
        var bytes = stream.ToArray();

        // Assert
        // Header = 12 bytes
        // Payload = 4 (length) + 5 (data) + 3 (padding) = 12 bytes
        Assert.Equal(24, bytes.Length);

        // Verify payload length in header
        var messageLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8, 4));
        Assert.Equal(12, messageLength); // 4 + 5 + 3 padding

        // Verify string length field
        var stringLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(12, 4));
        Assert.Equal(5, stringLength);

        // Verify string content
        var stringContent = Encoding.UTF8.GetString(bytes.AsSpan(16, 5));
        Assert.Equal(token, stringContent);

        // Verify padding is zeros
        Assert.Equal(0, bytes[21]);
        Assert.Equal(0, bytes[22]);
        Assert.Equal(0, bytes[23]);
    }

    #endregion

    #region JoinSessionRequest Message Tests

    /// <summary>
    /// Test JoinSessionRequest with empty key
    /// </summary>
    [Fact]
    public async Task JoinSessionRequest_EmptyKey_RoundTrip()
    {
        // Arrange
        var request = new JoinSessionRequest();
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, request);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream) as JoinSessionRequest;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Empty(deserialized.Key);
    }

    /// <summary>
    /// Test JoinSessionRequest with 32-byte key (typical session key)
    /// </summary>
    [Fact]
    public async Task JoinSessionRequest_With32ByteKey_RoundTrip()
    {
        // Arrange
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var request = new JoinSessionRequest(key);
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, request);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream) as JoinSessionRequest;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(key, deserialized.Key);
    }

    /// <summary>
    /// Test JoinSessionRequest rejects key longer than 32 bytes
    /// </summary>
    [Fact]
    public async Task JoinSessionRequest_KeyTooLong_Throws()
    {
        // Arrange
        var key = new byte[33]; // Too long
        var request = new JoinSessionRequest(key);
        using var stream = new MemoryStream();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RelayMessageSerializer.WriteMessageAsync(stream, request));
    }

    #endregion

    #region Response Message Tests

    /// <summary>
    /// Test Response message round-trip
    /// </summary>
    [Fact]
    public async Task Response_RoundTrip_PreservesData()
    {
        // Arrange
        var response = new Response(0, "success");
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, response);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream) as Response;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(0, deserialized.Code);
        Assert.Equal("success", deserialized.Message);
    }

    /// <summary>
    /// Test all standard response types
    /// </summary>
    [Theory]
    [InlineData(0, "success")]
    [InlineData(1, "not found")]
    [InlineData(2, "already connected")]
    [InlineData(3, "wrong token")]
    [InlineData(100, "unexpected message")]
    public async Task Response_StandardCodes_RoundTrip(int code, string message)
    {
        // Arrange
        var response = new Response(code, message);
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, response);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream) as Response;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(code, deserialized.Code);
        Assert.Equal(message, deserialized.Message);
    }

    /// <summary>
    /// Test Response.IsSuccess property
    /// </summary>
    [Fact]
    public void Response_IsSuccess_WorksCorrectly()
    {
        Assert.True(Response.Success.IsSuccess);
        Assert.False(Response.NotFound.IsSuccess);
        Assert.False(Response.AlreadyConnected.IsSuccess);
        Assert.False(Response.WrongToken.IsSuccess);
        Assert.False(Response.UnexpectedMessage.IsSuccess);
    }

    /// <summary>
    /// Test Response wire format (code + XDR string)
    /// </summary>
    [Fact]
    public async Task Response_WireFormat_IsCorrect()
    {
        // Arrange - "ok" has 2 chars, needs 2 bytes padding
        var response = new Response(0, "ok");
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, response);
        var bytes = stream.ToArray();

        // Assert
        // Header = 12 bytes
        // Payload = 4 (code) + 4 (string length) + 2 (data) + 2 (padding) = 12 bytes
        Assert.Equal(24, bytes.Length);

        // Verify code (int32 big-endian)
        var code = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(12, 4));
        Assert.Equal(0, code);

        // Verify string length
        var stringLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        Assert.Equal(2, stringLength);

        // Verify string content
        var stringContent = Encoding.UTF8.GetString(bytes.AsSpan(20, 2));
        Assert.Equal("ok", stringContent);
    }

    #endregion

    #region ConnectRequest Message Tests

    /// <summary>
    /// Test ConnectRequest with empty ID
    /// </summary>
    [Fact]
    public async Task ConnectRequest_EmptyId_RoundTrip()
    {
        // Arrange
        var request = new ConnectRequest();
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, request);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream) as ConnectRequest;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Empty(deserialized.Id);
    }

    /// <summary>
    /// Test ConnectRequest with 32-byte device ID (SHA-256 hash)
    /// </summary>
    [Fact]
    public async Task ConnectRequest_With32ByteDeviceId_RoundTrip()
    {
        // Arrange
        var deviceId = new byte[32];
        Random.Shared.NextBytes(deviceId);
        var request = new ConnectRequest(deviceId);
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, request);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream) as ConnectRequest;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(deviceId, deserialized.Id);
    }

    /// <summary>
    /// Test ConnectRequest rejects ID longer than 32 bytes
    /// </summary>
    [Fact]
    public async Task ConnectRequest_IdTooLong_Throws()
    {
        // Arrange
        var id = new byte[33]; // Too long
        var request = new ConnectRequest(id);
        using var stream = new MemoryStream();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RelayMessageSerializer.WriteMessageAsync(stream, request));
    }

    #endregion

    #region SessionInvitation Message Tests

    /// <summary>
    /// Test SessionInvitation round-trip with typical values
    /// </summary>
    [Fact]
    public async Task SessionInvitation_RoundTrip_PreservesData()
    {
        // Arrange
        var from = new byte[32];
        var key = new byte[32];
        var address = new byte[] { 192, 168, 1, 100 };
        Random.Shared.NextBytes(from);
        Random.Shared.NextBytes(key);

        var invitation = new SessionInvitation(from, key, address, 22000, true);
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, invitation);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream) as SessionInvitation;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(from, deserialized.From);
        Assert.Equal(key, deserialized.Key);
        Assert.Equal(address, deserialized.Address);
        Assert.Equal(22000, deserialized.Port);
        Assert.True(deserialized.ServerSocket);
    }

    /// <summary>
    /// Test SessionInvitation with empty fields
    /// </summary>
    [Fact]
    public async Task SessionInvitation_EmptyFields_RoundTrip()
    {
        // Arrange
        var invitation = new SessionInvitation();
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, invitation);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream) as SessionInvitation;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Empty(deserialized.From);
        Assert.Empty(deserialized.Key);
        Assert.Empty(deserialized.Address);
        Assert.Equal(0, deserialized.Port);
        Assert.False(deserialized.ServerSocket);
    }

    /// <summary>
    /// Test SessionInvitation with ServerSocket=false
    /// </summary>
    [Fact]
    public async Task SessionInvitation_ServerSocketFalse_RoundTrip()
    {
        // Arrange
        var invitation = new SessionInvitation(
            new byte[32],
            new byte[32],
            new byte[] { 10, 0, 0, 1 },
            8080,
            false
        );
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, invitation);
        stream.Position = 0;
        var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream) as SessionInvitation;

        // Assert
        Assert.NotNull(deserialized);
        Assert.False(deserialized.ServerSocket);
    }

    /// <summary>
    /// Test SessionInvitation port encoding (uint16 as uint32 with zero prefix)
    /// XDR uint16: [2B zeros][2B value] big-endian
    /// </summary>
    [Fact]
    public async Task SessionInvitation_PortEncoding_IsXdrUint16()
    {
        // Arrange
        var invitation = new SessionInvitation(
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            22000,
            false
        );
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, invitation);
        var bytes = stream.ToArray();

        // Assert
        // Header = 12 bytes
        // From = 4 (length of 0)
        // Key = 4 (length of 0)
        // Address = 4 (length of 0)
        // Port = 4 bytes (XDR uint16)
        // ServerSocket = 4 bytes (XDR bool)
        Assert.Equal(12 + 4 + 4 + 4 + 4 + 4, bytes.Length);

        // Port is at offset 12 + 4 + 4 + 4 = 24
        var portBytes = bytes.AsSpan(24, 4);
        // First 2 bytes should be zero (XDR uint16 zero prefix)
        Assert.Equal(0, portBytes[0]);
        Assert.Equal(0, portBytes[1]);
        // Port value in big-endian
        var port = (ushort)((portBytes[2] << 8) | portBytes[3]);
        Assert.Equal(22000, port);
    }

    /// <summary>
    /// Test SessionInvitation bool encoding (uint32 with 0 or 1)
    /// XDR bool: [4B value] where value is 0 or 1
    /// </summary>
    [Theory]
    [InlineData(true, 1u)]
    [InlineData(false, 0u)]
    public async Task SessionInvitation_BoolEncoding_IsXdrBool(bool serverSocket, uint expectedValue)
    {
        // Arrange
        var invitation = new SessionInvitation(
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            0,
            serverSocket
        );
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, invitation);
        var bytes = stream.ToArray();

        // ServerSocket is at offset 12 + 4 + 4 + 4 + 4 = 28
        var boolValue = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(28, 4));
        Assert.Equal(expectedValue, boolValue);
    }

    /// <summary>
    /// Test SessionInvitation rejects fields longer than 32 bytes
    /// </summary>
    [Theory]
    [InlineData(33, 0, 0)]  // From too long
    [InlineData(0, 33, 0)]  // Key too long
    [InlineData(0, 0, 33)]  // Address too long
    public async Task SessionInvitation_FieldsTooLong_Throws(int fromLen, int keyLen, int addressLen)
    {
        // Arrange
        var invitation = new SessionInvitation(
            new byte[fromLen],
            new byte[keyLen],
            new byte[addressLen],
            0,
            false
        );
        using var stream = new MemoryStream();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RelayMessageSerializer.WriteMessageAsync(stream, invitation));
    }

    /// <summary>
    /// Test SessionInvitation ToString format
    /// </summary>
    [Fact]
    public void SessionInvitation_ToString_FormatsCorrectly()
    {
        // Arrange
        var from = new byte[32];
        for (int i = 0; i < 32; i++) from[i] = (byte)i;
        var address = new byte[] { 192, 168, 1, 100 };

        var invitation = new SessionInvitation(from, new byte[32], address, 22000, true);

        // Act
        var str = invitation.ToString();

        // Assert - Should contain hex device ID and IP:port
        Assert.Contains("192.168.1.100:22000", str);
    }

    #endregion

    #region Header Serialization Tests

    /// <summary>
    /// Test header contains correct magic number in big-endian
    /// </summary>
    [Fact]
    public async Task Header_MagicNumber_IsBigEndian()
    {
        // Arrange
        var ping = new Ping();
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, ping);
        var bytes = stream.ToArray();

        // Assert - Magic should be 0x9E79BC40 in big-endian
        Assert.Equal(0x9E, bytes[0]);
        Assert.Equal(0x79, bytes[1]);
        Assert.Equal(0xBC, bytes[2]);
        Assert.Equal(0x40, bytes[3]);
    }

    /// <summary>
    /// Test header message type is big-endian int32
    /// </summary>
    [Theory]
    [InlineData(RelayProtocol.MessageType.Ping, 0)]
    [InlineData(RelayProtocol.MessageType.Pong, 1)]
    [InlineData(RelayProtocol.MessageType.JoinRelayRequest, 2)]
    [InlineData(RelayProtocol.MessageType.SessionInvitation, 6)]
    public async Task Header_MessageType_IsBigEndianInt32(RelayProtocol.MessageType messageType, int expectedValue)
    {
        // Arrange
        IRelayMessage message = messageType switch
        {
            RelayProtocol.MessageType.Ping => new Ping(),
            RelayProtocol.MessageType.Pong => new Pong(),
            RelayProtocol.MessageType.JoinRelayRequest => new JoinRelayRequest(""),
            RelayProtocol.MessageType.SessionInvitation => new SessionInvitation(),
            _ => throw new ArgumentException("Unsupported message type")
        };
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, message);
        var bytes = stream.ToArray();

        // Assert - Message type at offset 4
        var typeValue = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4, 4));
        Assert.Equal(expectedValue, typeValue);
    }

    #endregion

    #region Error Handling Tests

    /// <summary>
    /// Test reading message with invalid magic number
    /// </summary>
    [Fact]
    public async Task ReadMessage_InvalidMagic_Throws()
    {
        // Arrange - Header with wrong magic
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0x12345678); // Wrong magic
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4, 4), 0); // Ping type
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 0); // Length 0

        using var stream = new MemoryStream(bytes);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RelayMessageSerializer.ReadMessageAsync(stream));
    }

    /// <summary>
    /// Test reading message with negative length
    /// </summary>
    [Fact]
    public async Task ReadMessage_NegativeLength_Throws()
    {
        // Arrange - Header with negative length
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), RelayProtocol.Magic);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4, 4), 0); // Ping type
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), -1); // Negative length

        using var stream = new MemoryStream(bytes);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RelayMessageSerializer.ReadMessageAsync(stream));
    }

    /// <summary>
    /// Test reading message with excessive length
    /// </summary>
    [Fact]
    public async Task ReadMessage_ExcessiveLength_Throws()
    {
        // Arrange - Header with length > 1MB
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), RelayProtocol.Magic);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4, 4), 0); // Ping type
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 2 * 1024 * 1024); // 2MB

        using var stream = new MemoryStream(bytes);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RelayMessageSerializer.ReadMessageAsync(stream));
    }

    /// <summary>
    /// Test reading truncated message
    /// </summary>
    [Fact]
    public async Task ReadMessage_Truncated_Throws()
    {
        // Arrange - Only 6 bytes (incomplete header)
        var bytes = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), RelayProtocol.Magic);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(4, 2), 0); // Incomplete

        using var stream = new MemoryStream(bytes);

        // Act & Assert
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            RelayMessageSerializer.ReadMessageAsync(stream));
    }

    /// <summary>
    /// Test reading message with unknown type
    /// </summary>
    [Fact]
    public async Task ReadMessage_UnknownType_Throws()
    {
        // Arrange - Header with unknown message type
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), RelayProtocol.Magic);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4, 4), 999); // Unknown type
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 0); // Length 0

        using var stream = new MemoryStream(bytes);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RelayMessageSerializer.ReadMessageAsync(stream));
    }

    #endregion

    #region Exception Classes Tests

    /// <summary>
    /// Test RelayFullException creation and properties
    /// </summary>
    [Fact]
    public void RelayFullException_CreatesCorrectly()
    {
        // Arrange & Act
        var ex1 = new RelayFullException();
        var uri = new Uri("relay://example.com:443");
        var ex2 = new RelayFullException(uri);
        var ex3 = new RelayFullException("custom message");

        // Assert
        Assert.Equal("relay full", ex1.Message);
        Assert.Contains("relay full", ex2.Message);
        Assert.Equal(uri, ex2.RelayUri);
        Assert.Equal("custom message", ex3.Message);
    }

    /// <summary>
    /// Test RelayIncorrectResponseCodeException creation and properties
    /// </summary>
    [Fact]
    public void RelayIncorrectResponseCodeException_CreatesCorrectly()
    {
        // Arrange & Act
        var ex = new RelayIncorrectResponseCodeException(1, "not found");

        // Assert
        Assert.Equal(1, ex.Code);
        Assert.Equal("not found", ex.ResponseMessage);
        Assert.Contains("incorrect response code 1", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    /// <summary>
    /// Test RelayFullEventArgs creation
    /// </summary>
    [Fact]
    public void RelayFullEventArgs_CreatesCorrectly()
    {
        // Arrange
        var uri = new Uri("relay://example.com:443");

        // Act
        var args = new RelayFullEventArgs(uri);

        // Assert
        Assert.Equal(uri, args.RelayUri);
        Assert.True((DateTime.UtcNow - args.Timestamp).TotalSeconds < 5);
    }

    #endregion

    #region All Message Types Wire Format Verification

    /// <summary>
    /// Verify all message types produce valid wire format with correct header
    /// </summary>
    [Theory]
    [InlineData(typeof(Ping), RelayProtocol.MessageType.Ping)]
    [InlineData(typeof(Pong), RelayProtocol.MessageType.Pong)]
    [InlineData(typeof(RelayFull), RelayProtocol.MessageType.RelayFull)]
    public async Task AllEmptyPayloadMessages_HaveCorrectWireFormat(Type messageType, RelayProtocol.MessageType expectedType)
    {
        // Arrange
        var message = (IRelayMessage)Activator.CreateInstance(messageType)!;
        using var stream = new MemoryStream();

        // Act
        await RelayMessageSerializer.WriteMessageAsync(stream, message);
        var bytes = stream.ToArray();

        // Assert
        Assert.Equal(12, bytes.Length); // Header only

        var magic = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
        Assert.Equal(RelayProtocol.Magic, magic);

        var type = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4, 4));
        Assert.Equal((int)expectedType, type);

        var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8, 4));
        Assert.Equal(0, length);
    }

    /// <summary>
    /// Test concurrent serialization is thread-safe
    /// </summary>
    [Fact]
    public async Task Serialization_IsThreadSafe()
    {
        // Arrange
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                IRelayMessage message = (index % 4) switch
                {
                    0 => new Ping(),
                    1 => new Pong(),
                    2 => new JoinRelayRequest($"token-{index}"),
                    _ => new Response(0, $"success-{index}")
                };

                using var stream = new MemoryStream();
                await RelayMessageSerializer.WriteMessageAsync(stream, message);
                stream.Position = 0;
                var deserialized = await RelayMessageSerializer.ReadMessageAsync(stream);
                Assert.Equal(message.Type, deserialized.Type);
            }));
        }

        // Assert - All tasks should complete without exception
        await Task.WhenAll(tasks);
    }

    #endregion

    #region RelayHeader Tests

    /// <summary>
    /// Test RelayHeader default constructor sets magic
    /// </summary>
    [Fact]
    public void RelayHeader_Constructor_SetsMagic()
    {
        // Act
        var header = new RelayHeader(RelayProtocol.MessageType.Ping, 0);

        // Assert
        Assert.Equal(RelayProtocol.Magic, header.Magic);
        Assert.Equal(RelayProtocol.MessageType.Ping, header.MessageType);
        Assert.Equal(0, header.MessageLength);
    }

    /// <summary>
    /// Test RelayHeader with various message lengths
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(1024)]
    public void RelayHeader_VariousLengths_PreservesValue(int length)
    {
        // Act
        var header = new RelayHeader(RelayProtocol.MessageType.Response, length);

        // Assert
        Assert.Equal(length, header.MessageLength);
    }

    #endregion
}
