using System.IO;
using Xunit;
using Oppaku.Core.Services;

namespace Oppaku.Tests;

public class ExtractorTests
{
    [Fact]
    public void ComputeSourceFileHash_CachesResultAndComputesCorrectly()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, new byte[100]); // 100 bytes of zeros
            
            var extractor = new Extractor();
            var hash1 = extractor.ComputeSourceFileHash(tempFile);
            var hash2 = extractor.ComputeSourceFileHash(tempFile);
            
            Assert.NotEmpty(hash1);
            Assert.Equal(hash1, hash2); // Should be exactly the same, potentially cached
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ExtractChunk_ExtractsCorrectly()
    {
        var tempSource = Path.GetTempFileName();
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            // 8 MB test file
            int fileSize = 8 * 1024 * 1024;
            var data = new byte[fileSize];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(tempSource, data);

            var extractor = new Extractor();
            string sourceHash = extractor.ComputeSourceFileHash(tempSource);
            string sourceFileName = Path.GetFileName(tempSource);

            // Extract chunk 0 (5 MB)
            long chunkSize = 5 * 1024 * 1024;
            extractor.ExtractChunk(tempSource, 0, chunkSize, tempDir, sourceHash);

            var chunk0Path = Path.Combine(tempDir, $"{sourceFileName}.part0.oppk");
            Assert.True(File.Exists(chunk0Path));

            Oppaku.Core.Models.ChunkMetadata meta0;
            byte[] chunk0Payload;
            using (var stream = new FileStream(chunk0Path, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(stream))
            {
                meta0 = Oppaku.Core.Models.ChunkMetadata.ReadFrom(reader);
                chunk0Payload = reader.ReadBytes((int)meta0.ActualChunkSize);
            }

            Assert.Equal(0, meta0.ChunkIndex);
            Assert.Equal(chunkSize, meta0.ActualChunkSize);
            Assert.Equal(sourceHash, meta0.SourceFileHash);
            Assert.Equal(chunkSize, chunk0Payload.Length);
            
            // Extract chunk 1 (3 MB remaining)
            extractor.ExtractChunk(tempSource, 1, chunkSize, tempDir, sourceHash);
            var chunk1Path = Path.Combine(tempDir, $"{sourceFileName}.part1.oppk");
            Assert.True(File.Exists(chunk1Path));

            Oppaku.Core.Models.ChunkMetadata meta1;
            byte[] chunk1Payload;
            using (var stream = new FileStream(chunk1Path, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(stream))
            {
                meta1 = Oppaku.Core.Models.ChunkMetadata.ReadFrom(reader);
                chunk1Payload = reader.ReadBytes((int)meta1.ActualChunkSize);
            }

            Assert.Equal(1, meta1.ChunkIndex);
            Assert.Equal(3 * 1024 * 1024, meta1.ActualChunkSize);
            Assert.Equal(sourceHash, meta1.SourceFileHash);
            Assert.Equal(3 * 1024 * 1024, chunk1Payload.Length);
        }
        finally
        {
            if (File.Exists(tempSource)) File.Delete(tempSource);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
