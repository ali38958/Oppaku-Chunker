using System.Security.Cryptography;
using System.IO;
using System.Diagnostics;

namespace Oppaku.Core.Helpers;

public static class ChecksumHelper
{
    private const int BufferSize = 4 * 1024 * 1024; // 4 MB — fast sequential IO
    private const int ProgressIntervalMs = 80;       // max one UI update per 80ms

    public static string ComputeFileHash(string path, IProgress<long>? progress = null)
        => ComputeFileHash(path, long.MaxValue, progress);

    public static string ComputeFileHash(string path, long maxBytes, IProgress<long>? progress = null)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);

        byte[] buffer = new byte[BufferSize];
        long totalRead = 0;
        int bytesRead;
        var sw = Stopwatch.StartNew();

        while (totalRead < maxBytes &&
               (bytesRead = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, maxBytes - totalRead))) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            totalRead += bytesRead;

            if (progress != null && sw.ElapsedMilliseconds >= ProgressIntervalMs)
            {
                progress.Report(totalRead);
                sw.Restart();
            }
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        progress?.Report(totalRead); // always emit final value
        return FormatHash(sha256.Hash!);
    }

    public static string ComputeStreamHash(Stream stream, IProgress<long>? progress = null)
    {
        using var sha256 = SHA256.Create();
        byte[] buffer = new byte[BufferSize];
        long totalRead = 0;
        int bytesRead;
        var sw = Stopwatch.StartNew();

        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            totalRead += bytesRead;

            if (progress != null && sw.ElapsedMilliseconds >= ProgressIntervalMs)
            {
                progress.Report(totalRead);
                sw.Restart();
            }
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        progress?.Report(totalRead);
        return FormatHash(sha256.Hash!);
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
