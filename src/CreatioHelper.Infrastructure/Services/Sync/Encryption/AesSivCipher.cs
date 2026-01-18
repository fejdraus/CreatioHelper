using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace CreatioHelper.Infrastructure.Services.Sync.Encryption;

/// <summary>
/// AES-SIV (Synthetic Initialization Vector) cipher implementation according to RFC 5297.
/// Provides deterministic authenticated encryption that is nonce-misuse resistant.
/// Compatible with Syncthing's encrypted folder implementation.
/// </summary>
public class AesSivCipher
{
    /// <summary>
    /// SIV tag size in bytes (128 bits).
    /// </summary>
    public const int SivTagSize = 16;

    /// <summary>
    /// Key size in bytes (256 bits for AES-256-SIV, split into two 128-bit keys).
    /// </summary>
    public const int KeySize = 32;

    private const int BlockSize = 16;
    private static readonly byte[] Zero = new byte[BlockSize];
    private static readonly byte[] Rb = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x87 };

    /// <summary>
    /// Encrypts plaintext using AES-SIV with optional associated data.
    /// </summary>
    /// <param name="plaintext">The plaintext to encrypt.</param>
    /// <param name="key">The 256-bit key (split internally into two 128-bit keys).</param>
    /// <param name="associatedData">Optional associated data (authenticated but not encrypted).</param>
    /// <returns>The ciphertext with prepended SIV tag (16 bytes + plaintext length).</returns>
    public byte[] Encrypt(byte[] plaintext, byte[] key, params byte[][]? associatedData)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Key must be {KeySize} bytes", nameof(key));
        }

        // Split key into K1 (CMAC key) and K2 (CTR key)
        var k1 = new byte[16];
        var k2 = new byte[16];
        Buffer.BlockCopy(key, 0, k1, 0, 16);
        Buffer.BlockCopy(key, 16, k2, 0, 16);

        // S2V: Generate synthetic IV from associated data and plaintext
        var siv = S2V(k1, plaintext, associatedData ?? []);

        // CTR encrypt using SIV as counter (with high bit cleared per RFC 5297)
        var counter = (byte[])siv.Clone();
        counter[8] &= 0x7F;  // Clear high bit of first 64-bit block
        counter[12] &= 0x7F; // Clear high bit of second 64-bit block

        var ciphertext = CtrEncrypt(k2, counter, plaintext);

        // Prepend SIV to ciphertext
        var result = new byte[SivTagSize + ciphertext.Length];
        Buffer.BlockCopy(siv, 0, result, 0, SivTagSize);
        Buffer.BlockCopy(ciphertext, 0, result, SivTagSize, ciphertext.Length);

        return result;
    }

    /// <summary>
    /// Decrypts ciphertext using AES-SIV with optional associated data.
    /// </summary>
    /// <param name="ciphertext">The ciphertext with prepended SIV tag.</param>
    /// <param name="key">The 256-bit key (split internally into two 128-bit keys).</param>
    /// <param name="associatedData">Optional associated data (must match encryption).</param>
    /// <returns>The decrypted plaintext.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Thrown if authentication fails.</exception>
    public byte[] Decrypt(byte[] ciphertext, byte[] key, params byte[][]? associatedData)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Key must be {KeySize} bytes", nameof(key));
        }

        if (ciphertext.Length < SivTagSize)
        {
            throw new ArgumentException("Ciphertext too short", nameof(ciphertext));
        }

        // Split key into K1 (CMAC key) and K2 (CTR key)
        var k1 = new byte[16];
        var k2 = new byte[16];
        Buffer.BlockCopy(key, 0, k1, 0, 16);
        Buffer.BlockCopy(key, 16, k2, 0, 16);

        // Extract SIV from ciphertext
        var siv = new byte[SivTagSize];
        Buffer.BlockCopy(ciphertext, 0, siv, 0, SivTagSize);

        // Extract encrypted data
        var encryptedData = new byte[ciphertext.Length - SivTagSize];
        Buffer.BlockCopy(ciphertext, SivTagSize, encryptedData, 0, encryptedData.Length);

        // CTR decrypt using SIV as counter
        var counter = (byte[])siv.Clone();
        counter[8] &= 0x7F;  // Clear high bit of first 64-bit block
        counter[12] &= 0x7F; // Clear high bit of second 64-bit block

        var plaintext = CtrEncrypt(k2, counter, encryptedData); // CTR mode is symmetric

        // Verify SIV by recomputing S2V
        var expectedSiv = S2V(k1, plaintext, associatedData ?? []);

        if (!ConstantTimeEquals(siv, expectedSiv))
        {
            throw new System.Security.Cryptography.CryptographicException("AES-SIV authentication failed");
        }

        return plaintext;
    }

    /// <summary>
    /// S2V: String-to-Vector function from RFC 5297.
    /// Generates the synthetic IV from associated data and plaintext.
    /// </summary>
    private byte[] S2V(byte[] key, byte[] plaintext, byte[][] associatedData)
    {
        var cmac = CreateCmac(key);

        // D = CMAC(K, <zero>)
        var d = ComputeCmac(cmac, Zero);

        // For each associated data string:
        // D = dbl(D) xor CMAC(K, Si)
        foreach (var ad in associatedData)
        {
            if (ad != null && ad.Length > 0)
            {
                d = Dbl(d);
                var adMac = ComputeCmac(cmac, ad);
                d = Xor(d, adMac);
            }
        }

        // Final processing with plaintext
        byte[] t;
        if (plaintext.Length >= BlockSize)
        {
            // T = Sn xorend D
            t = XorEnd(plaintext, d);
        }
        else
        {
            // T = dbl(D) xor pad(Sn)
            d = Dbl(d);
            var padded = Pad(plaintext);
            t = Xor(d, padded);
        }

        // V = CMAC(K, T)
        return ComputeCmac(cmac, t);
    }

    /// <summary>
    /// Creates a CMAC instance with the given key.
    /// </summary>
    private static CMac CreateCmac(byte[] key)
    {
        var cmac = new CMac(new AesEngine());
        cmac.Init(new KeyParameter(key));
        return cmac;
    }

    /// <summary>
    /// Computes CMAC of the input data.
    /// </summary>
    private static byte[] ComputeCmac(CMac cmac, byte[] data)
    {
        cmac.Reset();
        cmac.BlockUpdate(data, 0, data.Length);
        var result = new byte[cmac.GetMacSize()];
        cmac.DoFinal(result, 0);
        return result;
    }

    /// <summary>
    /// CTR mode encryption/decryption.
    /// </summary>
    private static byte[] CtrEncrypt(byte[] key, byte[] counter, byte[] data)
    {
        var cipher = new SicBlockCipher(new AesEngine());
        cipher.Init(true, new ParametersWithIV(new KeyParameter(key), counter));

        var output = new byte[data.Length];
        var blockCount = (data.Length + BlockSize - 1) / BlockSize;

        for (int i = 0; i < blockCount; i++)
        {
            var offset = i * BlockSize;
            var len = Math.Min(BlockSize, data.Length - offset);

            if (len == BlockSize)
            {
                cipher.ProcessBlock(data, offset, output, offset);
            }
            else
            {
                // Handle partial last block
                var block = new byte[BlockSize];
                Buffer.BlockCopy(data, offset, block, 0, len);
                var encBlock = new byte[BlockSize];
                cipher.ProcessBlock(block, 0, encBlock, 0);
                Buffer.BlockCopy(encBlock, 0, output, offset, len);
            }
        }

        return output;
    }

    /// <summary>
    /// dbl() function: Doubles a block in GF(2^128).
    /// </summary>
    private static byte[] Dbl(byte[] data)
    {
        var result = new byte[BlockSize];
        int carry = 0;

        for (int i = BlockSize - 1; i >= 0; i--)
        {
            int b = data[i] & 0xFF;
            result[i] = (byte)((b << 1) | carry);
            carry = (b >> 7) & 1;
        }

        // If carry, XOR with Rb
        if (carry != 0)
        {
            result = Xor(result, Rb);
        }

        return result;
    }

    /// <summary>
    /// XOR two equal-length byte arrays.
    /// </summary>
    private static byte[] Xor(byte[] a, byte[] b)
    {
        var result = new byte[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            result[i] = (byte)(a[i] ^ b[i]);
        }
        return result;
    }

    /// <summary>
    /// XOR the last 16 bytes of data with the block.
    /// </summary>
    private static byte[] XorEnd(byte[] data, byte[] block)
    {
        var result = (byte[])data.Clone();
        int offset = data.Length - BlockSize;
        for (int i = 0; i < BlockSize; i++)
        {
            result[offset + i] ^= block[i];
        }
        return result;
    }

    /// <summary>
    /// Pad data to block size with 0x80 followed by zeros.
    /// </summary>
    private static byte[] Pad(byte[] data)
    {
        var result = new byte[BlockSize];
        Buffer.BlockCopy(data, 0, result, 0, data.Length);
        result[data.Length] = 0x80;
        return result;
    }

    /// <summary>
    /// Constant-time comparison of two byte arrays.
    /// </summary>
    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        int result = 0;
        for (int i = 0; i < a.Length; i++)
        {
            result |= a[i] ^ b[i];
        }
        return result == 0;
    }
}
