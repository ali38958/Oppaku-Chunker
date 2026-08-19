using System.IO;
using Xunit;
using Oppaku.Core.Services;
using Oppaku.Core.Models;

namespace Oppaku.Tests;

public class RebuilderTests
{
    [Fact]
    public void Rebuilder_EndToEnd_RoundTrip_WorksCorrectly()
    {
        var tempSource = Path.GetTempFileName();
        var tempUsbDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempTargetDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        
        Directory.CreateDirectory(tempUsbDir);
        Directory.CreateDirectory(tempTargetDir);

        try
        {
            // 1. Create a 6MB test file
            int fileSize = 6 * 1024 * 1024;
            var data = new byte[fileSize];
            new Random(123).NextBytes(data);
            File.WriteAllBytes(tempSource, data);

            // 2. Extract into 3 chunks
            var extractor = new Extractor();
            string sourceHash = extractor.ComputeSourceFileHash(tempSource);
            long chunkSize = 2 * 1024 * 1024;

            var rebuilder = new Rebuilder();
            string sourceFileName = Path.GetFileName(tempSource);
            string finalTargetPath = Path.Combine(tempTargetDir, sourceFileName);

            for (int i = 0; i < 3; i++)
            {
                // Simulate Source PC -> USB
                extractor.ExtractChunk(tempSource, i, chunkSize, tempUsbDir, sourceHash);
                
                // Simulate USB -> Target PC
                string chunkPath = Path.Combine(tempUsbDir, $"{sourceFileName}.part{i}.oppk");
                
                string resultPath = rebuilder.InsertChunk(chunkPath, tempTargetDir);
                Assert.Equal(finalTargetPath, resultPath);
                
                // Clean up USB for next trip
                File.Delete(chunkPath);
            }
            
            // 4. Finalise
            rebuilder.Finalise(finalTargetPath, sourceHash);
            
            // 5. Verify bit-for-bit
            Assert.True(File.Exists(finalTargetPath));
            
            string targetHash = Oppaku.Core.Helpers.ChecksumHelper.ComputeFileHash(finalTargetPath);
            Assert.Equal(sourceHash, targetHash);
        }
        finally
        {
            if (File.Exists(tempSource)) File.Delete(tempSource);
            if (Directory.Exists(tempUsbDir)) Directory.Delete(tempUsbDir, true);
            if (Directory.Exists(tempTargetDir)) Directory.Delete(tempTargetDir, true);
        }
    }
}
