using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace PmpShare.Plugin.Services;

public static class IdentityService
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private const int X25519KeySize = 32;
    private const int WrapKeySize = 32;
    private const int WrapNonceSize = 12;
    private const int WrapTagSize = 16;
    private static readonly byte[] WrapInfo = "PmpShare passphrase wrap v1"u8.ToArray();

    public static bool EnsureIdentity(Configuration configuration)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(configuration.PmpShareId))
        {
            configuration.PmpShareId = CreatePmpShareId();
            changed = true;
        }

        if (HasValidKeyPair(configuration.X25519PublicKeyBase64, configuration.X25519PrivateKeyBase64))
        {
            return changed;
        }

        var (publicKey, privateKey) = CreateX25519KeyPair();
        configuration.X25519PublicKeyBase64 = publicKey;
        configuration.X25519PrivateKeyBase64 = privateKey;
        return true;
    }

    public static string CreatePmpShareId()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return "ps_" + EncodeBase58(bytes);
    }

    public static string CombinedIdentity(Configuration configuration) =>
        CombinedIdentity(configuration.PmpShareId, configuration.X25519PublicKeyBase64);

    public static string CombinedIdentity(string pmpShareId, string publicKeyBase64) =>
        $"{pmpShareId}.{publicKeyBase64}";

    public static bool TryParseCombinedIdentity(string value, out string pmpShareId, out string publicKeyBase64)
    {
        pmpShareId = string.Empty;
        publicKeyBase64 = string.Empty;
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf('.');
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            return false;
        }

        var id = trimmed[..separator].Trim();
        var publicKey = trimmed[(separator + 1)..].Trim();
        if (!id.StartsWith("ps_", StringComparison.Ordinal) || !IsValidPublicKey(publicKey))
        {
            return false;
        }

        pmpShareId = id;
        publicKeyBase64 = publicKey;
        return true;
    }

    public static bool IsValidPublicKey(string publicKeyBase64) =>
        TryDecodeFixed(publicKeyBase64, X25519KeySize, out _);

    public static WrappedPassphrase WrapPassphrase(
        string senderPrivateKeyBase64,
        string recipientPublicKeyBase64,
        string passphrase)
    {
        var key = DeriveWrapKey(senderPrivateKeyBase64, recipientPublicKeyBase64);
        var nonce = RandomNumberGenerator.GetBytes(WrapNonceSize);
        var plaintext = Encoding.UTF8.GetBytes(passphrase);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[WrapTagSize];

        try
        {
            using var aes = new AesGcm(key, WrapTagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
            return new WrappedPassphrase(
                Convert.ToBase64String(ciphertext.Concat(tag).ToArray()),
                Convert.ToBase64String(nonce));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static string UnwrapPassphrase(
        string recipientPrivateKeyBase64,
        string senderPublicKeyBase64,
        string encryptedPassphraseBase64,
        string nonceBase64)
    {
        var payload = Convert.FromBase64String(encryptedPassphraseBase64);
        if (payload.Length < WrapTagSize)
        {
            throw new InvalidOperationException("Encrypted passphrase payload is invalid.");
        }

        var nonce = Convert.FromBase64String(nonceBase64);
        if (nonce.Length != WrapNonceSize)
        {
            throw new InvalidOperationException("Encrypted passphrase nonce is invalid.");
        }

        var ciphertext = payload.AsSpan(0, payload.Length - WrapTagSize).ToArray();
        var tag = payload.AsSpan(payload.Length - WrapTagSize, WrapTagSize).ToArray();
        var plaintext = new byte[ciphertext.Length];
        var key = DeriveWrapKey(recipientPrivateKeyBase64, senderPublicKeyBase64);

        try
        {
            using var aes = new AesGcm(key, WrapTagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Could not unwrap the transfer passphrase.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static (string PublicKeyBase64, string PrivateKeyBase64) CreateX25519KeyPair()
    {
        var algorithm = KeyAgreementAlgorithm.X25519;
        using var key = new Key(algorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (
            Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)),
            Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)));
    }

    private static byte[] DeriveWrapKey(string privateKeyBase64, string publicKeyBase64)
    {
        if (!TryDecodeFixed(privateKeyBase64, X25519KeySize, out var privateKey) ||
            !TryDecodeFixed(publicKeyBase64, X25519KeySize, out var publicKey))
        {
            throw new InvalidOperationException("PmpShare identity keys are invalid.");
        }

        var algorithm = KeyAgreementAlgorithm.X25519;
        using var localKey = Key.Import(algorithm, privateKey, KeyBlobFormat.RawPrivateKey);
        var remotePublicKey = PublicKey.Import(algorithm, publicKey, KeyBlobFormat.RawPublicKey);
        using var sharedSecret = algorithm.Agree(localKey, remotePublicKey)
            ?? throw new InvalidOperationException("Could not derive a shared identity secret.");
        return KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(sharedSecret, ReadOnlySpan<byte>.Empty, WrapInfo, WrapKeySize);
    }

    private static bool HasValidKeyPair(string publicKeyBase64, string privateKeyBase64) =>
        TryDecodeFixed(publicKeyBase64, X25519KeySize, out _) &&
        TryDecodeFixed(privateKeyBase64, X25519KeySize, out _);

    private static bool TryDecodeFixed(string value, int expectedLength, out byte[] bytes)
    {
        bytes = [];
        try
        {
            bytes = Convert.FromBase64String(value.Trim());
            return bytes.Length == expectedLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string EncodeBase58(ReadOnlySpan<byte> bytes)
    {
        var value = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new Stack<char>();
        while (value > 0)
        {
            value = System.Numerics.BigInteger.DivRem(value, 58, out var remainder);
            chars.Push(Alphabet[(int)remainder]);
        }

        return chars.Count == 0 ? "1" : new string(chars.ToArray());
    }
}

public sealed record WrappedPassphrase(string CiphertextBase64, string NonceBase64);
