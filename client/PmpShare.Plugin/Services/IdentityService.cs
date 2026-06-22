using System.Security.Cryptography;

namespace PmpShare.Plugin.Services;

public static class IdentityService
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string CreatePmpShareId()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return "ps_" + EncodeBase58(bytes);
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
