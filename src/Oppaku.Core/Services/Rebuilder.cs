using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Diagnostics;
using Oppaku.Core.Models;
using Oppaku.Core.Helpers;
using Oppaku.Core.Exceptions;

namespace Oppaku.Core.Services;

public class RebuildProgress
{
    public List<int> Received { get; set; } = new();
    public int Total { get; set; }
    public long ContentSize { get; set; }
    public string ExpectedHash { get; set; } = "";
}

public class Rebuilder
{
    private const int BufferSize = 4 * 1024 * 1024; // 4 MB
    private const int ProgressIntervalMs = 80;

    public void InitialiseTarget(string destDir, ChunkMetadata metadata, string outputFileName)
    {
        if (!Directory.Exists(destDir))
            throw new OppakuException(ErrorCode.InvalidChunk, $"Destination directory not found: {destDir}");

        string targetFilePath = Path.Combine(destDir, outputFileName);

        if (!File.Exists(targetFilePath))
        {
            // Sparse file = content + metadata reserve zone
            SparseFileHelper.CreateSparseFile(targetFilePath, metadata.TotalFileSize);
        }

        // Write initial embedded progress if not already present
        var existing = SparseFileHelper.ReadEmbeddedProgress(targetFilePath, metadata.TotalFileSize);
        if (existing == null)
        {
            var initial = new RebuildProgress
            {
                Total = metadata.TotalChunks,
                ContentSize = metadata.TotalFileSize,
                ExpectedHash = metadata.SourceFileHash
            };
            SparseFileHelper.WriteEmbeddedProgress(targetFilePath, initial);
        }
    }

    public string InsertChunk(string chunkBinPath, string targetLocation, IProgress<long>? progress = null)
    {
        if (!File.Exists(chunkBinPath))
            throw new OppakuException(ErrorCode.InvalidChunk, $"Chunk file not found: {chunkBinPath}");

        ChunkMetadata metadata;
        long payloadStartOffset;
        
        using (var sourceStream = new FileStream(chunkBinPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new BinaryReader(sourceStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            metadata = ChunkMetadata.ReadFrom(reader);
            payloadStartOffset = sourceStream.Position;
        }

        string targetFilePath;
        if (Directory.Exists(targetLocation))
        {
            targetFilePath = Path.Combine(targetLocation, metadata.FileName);
        }
        else
        {
            targetFilePath = targetLocation;
        }

        if (!File.Exists(targetFilePath))
        {
            InitialiseTarget(Path.GetDirectoryName(targetFilePath) ?? "", metadata, Path.GetFileName(targetFilePath));
        }
        else
        {
            // Ensure progress zone exists for files created by older versions
            var existing = SparseFileHelper.ReadEmbeddedProgress(targetFilePath, metadata.TotalFileSize);
            if (existing == null)
            {
                var initial = new RebuildProgress { Total = metadata.TotalChunks, ContentSize = metadata.TotalFileSize, ExpectedHash = metadata.SourceFileHash };
                SparseFileHelper.WriteEmbeddedProgress(targetFilePath, initial);
            }
        }

        // Write chunk payload to correct offset
        using var sourcePayloadStream = new FileStream(chunkBinPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        sourcePayloadStream.Position = payloadStartOffset;

        using var destStream = new FileStream(targetFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, BufferSize);
        destStream.Position = metadata.ByteOffset;

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        byte[] buffer = new byte[BufferSize];
        long bytesRemaining = metadata.ActualChunkSize;
        long bytesWritten = 0;
        var sw = Stopwatch.StartNew();

        while (bytesRemaining > 0)
        {
            int bytesToRead = (int)Math.Min(buffer.Length, bytesRemaining);
            int bytesRead = sourcePayloadStream.Read(buffer, 0, bytesToRead);
            if (bytesRead == 0) break;

            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            destStream.Write(buffer, 0, bytesRead);
            bytesRemaining -= bytesRead;
            bytesWritten += bytesRead;

            if (progress != null && sw.ElapsedMilliseconds >= ProgressIntervalMs)
            {
                progress.Report(bytesWritten);
                sw.Restart();
            }
        }
        progress?.Report(bytesWritten); // final

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        string computedChecksum = $"sha256:{Convert.ToHexString(sha256.Hash!).ToLowerInvariant()}";

        if (computedChecksum != metadata.ChunkChecksum)
            throw new OppakuException(ErrorCode.ChecksumMismatch, $"Chunk checksum mismatch for part {metadata.ChunkIndex}");

        // Update embedded progress (read → mutate → write back)
        var rebuildState = SparseFileHelper.ReadEmbeddedProgress(targetFilePath, metadata.TotalFileSize)
            ?? new RebuildProgress { Total = metadata.TotalChunks, ContentSize = metadata.TotalFileSize, ExpectedHash = metadata.SourceFileHash };

        if (!rebuildState.Received.Contains(metadata.ChunkIndex))
        {
            rebuildState.Received.Add(metadata.ChunkIndex);
            rebuildState.Received.Sort();
            SparseFileHelper.WriteEmbeddedProgress(targetFilePath, rebuildState);
        }

        return targetFilePath;
    }

    public void Finalise(string targetFilePath, string sourceFileHash, IProgress<long>? progress = null)
    {
        if (!File.Exists(targetFilePath))
            throw new OppakuException(ErrorCode.InvalidChunk, "Target file does not exist");

        // We need the content size to know the metadata zone offset.
        // Try reading it from embedded progress first.
        // If the file is at exact content size (old format), fall back to file length.
        long contentSize;
        RebuildProgress? rebuildState = null;

        // Probe: if file is larger than expected, metadata zone is at file.Length - MetadataReserve
        // We try to read from file.Length - MetadataReserve
        var fi = new FileInfo(targetFilePath);
        long probeContentSize = fi.Length - SparseFileHelper.MetadataReserve;
        if (probeContentSize > 0)
        {
            rebuildState = SparseFileHelper.ReadEmbeddedProgress(targetFilePath, probeContentSize);
        }

        if (rebuildState != null)
        {
            contentSize = rebuildState.ContentSize;
        }
        else
        {
            // Fallback: file has no embedded metadata (shouldn't happen with new format)
            contentSize = fi.Length;
        }

        if (rebuildState != null && rebuildState.Received.Count < rebuildState.Total)
        {
            var missing = Enumerable.Range(0, rebuildState.Total).Except(rebuildState.Received).ToList();
            throw new OppakuException(ErrorCode.InvalidChunk,
                $"Cannot finalise: missing parts {string.Join(", ", missing)}");
        }

        // Hash only the content portion [0, contentSize)
        string rebuiltHash = ChecksumHelper.ComputeFileHash(targetFilePath, contentSize, progress);
        if (rebuiltHash != sourceFileHash)
            throw new OppakuException(ErrorCode.ChecksumMismatch, "Final rebuilt file hash does not match original source hash");

        // Strip metadata zone — truncate file to exact content size
        using (var fs = new FileStream(targetFilePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(contentSize);
        }
    }

    /// <summary>Returns current rebuild state from the file's embedded metadata, or null if none found.</summary>
    public RebuildProgress? GetProgress(string targetFilePath)
    {
        if (!File.Exists(targetFilePath)) return null;
        var fi = new FileInfo(targetFilePath);
        long probeContentSize = fi.Length - SparseFileHelper.MetadataReserve;
        if (probeContentSize <= 0) return null;
        return SparseFileHelper.ReadEmbeddedProgress(targetFilePath, probeContentSize);
    }
}
