using System;
using System.IO;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Threading;
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

    public string ComputeSourceFileHash(string filePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Source file not found: {filePath}");

        if (_cachedSourcePath == filePath && !string.IsNullOrEmpty(_cachedSourceHash))
        {
            return _cachedSourceHash;
        }

        _cachedSourcePath = filePath;
        _cachedSourceHash = ChecksumHelper.ComputeFileHash(filePath, progress, cancellationToken);
        return _cachedSourceHash;
    }

    public string ComputeSourceStreamHash(Stream sourceStream, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (sourceStream == null || !sourceStream.CanRead)
            throw new OppakuException(ErrorCode.InvalidChunk, "Source stream is invalid or unreadable");

        if (sourceStream is FileStream fs)
        {
            if (_cachedSourcePath == fs.Name && !string.IsNullOrEmpty(_cachedSourceHash))
            {
                return _cachedSourceHash;
            }
            _cachedSourcePath = fs.Name;
        }

        sourceStream.Position = 0;
        _cachedSourceHash = ChecksumHelper.ComputeStreamHash(sourceStream, progress, cancellationToken);
        
        return _cachedSourceHash;
    }

    public void ExtractChunk(string sourcePath, int chunkIndex, long chunkSize, string outputDir, string sourceFileHash, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ExtractChunk(fs, Path.GetFileName(sourcePath), chunkIndex, chunkSize, outputDir, sourceFileHash, progress, cancellationToken);
    }

    public void ExtractChunk(Stream sourceStream, string fileName, int chunkIndex, long chunkSize, string outputDir, string sourceFileHash, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (sourceStream == null || !sourceStream.CanRead)
            throw new OppakuException(ErrorCode.InvalidChunk, "Source stream is invalid or unreadable");
        if (chunkIndex < 0)
            throw new OppakuException(ErrorCode.InvalidChunk, "Chunk index cannot be negative");
        if (chunkSize <= 0)
            throw new OppakuException(ErrorCode.InvalidChunk, "Chunk size must be greater than zero");
        if (!Directory.Exists(outputDir))
            throw new OppakuException(ErrorCode.InvalidChunk, $"Output directory not found: {outputDir}");

        long byteOffset = chunkIndex * chunkSize;
        
        if (byteOffset >= sourceStream.Length)
            throw new OppakuException(ErrorCode.InvalidChunk, "Chunk offset is past the end of the file");

        long actualChunkSize = Math.Min(chunkSize, sourceStream.Length - byteOffset);
        
        string chunkFileName = $"{fileName}.part{chunkIndex}.oppk";
        string chunkPath = Path.Combine(outputDir, chunkFileName);

        sourceStream.Position = byteOffset;
        
        bool extractionSucceeded = false;

        try
        {
            using (var outStream = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            using (var writer = new BinaryWriter(outStream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                var dummyChecksum = "sha256:" + new string('0', 64);
                var metadata = new ChunkMetadata
                {
                    FileName = fileName,
                    TotalFileSize = sourceStream.Length,
                    ChunkSize = chunkSize,
                    TotalChunks = (int)Math.Ceiling((double)sourceStream.Length / chunkSize),
                    ChunkIndex = chunkIndex,
                    ByteOffset = byteOffset,
                    ActualChunkSize = actualChunkSize,
                    SourceFileHash = sourceFileHash,
                    ChunkChecksum = dummyChecksum,
                    CreatedAt = DateTimeOffset.UtcNow,
                    OppakuVersion = "2.0.0"
                };

                metadata.WriteTo(writer);

                using (var sha256 = SHA256.Create())
                using (var cryptoStream = new CryptoStream(outStream, sha256, CryptoStreamMode.Write, leaveOpen: true))
                {
                    byte[] buffer = new byte[BufferSize];
                    long bytesRemaining = actualChunkSize;
                    long bytesWritten = 0;
                    var sw = Stopwatch.StartNew();

                    while (bytesRemaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int bytesToRead = (int)Math.Min(buffer.Length, bytesRemaining);
                        int bytesRead = sourceStream.Read(buffer, 0, bytesToRead);
                        
                        if (bytesRead == 0) break;

                        cryptoStream.Write(buffer, 0, bytesRead);
                        bytesRemaining -= bytesRead;
                        bytesWritten += bytesRead;

                        if (progress != null && (sw.ElapsedMilliseconds >= ProgressIntervalMs || bytesWritten == bytesRead))
                        {
                            progress.Report(bytesWritten);
                            sw.Restart();
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (bytesRemaining > 0)
                        throw new OppakuException(ErrorCode.InvalidChunk, "Failed to read the expected number of bytes");

                    progress?.Report(bytesWritten); // final
                    cancellationToken.ThrowIfCancellationRequested();
                    cryptoStream.FlushFinalBlock();

                    string actualChecksum = $"sha256:{Convert.ToHexString(sha256.Hash!).ToLowerInvariant()}";
                    
                    outStream.Position = 0;
                    var finalMetadata = metadata with { ChunkChecksum = actualChecksum };
                    finalMetadata.WriteTo(writer);
                }
            }

            extractionSucceeded = true;
        }
        finally
        {
            if (!extractionSucceeded && File.Exists(chunkPath))
            {
                try { File.Delete(chunkPath); } catch { /* Ignore cleanup errors */ }
            }
        }
    }
}
