using System;
using System.IO;
using System.Threading;
using Xunit;
using Oppaku.Core.Services;
using Oppaku.Core.Models;

namespace Oppaku.Tests;

public class CancellationAndRollbackTests
{
    private class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    [Fact]
    public void ExtractChunk_WhenCancelled_CleansUpPartialOppkFile()
    {
        var tempSource = Path.GetTempFileName();
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            // 10 MB test file
            int fileSize = 10 * 1024 * 1024;
            var data = new byte[fileSize];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(tempSource, data);

            var extractor = new Extractor();
            string sourceHash = extractor.ComputeSourceFileHash(tempSource);
            string sourceFileName = Path.GetFileName(tempSource);

            using var cts = new CancellationTokenSource();
            
            // Cancel synchronously on the very first progress callback
            var progress = new SynchronousProgress<long>(_ => cts.Cancel());

            string expectedChunkPath = Path.Combine(tempDir, $"{sourceFileName}.part0.oppk");

            Assert.Throws<OperationCanceledException>(() =>
            {
                extractor.ExtractChunk(tempSource, 0, 5 * 1024 * 1024, tempDir, sourceHash, progress, cts.Token);
            });

            // Verify the partial file was cleaned up and deleted
            Assert.False(File.Exists(expectedChunkPath), "Partial .oppk chunk file should be removed on cancellation.");
        }
        finally
        {
            if (File.Exists(tempSource)) File.Delete(tempSource);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ArchivePacker_WhenCancelled_CleansUpPartialArchiveFile()
    {
        var tempSourceDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempArchive = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".oppaku-archive");
        Directory.CreateDirectory(tempSourceDir);

        try
        {
            // Create a few files
            for (int i = 0; i < 5; i++)
            {
                var fileData = new byte[1024 * 1024]; // 1MB each
                new Random(i).NextBytes(fileData);
                File.WriteAllBytes(Path.Combine(tempSourceDir, $"file_{i}.dat"), fileData);
            }

            using var cts = new CancellationTokenSource();
            var progress = new SynchronousProgress<long>(_ => cts.Cancel());

            Assert.Throws<OperationCanceledException>(() =>
            {
                ArchivePacker.Pack(tempSourceDir, tempArchive, null, OppakuCompressionLevel.Normal, progress, cts.Token);
            });

            // Verify the partial archive file was removed
            Assert.False(File.Exists(tempArchive), "Partial .oppaku-archive file should be removed on cancellation.");
        }
        finally
        {
            if (Directory.Exists(tempSourceDir)) Directory.Delete(tempSourceDir, true);
            if (File.Exists(tempArchive)) File.Delete(tempArchive);
        }
    }

    [Fact]
    public void Rebuilder_WhenCancelled_PreservesTargetFileWithoutMarkingChunkReceived()
    {
        var tempSource = Path.GetTempFileName();
        var tempUsbDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempTargetDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        Directory.CreateDirectory(tempUsbDir);
        Directory.CreateDirectory(tempTargetDir);

        try
        {
            int fileSize = 8 * 1024 * 1024;
            var data = new byte[fileSize];
            new Random(99).NextBytes(data);
            File.WriteAllBytes(tempSource, data);

            var extractor = new Extractor();
            string sourceHash = extractor.ComputeSourceFileHash(tempSource);
            long chunkSize = 4 * 1024 * 1024;

            // Extract chunk 0 normally
            extractor.ExtractChunk(tempSource, 0, chunkSize, tempUsbDir, sourceHash);
            string sourceFileName = Path.GetFileName(tempSource);
            string chunk0Path = Path.Combine(tempUsbDir, $"{sourceFileName}.part0.oppk");

            var rebuilder = new Rebuilder();

            using var cts = new CancellationTokenSource();
            var progress = new SynchronousProgress<long>(_ => cts.Cancel());

            string targetFile = Path.Combine(tempTargetDir, sourceFileName);

            Assert.Throws<OperationCanceledException>(() =>
            {
                rebuilder.InsertChunk(chunk0Path, tempTargetDir, progress, cts.Token);
            });

            // Verify sparse target file is preserved on disk
            Assert.True(File.Exists(targetFile), "Target sparse file should be preserved on cancel.");

            // Verify chunk 0 is NOT marked as received
            var state = rebuilder.GetProgress(targetFile);
            Assert.NotNull(state);
            Assert.DoesNotContain(0, state.Received);
        }
        finally
        {
            if (File.Exists(tempSource)) File.Delete(tempSource);
            if (Directory.Exists(tempUsbDir)) Directory.Delete(tempUsbDir, true);
            if (Directory.Exists(tempTargetDir)) Directory.Delete(tempTargetDir, true);
        }
    }
}
