using PmpShare.Plugin.Services;

var workDir = Path.Combine(Path.GetTempPath(), $"pmpshare-plugin-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(workDir);

try
{
    var sourcePath = Path.Combine(workDir, "source.pmp");
    var encryptedPath = Path.Combine(workDir, "source.pmp.enc");
    var decryptedPath = Path.Combine(workDir, "decrypted.pmp");
    var passphrase = "test-passphrase";
    await File.WriteAllTextAsync(sourcePath, "fake pmp content for crypto roundtrip");

    var crypto = new TransferCrypto();
    var encryption = await crypto.EncryptFileAsync(sourcePath, encryptedPath, passphrase, CancellationToken.None);
    var decryptedSha256 = await crypto.DecryptFileAsync(encryptedPath, decryptedPath, passphrase, CancellationToken.None);

    if (!string.Equals(encryption.PlaintextSha256, decryptedSha256, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Roundtrip SHA-256 mismatch.");
    }

    if (!File.ReadAllBytes(sourcePath).SequenceEqual(File.ReadAllBytes(decryptedPath)))
    {
        throw new InvalidOperationException("Roundtrip plaintext mismatch.");
    }

    var senderIdValue = IdentityService.CreatePmpShareId();
    var senderKeys = IdentityService.CreateX25519KeyPair();
    var recipientKeys = IdentityService.CreateX25519KeyPair();

    if (!IdentityService.TryParseCombinedIdentity(IdentityService.CombinedIdentity(senderIdValue, senderKeys.PublicKeyBase64), out var senderId, out var senderPublicKey) ||
        senderId != senderIdValue ||
        senderPublicKey != senderKeys.PublicKeyBase64)
    {
        throw new InvalidOperationException("Combined identity parsing failed.");
    }

    var wrapped = IdentityService.WrapPassphrase(
        senderKeys.PrivateKeyBase64,
        recipientKeys.PublicKeyBase64,
        passphrase);
    var unwrapped = IdentityService.UnwrapPassphrase(
        recipientKeys.PrivateKeyBase64,
        senderKeys.PublicKeyBase64,
        wrapped.CiphertextBase64,
        wrapped.NonceBase64);
    if (unwrapped != passphrase)
    {
        throw new InvalidOperationException("X25519 passphrase wrap roundtrip failed.");
    }

    try
    {
        await crypto.DecryptFileAsync(encryptedPath, Path.Combine(workDir, "bad.pmp"), "wrong-passphrase", CancellationToken.None);
        throw new InvalidOperationException("Decrypt with wrong passphrase unexpectedly succeeded.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Decrypt failed", StringComparison.OrdinalIgnoreCase))
    {
    }

    Console.WriteLine("PmpShare.Plugin.Tests passed.");
}
finally
{
    Directory.Delete(workDir, recursive: true);
}
