using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using Oppaku.Core.Models;
using Oppaku.Core.Helpers;
using Oppaku.Core.Exceptions;

namespace Oppaku.Core.Services;

public class RebuildProgress
{
    public List<int> Received { get; set; } = new();
    public int Total { get; set; }
}

public class Rebuilder
{
    public void InitialiseTarget(string destDir, ChunkMetadata metadata, string outputFileName)
    {
        if (!Directory.Exists(destDir))
            throw new OppakuException(ErrorCode.InvalidChunk, $"Destination directory not found: {destDir}");

        string targetFilePath = Path.Combine(destDir, outputFileName);
        string progressPath = $"{targetFilePath}.progress";

        if (!File.Exists(targetFilePath))
        {
            SparseFileHelper.CreateSparseFile(targetFilePath, metadata.TotalFileSize);
        }

        if (!File.Exists(progressPath))
        {
            var progress = new RebuildProgress { Total = metadata.TotalChunks };
            File.WriteAllText(progressPath, JsonSerializer.Serialize(progress));
        }
    }

    public string InsertChunk(string chunkBinPath, string targetLocation)
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

        string progressPath = $"{targetFilePath}.progress";

        if (!File.Exists(targetFilePath) || !File.Exists(progressPath))
        {
            InitialiseTarget(Path.GetDirectoryName(targetFilePath) ?? "", metadata, Path.GetFileName(targetFilePath));
        }

        using var sourcePayloadStream = new FileStream(chunkBinPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        sourcePayloadStream.Position = payloadStartOffset;

        using var destStream = new FileStream(targetFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        destStream.Position = metadata.ByteOffset;

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        byte[] buffer = new byte[64 * 1024];
        long bytesRemaining = metadata.ActualChunkSize;

        while (bytesRemaining > 0)
        {
            int bytesToRead = (int)Math.Min(buffer.Length, bytesRemaining);
            int bytesRead = sourcePayloadStream.Read(buffer, 0, bytesToRead);
            if (bytesRead == 0) break;

            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            destStream.Write(buffer, 0, bytesRead);
            bytesRemaining -= bytesRead;
        }
        
        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        string computedChecksum = $"sha256:{Convert.ToHexString(sha256.Hash!).ToLowerInvariant()}";

        if (computedChecksum != metadata.ChunkChecksum)
        {
            throw new OppakuException(ErrorCode.ChecksumMismatch, $"Chunk checksum mismatch for part {metadata.ChunkIndex}");
        }

        var progressJson = File.ReadAllText(progressPath);
        var progress = JsonSerializer.Deserialize<RebuildProgress>(progressJson) ?? new RebuildProgress();

        if (!progress.Received.Contains(metadata.ChunkIndex))
        {
            progress.Received.Add(metadata.ChunkIndex);
            progress.Received.Sort();
            File.WriteAllText(progressPath, JsonSerializer.Serialize(progress));
        }

        return targetFilePath;
    }

    public void Finalise(string targetFilePath, string sourceFileHash)
    {
        string progressPath = $"{targetFilePath}.progress";

        if (!File.Exists(targetFilePath))
            throw new OppakuException(ErrorCode.InvalidChunk, "Target file does not exist");
        if (!File.Exists(progressPath))
            throw new OppakuException(ErrorCode.InvalidChunk, "Progress file does not exist");

        var progress = JsonSerializer.Deserialize<RebuildProgress>(File.ReadAllText(progressPath));
        if (progress == null || progress.Received.Count < progress.Total)
        {
            throw new OppakuException(ErrorCode.InvalidChunk, "Cannot finalise: not all chunks have been received");
        }

        string rebuiltHash = ChecksumHelper.ComputeFileHash(targetFilePath);
        if (rebuiltHash != sourceFileHash)
        {
            throw new OppakuException(ErrorCode.ChecksumMismatch, "Final rebuilt file hash does not match original source hash");
        }

        File.Delete(progressPath);
    }
}
