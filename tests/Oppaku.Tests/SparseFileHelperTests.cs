using System.IO;
using Oppaku.Core.Services;

namespace Oppaku.Tests;

public class SparseFileHelperTests
{
    [Fact]
    public void CreateSparseFile_CreatesFileWithCorrectLogicalSizeAndAttribute()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "sparse_test.bin");
        try
        {
            long targetSize = 4L * 1024 * 1024 * 1024; // 4 GB
            SparseFileHelper.CreateSparseFile(tempPath, targetSize);

            var info = new FileInfo(tempPath);
            Assert.True(info.Exists);
            Assert.Equal(targetSize + SparseFileHelper.MetadataReserve, info.Length);
            
            // Check if Sparse file attribute is set
            Assert.True((info.Attributes & FileAttributes.SparseFile) == FileAttributes.SparseFile);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
