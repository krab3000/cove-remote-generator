using System.Security.Cryptography;
using System.Text;

namespace RemoteHeavylifter.Protocol;

/// <summary>The worker token is the only credential: it authenticates the WebSocket in either direction and the worker's HTTP calls.</summary>
public static class WorkerTokens
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public const int IdLength = 12;
    public const int MinLength = 16;

    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Lowercase hex SHA-256 of the token; what Cove stores when it does not need the token itself.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    /// <summary>Short public identifier shown in the UI and logged by the worker, derived from the hash.</summary>
    public static string Id(string token) => IdFromHash(Hash(token));

    public static string IdFromHash(string hash) => Base32(Convert.FromHexString(hash))[..IdLength];

    /// <summary>Constant-time comparison of two hex hashes.</summary>
    public static bool HashEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    public static bool Matches(string token, string expectedHash) => HashEquals(Hash(token), expectedHash);

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base32(byte[] bytes)
    {
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            output.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }
}
