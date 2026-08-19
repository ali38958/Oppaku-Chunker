using System.IO;
using Xunit;
using Oppaku.Core.Models;

namespace Oppaku.Tests;

public class MetadataTests
{
    [Fact]
    public void ChunkMetadata_Binary_SerializesAndDeserializes_Correctly()
    {
        var meta = new ChunkMetadata
        {
            FileName = "ubuntu-24.iso",
            TotalFileSize = 5368709120,
            ChunkSize = 1073741824,
            TotalChunks = 5,
            ChunkIndex = 2,
            ByteOffset = 2147483648,
            ActualChunkSize = 1073741824,
            SourceFileHash = "sha256:abc123",
            ChunkChecksum = "sha256:def456",
            CreatedAt = new DateTimeOffset(2026, 8, 18, 23, 30, 0, TimeSpan.Zero),
            OppakuVersion = "2.0.0"
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        meta.WriteTo(writer);

        ms.Position = 0;
        using var reader = new BinaryReader(ms);
        var deserialized = ChunkMetadata.ReadFrom(reader);

        Assert.NotNull(deserialized);
        Assert.Equal(meta.FileName, deserialized.FileName);
        Assert.Equal(meta.TotalFileSize, deserialized.TotalFileSize);
        Assert.Equal(meta.ChunkSize, deserialized.ChunkSize);
        Assert.Equal(meta.TotalChunks, deserialized.TotalChunks);
        Assert.Equal(meta.ChunkIndex, deserialized.ChunkIndex);
        Assert.Equal(meta.ByteOffset, deserialized.ByteOffset);
        Assert.Equal(meta.ActualChunkSize, deserialized.ActualChunkSize);
        Assert.Equal(meta.SourceFileHash, deserialized.SourceFileHash);
        Assert.Equal(meta.ChunkChecksum, deserialized.ChunkChecksum);
        Assert.Equal(meta.CreatedAt.ToUnixTimeMilliseconds(), deserialized.CreatedAt.ToUnixTimeMilliseconds());
        Assert.Equal(meta.OppakuVersion, deserialized.OppakuVersion);
    }
}
