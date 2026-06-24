using System.Security.Cryptography;

namespace PmpShare.Plugin.Services;

public sealed class TransferCrypto
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 210_000;
    private static readonly byte[] Magic = "PMPSHARE1"u8.ToArray();
    private static readonly byte[] ChunkedMagic = "PMPSHARE2"u8.ToArray();

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

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(passphrase, salt);
        var plaintextBuffer = new byte[ChunkSize];
        var ciphertextBuffer = new byte[ChunkSize];
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var lengthBuffer = new byte[sizeof(int)];
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            await using var input = File.OpenRead(plaintextPath);
            await using var output = File.Create(encryptedOutputPath);
            using var aes = new AesGcm(key, TagSize);
            await output.WriteAsync(ChunkedMagic, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(salt, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var bytesRead = await input.ReadAsync(plaintextBuffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                RandomNumberGenerator.Fill(nonce);
                var plaintext = plaintextBuffer.AsSpan(0, bytesRead);
                var ciphertext = ciphertextBuffer.AsSpan(0, bytesRead);
                aes.Encrypt(nonce, plaintext, ciphertext, tag);
                sha256.AppendData(plaintext);

                WriteInt32BigEndian(lengthBuffer, bytesRead);
                await output.WriteAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(ciphertextBuffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintextBuffer);
            CryptographicOperations.ZeroMemory(ciphertextBuffer);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
        }

        var plaintextSha256 = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
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

        await using var input = File.OpenRead(encryptedPath);
        var magic = new byte[ChunkedMagic.Length];
        await ReadExactlyAsync(input, magic, cancellationToken).ConfigureAwait(false);
        if (magic.AsSpan().SequenceEqual(ChunkedMagic))
        {
            return await DecryptChunkedFileAsync(input, plaintextOutputPath, passphrase, cancellationToken).ConfigureAwait(false);
        }

        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidOperationException("Encrypted file is not a PmpShare encrypted payload.");
        }

        return await DecryptLegacyFileAsync(encryptedPath, plaintextOutputPath, passphrase, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> DecryptChunkedFileAsync(
        Stream input,
        string plaintextOutputPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var salt = new byte[SaltSize];
        await ReadExactlyAsync(input, salt, cancellationToken).ConfigureAwait(false);
        var key = DeriveKey(passphrase, salt);
        var lengthBuffer = new byte[sizeof(int)];
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[ChunkSize];
        var plaintext = new byte[ChunkSize];
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            await using var output = File.Create(plaintextOutputPath);
            using var aes = new AesGcm(key, TagSize);
            while (true)
            {
                var bytesRead = await ReadAtMostAsync(input, lengthBuffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }
                if (bytesRead != lengthBuffer.Length)
                {
                    throw new InvalidOperationException("Encrypted file ended in the middle of a chunk header.");
                }

                var chunkLength = ReadInt32BigEndian(lengthBuffer);
                if (chunkLength <= 0 || chunkLength > ChunkSize)
                {
                    throw new InvalidOperationException("Encrypted file has an invalid chunk length.");
                }

                await ReadExactlyAsync(input, nonce, cancellationToken).ConfigureAwait(false);
                await ReadExactlyAsync(input, tag, cancellationToken).ConfigureAwait(false);
                await ReadExactlyAsync(input, ciphertext.AsMemory(0, chunkLength), cancellationToken).ConfigureAwait(false);

                try
                {
                    aes.Decrypt(nonce, ciphertext.AsSpan(0, chunkLength), tag, plaintext.AsSpan(0, chunkLength));
                }
                catch (CryptographicException ex)
                {
                    throw new InvalidOperationException("Decrypt failed. Check the passphrase and transfer data.", ex);
                }

                sha256.AppendData(plaintext.AsSpan(0, chunkLength));
                await output.WriteAsync(plaintext.AsMemory(0, chunkLength), cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, chunkLength));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(plaintext);
        }

        return Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> DecryptLegacyFileAsync(
        string encryptedPath,
        string plaintextOutputPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
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

    private static void WriteInt32BigEndian(byte[] destination, int value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static int ReadInt32BigEndian(byte[] source) =>
        source[0] << 24 | source[1] << 16 | source[2] << 8 | source[3];

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new InvalidOperationException("Encrypted file ended unexpectedly.");
            }
            offset += bytesRead;
        }
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }
            offset += bytesRead;
        }
        return offset;
    }
}

public sealed record EncryptionResult(string PlaintextSha256, long EncryptedSize);
