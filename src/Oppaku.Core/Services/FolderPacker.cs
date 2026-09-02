using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Oppaku.Core.Services;

public static class FolderPacker
{
    private const string Magic = "OPPAKDIR";

    public static void Pack(string sourceDir, string outputPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        bool packSucceeded = false;

        try
        {
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

            // 1. Write Header
            writer.Write(Magic);
            writer.Write(files.Length);

            long totalPayloadSize = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relPath = Path.GetRelativePath(sourceDir, file);
                var fi = new FileInfo(file);
                writer.Write(relPath);
                writer.Write(fi.Length);
                totalPayloadSize += fi.Length;
            }

            // 2. Write Payloads
            byte[] buffer = new byte[4 * 1024 * 1024]; // 4 MB buffer
            long totalWritten = 0;

            foreach (var file in files)
            {
                using var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                int bytesRead;
                while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    fs.Write(buffer, 0, bytesRead);
                    totalWritten += bytesRead;
                    progress?.Report(totalWritten);
                }
            }

            packSucceeded = true;
        }
        finally
        {
            if (!packSucceeded && File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { /* Ignore cleanup errors */ }
            }
        }
    }

    public static bool IsPackedFolder(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            return reader.ReadString() == Magic;
        }
        catch
        {
            return false;
        }
    }

    public static void Unpack(string archivePath, string destDir, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        string magic = reader.ReadString();
        if (magic != Magic)
            throw new InvalidDataException("Invalid folder archive format.");

        int fileCount = reader.ReadInt32();
        var fileEntries = new List<(string Path, long Size)>();

        for (int i = 0; i < fileCount; i++)
        {
            fileEntries.Add((reader.ReadString(), reader.ReadInt64()));
        }

        byte[] buffer = new byte[4 * 1024 * 1024];
        long totalExtracted = 0;

        foreach (var entry in fileEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string targetPath = Path.Combine(destDir, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            long bytesRemaining = entry.Size;
            bool fileCompleted = false;

            try
            {
                using (var destStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
                {
                    while (bytesRemaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                        long readOffset = fs.Position;
                        int bytesRead = fs.Read(buffer, 0, toRead);
                        if (bytesRead == 0) throw new EndOfStreamException("Unexpected end of archive.");

                        destStream.Write(buffer, 0, bytesRead);
                        
                        // Hole-punching to free space immediately (zero-space overhead)
                        try 
                        {
                            SparseFileHelper.SetZeroData(fs.SafeFileHandle, readOffset, bytesRead);
                        } 
                        catch { /* Ignore hole punch errors on non-NTFS/non-sparse volumes */ }

                        bytesRemaining -= bytesRead;
                        totalExtracted += bytesRead;
                        progress?.Report(totalExtracted);
                    }
                }
                fileCompleted = true;
            }
            finally
            {
                if (!fileCompleted && File.Exists(targetPath))
                {
                    try { File.Delete(targetPath); } catch { /* Ignore cleanup errors */ }
                }
            }
        }
    }
}
