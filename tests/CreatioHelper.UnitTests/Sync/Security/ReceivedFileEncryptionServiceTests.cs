using CreatioHelper.Infrastructure.Services.Sync.Security;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Security;

public class ReceivedFileEncryptionServiceTests
{
    private readonly Mock<ILogger<ReceivedFileEncryptionService>> _loggerMock;
    private readonly ReceivedFileEncryptionConfiguration _config;
    private readonly ReceivedFileEncryptionService _service;

    public ReceivedFileEncryptionServiceTests()
    {
        _loggerMock = new Mock<ILogger<ReceivedFileEncryptionService>>();
        _config = new ReceivedFileEncryptionConfiguration();
        _service = new ReceivedFileEncryptionService(_loggerMock.Object, _config);
    }

    #region IsEncryptionEnabled Tests

    [Fact]
    public void IsEncryptionEnabled_Default_ReturnsFalse()
    {
        Assert.False(_service.IsEncryptionEnabled("folder1"));
    }

    [Fact]
    public void IsEncryptionEnabled_AfterEnabled_ReturnsTrue()
    {
        _service.SetEncryptionEnabled("folder1", true);

        Assert.True(_service.IsEncryptionEnabled("folder1"));
    }

    [Fact]
    public void IsEncryptionEnabled_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.IsEncryptionEnabled(null!));
    }

    #endregion

    #region SetEncryptionEnabled Tests

    [Fact]
    public void SetEncryptionEnabled_True_EnablesEncryption()
    {
        _service.SetEncryptionEnabled("folder1", true);

        Assert.True(_service.IsEncryptionEnabled("folder1"));
        Assert.NotEqual(EncryptionMode.None, _service.GetEncryptionMode("folder1"));
    }

    [Fact]
    public void SetEncryptionEnabled_False_DisablesEncryption()
    {
        _service.SetEncryptionEnabled("folder1", true);
        _service.SetEncryptionEnabled("folder1", false);

        Assert.False(_service.IsEncryptionEnabled("folder1"));
    }

    [Fact]
    public void SetEncryptionEnabled_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetEncryptionEnabled(null!, true));
    }

    #endregion

    #region GetEncryptionMode Tests

    [Fact]
    public void GetEncryptionMode_Default_ReturnsNone()
    {
        Assert.Equal(EncryptionMode.None, _service.GetEncryptionMode("folder1"));
    }

    [Fact]
    public void GetEncryptionMode_AfterSet_ReturnsSetValue()
    {
        _service.SetEncryptionMode("folder1", EncryptionMode.ContentOnly);

        Assert.Equal(EncryptionMode.ContentOnly, _service.GetEncryptionMode("folder1"));
    }

    [Fact]
    public void GetEncryptionMode_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetEncryptionMode(null!));
    }

    #endregion

    #region SetEncryptionMode Tests

    [Fact]
    public void SetEncryptionMode_ValidMode_SetsCorrectly()
    {
        _service.SetEncryptionMode("folder1", EncryptionMode.Full);

        Assert.Equal(EncryptionMode.Full, _service.GetEncryptionMode("folder1"));
    }

    [Fact]
    public void SetEncryptionMode_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetEncryptionMode(null!, EncryptionMode.Full));
    }

    #endregion

    #region EncryptFileAsync Tests

    [Fact]
    public async Task EncryptFileAsync_NoEncryption_ReturnsOriginalStream()
    {
        var content = "Test content"u8.ToArray();
        using var stream = new MemoryStream(content);

        var result = await _service.EncryptFileAsync("folder1", "test.txt", stream);

        Assert.True(result.Success);
        Assert.NotNull(result.EncryptedStream);
        Assert.Equal(content.Length, result.OriginalSize);
    }

    [Fact]
    public async Task EncryptFileAsync_WithEncryption_EncryptsContent()
    {
        _service.SetEncryptionMode("folder1", EncryptionMode.Full);
        _service.SetPassword("folder1", "test-password");

        var content = "Test content to encrypt"u8.ToArray();
        using var stream = new MemoryStream(content);

        var result = await _service.EncryptFileAsync("folder1", "test.txt", stream);

        Assert.True(result.Success);
        Assert.NotNull(result.EncryptedStream);
        Assert.True(result.EncryptedSize > content.Length); // Encrypted + nonce + tag
    }

    [Fact]
    public async Task EncryptFileAsync_NoPassword_ReturnsError()
    {
        _service.SetEncryptionMode("folder1", EncryptionMode.Full);

        var content = "Test content"u8.ToArray();
        using var stream = new MemoryStream(content);

        var result = await _service.EncryptFileAsync("folder1", "test.txt", stream);

        Assert.False(result.Success);
        Assert.Contains("No encryption key", result.ErrorMessage);
    }

    [Fact]
    public async Task EncryptFileAsync_NullFolderId_ThrowsArgumentNull()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.EncryptFileAsync(null!, "test.txt", stream));
    }

    #endregion

    #region DecryptFileAsync Tests

    [Fact]
    public async Task DecryptFileAsync_NoEncryption_ReturnsOriginalStream()
    {
        var content = "Test content"u8.ToArray();
        using var stream = new MemoryStream(content);

        var result = await _service.DecryptFileAsync("folder1", "test.txt", stream);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task DecryptFileAsync_WithEncryption_DecryptsContent()
    {
        _service.SetEncryptionMode("folder1", EncryptionMode.Full);
        _service.SetPassword("folder1", "test-password");

        var original = "Test content to encrypt"u8.ToArray();
        using var originalStream = new MemoryStream(original);

        var encryptResult = await _service.EncryptFileAsync("folder1", "test.txt", originalStream);
        Assert.True(encryptResult.Success);

        var decrypted = await _service.DecryptFileAsync("folder1", "test.txt", encryptResult.EncryptedStream!);

        using var reader = new StreamReader(decrypted);
        var decryptedContent = await reader.ReadToEndAsync();
        Assert.Equal("Test content to encrypt", decryptedContent);
    }

    [Fact]
    public async Task DecryptFileAsync_NullFolderId_ThrowsArgumentNull()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.DecryptFileAsync(null!, "test.txt", stream));
    }

    #endregion

    #region EncryptFileName Tests

    [Fact]
    public void EncryptFileName_NoEncryption_ReturnsOriginal()
    {
        var result = _service.EncryptFileName("folder1", "test.txt");

        Assert.Equal("test.txt", result);
    }

    [Fact]
    public void EncryptFileName_ContentOnlyMode_ReturnsOriginal()
    {
        _service.SetEncryptionMode("folder1", EncryptionMode.ContentOnly);
        _service.SetPassword("folder1", "password");

        var result = _service.EncryptFileName("folder1", "test.txt");

        Assert.Equal("test.txt", result);
    }

    [Fact]
    public void EncryptFileName_FullMode_ReturnsEncrypted()
    {
        _service.SetEncryptionMode("folder1", EncryptionMode.Full);
        _service.SetPassword("folder1", "password");

        var result = _service.EncryptFileName("folder1", "test.txt");

        Assert.StartsWith(_config.EncryptedFilePrefix, result);
        Assert.NotEqual("test.txt", result);
    }

    [Fact]
    public void EncryptFileName_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.EncryptFileName(null!, "test.txt"));
    }

    #endregion

    #region SetPassword Tests

    [Fact]
    public void SetPassword_ValidPassword_SetsPassword()
    {
        _service.SetPassword("folder1", "my-password");

        Assert.True(_service.HasPassword("folder1"));
    }

    [Fact]
    public void SetPassword_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetPassword(null!, "password"));
    }

    [Fact]
    public void SetPassword_NullPassword_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetPassword("folder1", null!));
    }

    #endregion

    #region HasPassword Tests

    [Fact]
    public void HasPassword_NoPassword_ReturnsFalse()
    {
        Assert.False(_service.HasPassword("folder1"));
    }

    [Fact]
    public void HasPassword_WithPassword_ReturnsTrue()
    {
        _service.SetPassword("folder1", "password");

        Assert.True(_service.HasPassword("folder1"));
    }

    [Fact]
    public void HasPassword_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.HasPassword(null!));
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_Initial_ReturnsEmptyStats()
    {
        var stats = _service.GetStats("folder1");

        Assert.Equal("folder1", stats.FolderId);
        Assert.Equal(0, stats.FilesEncrypted);
        Assert.Equal(0, stats.FilesDecrypted);
    }

    [Fact]
    public async Task GetStats_AfterEncryption_TracksStats()
    {
        _service.SetEncryptionMode("folder1", EncryptionMode.Full);
        _service.SetPassword("folder1", "password");

        using var stream = new MemoryStream("Test"u8.ToArray());
        await _service.EncryptFileAsync("folder1", "test.txt", stream);

        var stats = _service.GetStats("folder1");

        Assert.Equal(1, stats.FilesEncrypted);
        Assert.NotNull(stats.LastEncryption);
    }

    [Fact]
    public void GetStats_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetStats(null!));
    }

    #endregion

    #region VerifyIntegrityAsync Tests

    [Fact]
    public async Task VerifyIntegrityAsync_ValidEncryption_ReturnsTrue()
    {
        _service.SetEncryptionMode("folder1", EncryptionMode.Full);
        _service.SetPassword("folder1", "password");

        using var stream = new MemoryStream("Test content"u8.ToArray());
        var result = await _service.EncryptFileAsync("folder1", "test.txt", stream);

        var isValid = await _service.VerifyIntegrityAsync("folder1", "test.txt", result.EncryptedStream!);

        Assert.True(isValid);
    }

    [Fact]
    public async Task VerifyIntegrityAsync_NullFolderId_ThrowsArgumentNull()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.VerifyIntegrityAsync(null!, "test.txt", stream));
    }

    #endregion
}

public class ReceivedFileEncryptionConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new ReceivedFileEncryptionConfiguration();

        Assert.Equal(EncryptionMode.None, config.DefaultMode);
        Assert.Equal(100000, config.KeyDerivationIterations);
        Assert.Equal(256, config.KeySizeBits);
        Assert.Equal(12, config.NonceSizeBytes);
        Assert.Equal(64 * 1024, config.BufferSize);
    }
}
