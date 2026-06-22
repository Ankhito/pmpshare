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
