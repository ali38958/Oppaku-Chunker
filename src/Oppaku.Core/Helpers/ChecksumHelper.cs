using System.Security.Cryptography;
using System.IO;

namespace Oppaku.Core.Helpers;

public static class ChecksumHelper
{
    private const int BufferSize = 64 * 1024; // 64 KB

    public static string ComputeFileHash(string path)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        var hashBytes = sha256.ComputeHash(stream);
        return FormatHash(hashBytes);
    }

    public static string ComputeSpanChecksum(ReadOnlySpan<byte> data)
    {
        var hashBytes = SHA256.HashData(data);
        return FormatHash(hashBytes);
    }

    private static string FormatHash(byte[] hashBytes)
    {
        var hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return $"sha256:{hex}";
    }
}
