using System;
using System.Security.Cryptography;
using System.Text;

namespace Muallimi.Infrastructure.Identity.Cryptography;

/// <summary>
/// T028 — AES-256-GCM encryptor for TOTP secrets and recovery codes.
/// The key is supplied from the <c>IDENTITY_TOTP_ENCRYPTION_KEY</c> env
/// var (base64-encoded 32-byte key). Ciphertext layout is
/// <c>[12-byte nonce][16-byte tag][ciphertext]</c> encoded as base64
/// so the value fits the existing <c>TwoFactorSecret.Secret</c> /
/// <c>RecoveryCodes</c> <c>varchar</c> columns.
/// </summary>
public interface IAesEncryptor
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
}

public sealed class AesEncryptor : IAesEncryptor
{
    private const int NonceSize = 12; // AES-GCM standard
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesEncryptor(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("AES-256-GCM key must be 32 bytes.", nameof(key));
        }
        _key = key;
    }

    public static AesEncryptor FromBase64Key(string base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            throw new ArgumentException("Key cannot be empty.", nameof(base64Key));
        }
        var bytes = Convert.FromBase64String(base64Key);
        return new AesEncryptor(bytes);
    }

    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var packed = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, packed, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, packed, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(packed);
    }

    public string Decrypt(string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        var packed = Convert.FromBase64String(ciphertext);
        if (packed.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext is shorter than nonce + tag.");
        }
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipher = new byte[packed.Length - NonceSize - TagSize];
        Buffer.BlockCopy(packed, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(packed, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(packed, NonceSize + TagSize, cipher, 0, cipher.Length);

        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
