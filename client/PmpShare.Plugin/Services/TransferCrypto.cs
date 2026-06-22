using System.Security.Cryptography;

namespace PmpShare.Plugin.Services;

public sealed class TransferCrypto
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 210_000;
    private static readonly byte[] Magic = "PMPSHARE1"u8.ToArray();

    public async Task<EncryptionResult> EncryptFileAsync(
        string plaintextPath,
        string encryptedOutputPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            throw new InvalidOperationException("Passphrase is required.");
        }

        var plaintext = await File.ReadAllBytesAsync(plaintextPath, cancellationToken).ConfigureAwait(false);
        var plaintextSha256 = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(passphrase, salt);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        await using var output = File.Create(encryptedOutputPath);
        await output.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(salt, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);

        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintext);

        return new EncryptionResult(plaintextSha256, new FileInfo(encryptedOutputPath).Length);
    }

    public async Task<string> DecryptFileAsync(
        string encryptedPath,
        string plaintextOutputPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            throw new InvalidOperationException("Passphrase is required.");
        }

        var payload = await File.ReadAllBytesAsync(encryptedPath, cancellationToken).ConfigureAwait(false);
        var minimumSize = Magic.Length + SaltSize + NonceSize + TagSize;
        if (payload.Length < minimumSize || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidOperationException("Encrypted file is not a PmpShare encrypted payload.");
        }

        var offset = Magic.Length;
        var salt = payload.AsSpan(offset, SaltSize).ToArray();
        offset += SaltSize;
        var nonce = payload.AsSpan(offset, NonceSize).ToArray();
        offset += NonceSize;
        var tag = payload.AsSpan(offset, TagSize).ToArray();
        offset += TagSize;
        var ciphertext = payload.AsSpan(offset).ToArray();
        var plaintext = new byte[ciphertext.Length];
        var key = DeriveKey(passphrase, salt);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Decrypt failed. Check the passphrase and transfer data.", ex);
        }

        await File.WriteAllBytesAsync(plaintextOutputPath, plaintext, cancellationToken).ConfigureAwait(false);
        var plaintextSha256 = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();

        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(payload);

        return plaintextSha256;
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
    }
}

public sealed record EncryptionResult(string PlaintextSha256, long EncryptedSize);
