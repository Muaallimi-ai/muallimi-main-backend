using System.Security.Cryptography;
using System.Text;

namespace Muallimi.Api.Security.DataEncryption;

/// <summary>
/// T028 — Column-level encryption adapter. Default implementation uses
/// AES-256-GCM with a local key file so development can run without a managed
/// KMS. Production bindings swap in the real KMS/HSM adapter.
/// </summary>
public interface IDataEncryptionAdapter
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
}

public class LocalAesGcmEncryptionAdapter : IDataEncryptionAdapter
{
    private readonly byte[] _key;

    public LocalAesGcmEncryptionAdapter(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("AES-256 requires a 32-byte key", nameof(key));
        _key = key;
    }

    public static LocalAesGcmEncryptionAdapter FromConfiguration(IConfiguration config)
    {
        var keyBase64 = config["Phase6:Encryption:LocalKeyBase64"];
        byte[] key;
        if (!string.IsNullOrEmpty(keyBase64))
        {
            key = Convert.FromBase64String(keyBase64);
        }
        else
        {
            // Deterministic dev key — MUST be overridden in non-local envs.
            key = SHA256.HashData(Encoding.UTF8.GetBytes("muallimi-local-dev-key"));
        }
        return new LocalAesGcmEncryptionAdapter(key);
    }

    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var output = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, output, nonce.Length + tag.Length, ciphertext.Length);
        return Convert.ToBase64String(output);
    }

    public string Decrypt(string ciphertextB64)
    {
        var input = Convert.FromBase64String(ciphertextB64);
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;

        var nonce = new byte[nonceSize];
        var tag = new byte[tagSize];
        var ciphertext = new byte[input.Length - nonceSize - tagSize];

        Buffer.BlockCopy(input, 0, nonce, 0, nonceSize);
        Buffer.BlockCopy(input, nonceSize, tag, 0, tagSize);
        Buffer.BlockCopy(input, nonceSize + tagSize, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, tagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
