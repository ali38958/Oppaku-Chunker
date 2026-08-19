using System.IO;
using System.Security.Cryptography;
using System.Diagnostics;
using Oppaku.Core.Helpers;
using Oppaku.Core.Exceptions;
using Oppaku.Core.Models;

namespace Oppaku.Core.Services;

public class Extractor
{
    private const int BufferSize = 4 * 1024 * 1024; // 4 MB
    private const int ProgressIntervalMs = 80;

    private string? _cachedSourceHash;
    private string? _cachedSourcePath;

    public string ComputeSourceFileHash(string sourcePath, IProgress<long>? progress = null)
    {
        if (!File.Exists(sourcePath))
            throw new OppakuException(ErrorCode.InvalidChunk, $"Source file not found: {sourcePath}");

        if (_cachedSourcePath == sourcePath && !string.IsNullOrEmpty(_cachedSourceHash))
        {
            return _cachedSourceHash;
        }

        _cachedSourceHash = ChecksumHelper.ComputeFileHash(sourcePath, progress);
        _cachedSourcePath = sourcePath;
        
        return _cachedSourceHash;
    }

    public void ExtractChunk(string sourcePath, int chunkIndex, long chunkSize, string outputDir, string sourceFileHash, IProgress<long>? progress = null)
    {
        if (!File.Exists(sourcePath))
            throw new OppakuException(ErrorCode.InvalidChunk, $"Source file not found: {sourcePath}");
        if (chunkIndex < 0)
            throw new OppakuException(ErrorCode.InvalidChunk, "Chunk index cannot be negative");
        if (chunkSize <= 0)
            throw new OppakuException(ErrorCode.InvalidChunk, "Chunk size must be greater than zero");
        if (!Directory.Exists(outputDir))
            throw new OppakuException(ErrorCode.InvalidChunk, $"Output directory not found: {outputDir}");

        var fileInfo = new FileInfo(sourcePath);
        long byteOffset = chunkIndex * chunkSize;
        
        if (byteOffset >= fileInfo.Length)
            throw new OppakuException(ErrorCode.InvalidChunk, "Chunk offset is past the end of the file");

        long actualChunkSize = Math.Min(chunkSize, fileInfo.Length - byteOffset);
        
        string chunkFileName = $"{fileInfo.Name}.part{chunkIndex}.oppk";
        string chunkPath = Path.Combine(outputDir, chunkFileName);

        using var handle = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        using var outStream = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
        using var writer = new BinaryWriter(outStream, System.Text.Encoding.UTF8, leaveOpen: true);

        var dummyChecksum = "sha256:" + new string('0', 64);
        var metadata = new ChunkMetadata
        {
            FileName = fileInfo.Name,
            TotalFileSize = fileInfo.Length,
            ChunkSize = chunkSize,
            TotalChunks = (int)Math.Ceiling((double)fileInfo.Length / chunkSize),
            ChunkIndex = chunkIndex,
            ByteOffset = byteOffset,
            ActualChunkSize = actualChunkSize,
            SourceFileHash = sourceFileHash,
            ChunkChecksum = dummyChecksum,
            CreatedAt = DateTimeOffset.UtcNow,
            OppakuVersion = "2.0.0"
        };

        metadata.WriteTo(writer);

        using var sha256 = SHA256.Create();
        using var cryptoStream = new CryptoStream(outStream, sha256, CryptoStreamMode.Write, leaveOpen: true);

        byte[] buffer = new byte[BufferSize];
        long bytesRemaining = actualChunkSize;
        long currentOffset = byteOffset;
        long bytesWritten = 0;
        var sw = Stopwatch.StartNew();

        while (bytesRemaining > 0)
        {
            int bytesToRead = (int)Math.Min(buffer.Length, bytesRemaining);
            int bytesRead = RandomAccess.Read(handle, buffer.AsSpan(0, bytesToRead), currentOffset);
            
            if (bytesRead == 0) break;

            cryptoStream.Write(buffer, 0, bytesRead);
            currentOffset += bytesRead;
            bytesRemaining -= bytesRead;
            bytesWritten += bytesRead;

            if (progress != null && sw.ElapsedMilliseconds >= ProgressIntervalMs)
            {
                progress.Report(bytesWritten);
                sw.Restart();
            }
        }

        if (bytesRemaining > 0)
            throw new OppakuException(ErrorCode.InvalidChunk, "Failed to read the expected number of bytes");

        cryptoStream.FlushFinalBlock();
        progress?.Report(bytesWritten); // final

        string actualChecksum = $"sha256:{Convert.ToHexString(sha256.Hash!).ToLowerInvariant()}";
        
        outStream.Position = 0;
        var finalMetadata = metadata with { ChunkChecksum = actualChecksum };
        finalMetadata.WriteTo(writer);
    }
}
