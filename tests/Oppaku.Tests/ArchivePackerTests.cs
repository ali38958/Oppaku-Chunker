using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using Xunit;
using Oppaku.Core.Models;
using Oppaku.Core.Services;

namespace Oppaku.Tests;

public class ArchivePackerTests
{
    private class TestProgress : IProgress<long>
    {
        public long LastReported { get; private set; }
        public int CallCount { get; private set; }
        public void Report(long value)
        {
            LastReported = value;
            CallCount++;
        }
    }

    [Fact]
    public void Pack_CreatesStandardZipCompliantArchive()
    {
        var tempSourceDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempArchive = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".oppaku-archive");
        var tempExtractDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        Directory.CreateDirectory(tempSourceDir);
        Directory.CreateDirectory(Path.Combine(tempSourceDir, "nested", "subfolder"));

        try
        {
            // Create test files
            File.WriteAllText(Path.Combine(tempSourceDir, "root.txt"), "Hello from root!");
            File.WriteAllText(Path.Combine(tempSourceDir, "nested", "file1.txt"), "Content of file 1");
            File.WriteAllText(Path.Combine(tempSourceDir, "nested", "subfolder", "file2.bin"), "Binary test data 1234567890");

            var progress = new TestProgress();
            ArchivePacker.Pack(tempSourceDir, tempArchive, null, OppakuCompressionLevel.Normal, progress);

            // 1. Verify archive exists and has non-zero length
            Assert.True(File.Exists(tempArchive));
            Assert.True(new FileInfo(tempArchive).Length > 0);

            // 2. Verify standard ZIP magic header PK\x03\x04
            byte[] header = new byte[4];
            using (var fs = File.OpenRead(tempArchive))
            {
                fs.ReadExactly(header);
            }
            Assert.Equal(0x50, header[0]); // 'P'
            Assert.Equal(0x4B, header[1]); // 'K'
            Assert.Equal(0x03, header[2]);
            Assert.Equal(0x04, header[3]);

            // 3. Verify IsPackedArchive identifies it
            Assert.True(ArchivePacker.IsPackedArchive(tempArchive));

            // 4. Verify standard third-party ZipArchive can open and read entries directly
            using (var zip = ZipFile.OpenRead(tempArchive))
            {
                Assert.Equal(3, zip.Entries.Count);
                Assert.Contains(zip.Entries, e => e.FullName == "root.txt");
                Assert.Contains(zip.Entries, e => e.FullName == "nested/file1.txt");
                Assert.Contains(zip.Entries, e => e.FullName == "nested/subfolder/file2.bin");
            }

            // 5. Verify Unpack extracts files accurately
            ArchivePacker.Unpack(tempArchive, tempExtractDir);

            Assert.True(File.Exists(Path.Combine(tempExtractDir, "root.txt")));
            Assert.Equal("Hello from root!", File.ReadAllText(Path.Combine(tempExtractDir, "root.txt")));

            Assert.True(File.Exists(Path.Combine(tempExtractDir, "nested", "file1.txt")));
            Assert.Equal("Content of file 1", File.ReadAllText(Path.Combine(tempExtractDir, "nested", "file1.txt")));

            Assert.True(File.Exists(Path.Combine(tempExtractDir, "nested", "subfolder", "file2.bin")));
            Assert.Equal("Binary test data 1234567890", File.ReadAllText(Path.Combine(tempExtractDir, "nested", "subfolder", "file2.bin")));
        }
        finally
        {
            if (Directory.Exists(tempSourceDir)) Directory.Delete(tempSourceDir, true);
            if (File.Exists(tempArchive)) File.Delete(tempArchive);
            if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, true);
        }
    }

    [Theory]
    [InlineData(OppakuCompressionLevel.None)]
    [InlineData(OppakuCompressionLevel.Normal)]
    [InlineData(OppakuCompressionLevel.High)]
    [InlineData(OppakuCompressionLevel.Extreme)]
    public void Pack_SupportsAllCompressionLevels(OppakuCompressionLevel level)
    {
        var tempFile = Path.GetTempFileName();
        var tempArchive = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zip");
        var tempExtractDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            var content = new string('A', 50000);
            File.WriteAllText(tempFile, content);

            ArchivePacker.Pack(tempFile, tempArchive, null, level);
            Assert.True(File.Exists(tempArchive));
            Assert.True(ArchivePacker.IsPackedArchive(tempArchive));

            ArchivePacker.Unpack(tempArchive, tempExtractDir);
            string extractedFile = Path.Combine(tempExtractDir, Path.GetFileName(tempFile));
            Assert.True(File.Exists(extractedFile));
            Assert.Equal(content, File.ReadAllText(extractedFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempArchive)) File.Delete(tempArchive);
            if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, true);
        }
    }

    [Fact]
    public void Unpack_RespectsOverwriteConfirmCallback()
    {
        var tempSourceDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempArchive = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".oppaku-archive");
        var tempExtractDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        Directory.CreateDirectory(tempSourceDir);
        Directory.CreateDirectory(tempExtractDir);

        try
        {
            File.WriteAllText(Path.Combine(tempSourceDir, "test.txt"), "New Content");
            ArchivePacker.Pack(tempSourceDir, tempArchive);

            // Pre-create file in dest with old content
            File.WriteAllText(Path.Combine(tempExtractDir, "test.txt"), "Old Content");

            // Extract with callback rejecting overwrite
            ArchivePacker.Unpack(tempArchive, tempExtractDir, onOverwriteConfirm: _ => false);
            Assert.Equal("Old Content", File.ReadAllText(Path.Combine(tempExtractDir, "test.txt")));

            // Extract with callback accepting overwrite
            ArchivePacker.Unpack(tempArchive, tempExtractDir, onOverwriteConfirm: _ => true);
            Assert.Equal("New Content", File.ReadAllText(Path.Combine(tempExtractDir, "test.txt")));
        }
        finally
        {
            if (Directory.Exists(tempSourceDir)) Directory.Delete(tempSourceDir, true);
            if (File.Exists(tempArchive)) File.Delete(tempArchive);
            if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, true);
        }
    }
}
