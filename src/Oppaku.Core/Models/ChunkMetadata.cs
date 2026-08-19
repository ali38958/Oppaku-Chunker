using System.IO;

namespace Oppaku.Core.Models;

public record ChunkMetadata
{
    public const int Magic = 0x4B50504F; // "OPPK"
    public const byte Version = 1;

    public string FileName { get; init; } = string.Empty;
    public long TotalFileSize { get; init; }
    public long ChunkSize { get; init; }
    public int TotalChunks { get; init; }
    public int ChunkIndex { get; init; }
    public long ByteOffset { get; init; }
    public long ActualChunkSize { get; init; }
    public string SourceFileHash { get; init; } = string.Empty;
    public string ChunkChecksum { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string OppakuVersion { get; init; } = "2.0.0";

    public void WriteTo(BinaryWriter writer)
    {
        writer.Write(Magic);
        writer.Write(Version);

        writer.Write(FileName);
        writer.Write(TotalFileSize);
        writer.Write(ChunkSize);
        writer.Write(TotalChunks);
        writer.Write(ChunkIndex);
        writer.Write(ByteOffset);
        writer.Write(ActualChunkSize);
        writer.Write(SourceFileHash);
        writer.Write(ChunkChecksum);
        writer.Write(CreatedAt.ToUnixTimeMilliseconds());
        writer.Write(OppakuVersion);
    }

    public static ChunkMetadata ReadFrom(BinaryReader reader)
    {
        int magic = reader.ReadInt32();
        if (magic != Magic)
        {
            throw new Exception("Invalid OPPK file format.");
        }

        byte version = reader.ReadByte();
        if (version != Version)
        {
            throw new Exception($"Unsupported OPPK version: {version}");
        }

        return new ChunkMetadata
        {
            FileName = reader.ReadString(),
            TotalFileSize = reader.ReadInt64(),
            ChunkSize = reader.ReadInt64(),
            TotalChunks = reader.ReadInt32(),
            ChunkIndex = reader.ReadInt32(),
            ByteOffset = reader.ReadInt64(),
            ActualChunkSize = reader.ReadInt64(),
            SourceFileHash = reader.ReadString(),
            ChunkChecksum = reader.ReadString(),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64()),
            OppakuVersion = reader.ReadString()
        };
    }
}
