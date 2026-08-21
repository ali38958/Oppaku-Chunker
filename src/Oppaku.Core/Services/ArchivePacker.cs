using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Oppaku.Core.Models;

namespace Oppaku.Core.Services;

public static class ArchivePacker
{
    private const string MagicV1 = "OPPAKARC";
    private const string MagicV2 = "OPPAKAR2";

    public static void Pack(string sourcePath, string outputPath, string? password = null, OppakuCompressionLevel compression = OppakuCompressionLevel.None, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        bool isFolder = Directory.Exists(sourcePath);
        string[] files = isFolder 
            ? Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories).Where(f => !f.Equals(outputPath, StringComparison.OrdinalIgnoreCase)).ToArray()
            : new[] { sourcePath };
        
        bool packSucceeded = false;

        try
        {
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
            {
                // 1. Write Header
                writer.Write(MagicV2);
                
                bool isEncrypted = !string.IsNullOrEmpty(password);
                writer.Write(isEncrypted);
                writer.Write((byte)compression);

                byte[]? key = null;
                if (isEncrypted)
                {
                    byte[] salt = new byte[16];
                    RandomNumberGenerator.Fill(salt);
                    writer.Write(salt);
                    key = Rfc2898DeriveBytes.Pbkdf2(password!, salt, 100_000, HashAlgorithmName.SHA256, 32);
                }

                writer.Write(files.Length);

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relPath = isFolder ? Path.GetRelativePath(sourcePath, file) : Path.GetFileName(file);
                    var fi = new FileInfo(file);
                    writer.Write(relPath);
                    writer.Write(fi.Length);
                }
                
                writer.Flush();

                // 2. Setup Solid Pipeline: Raw -> Brotli -> AES -> File
                Stream outStream = fs;
                CryptoStream? cryptoStream = null;
                BrotliStream? brotliStream = null;

                try
                {
                    if (isEncrypted)
                    {
                        using var aes = Aes.Create();
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        aes.GenerateIV();
                        
                        writer.Write(aes.IV);
                        writer.Flush();
                        
                        var encryptor = aes.CreateEncryptor(key!, aes.IV);
                        cryptoStream = new CryptoStream(outStream, encryptor, CryptoStreamMode.Write, leaveOpen: true);
                        outStream = cryptoStream;
                    }

                    if (compression != OppakuCompressionLevel.None)
                    {
                        CompressionLevel cl = compression switch {
                            OppakuCompressionLevel.Normal => CompressionLevel.Fastest,
                            OppakuCompressionLevel.High => CompressionLevel.Optimal,
                            OppakuCompressionLevel.Extreme => CompressionLevel.SmallestSize,
                            _ => CompressionLevel.Optimal
                        };
                        brotliStream = new BrotliStream(outStream, cl, leaveOpen: true);
                        outStream = brotliStream;
                    }

                    byte[] buffer = new byte[4 * 1024 * 1024]; // 4 MB buffer
                    long totalWritten = 0;

                    foreach (var file in files)
                    {
                        using var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                        int bytesRead;
                        while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            outStream.Write(buffer, 0, bytesRead);
                            totalWritten += bytesRead;
                            progress?.Report(totalWritten);
                        }
                    }
                    
                    brotliStream?.Dispose();
                    brotliStream = null;

                    cryptoStream?.FlushFinalBlock();
                    cryptoStream?.Dispose();
                    cryptoStream = null;

                    packSucceeded = true;
                }
                finally
                {
                    brotliStream?.Dispose();
                    cryptoStream?.Dispose();
                }
            }
        }
        finally
        {
            if (!packSucceeded && File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { /* Ignore cleanup errors */ }
            }
        }
    }

    public static bool IsPackedArchive(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            string magic = reader.ReadString();
            return magic == MagicV1 || magic == MagicV2;
        }
        catch
        {
            return false;
        }
    }

    public static void Unpack(string archivePath, string destDir, string? password = null, Func<string, bool>? onOverwriteConfirm = null, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        string magic = reader.ReadString();
        bool isV2 = magic == MagicV2;
        
        if (magic != MagicV1 && magic != MagicV2)
            throw new InvalidDataException("Invalid archive format.");

        bool isEncrypted = reader.ReadBoolean();
        
        OppakuCompressionLevel compression = OppakuCompressionLevel.None;
        if (isV2)
        {
            compression = (OppakuCompressionLevel)reader.ReadByte();
        }

        byte[]? key = null;
        if (isEncrypted)
        {
            if (string.IsNullOrEmpty(password))
                throw new UnauthorizedAccessException("This archive is encrypted. A password is required.");

            byte[] salt = reader.ReadBytes(16);
            key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        }

        int fileCount = reader.ReadInt32();
        var fileEntries = new List<(string Path, long OriginalSize)>();

        for (int i = 0; i < fileCount; i++)
        {
            fileEntries.Add((reader.ReadString(), reader.ReadInt64()));
        }

        if (!isV2)
        {
            UnpackV1(fs, reader, destDir, fileEntries, isEncrypted, key, onOverwriteConfirm, progress, cancellationToken);
            return;
        }

        Stream inStream = fs;
        CryptoStream? cryptoStream = null;
        BrotliStream? brotliStream = null;

        if (isEncrypted)
        {
            byte[] iv = reader.ReadBytes(16);
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            var decryptor = aes.CreateDecryptor(key!, iv);
            cryptoStream = new CryptoStream(inStream, decryptor, CryptoStreamMode.Read, leaveOpen: true);
            inStream = cryptoStream;
        }

        if (compression != OppakuCompressionLevel.None)
        {
            brotliStream = new BrotliStream(inStream, CompressionMode.Decompress, leaveOpen: true);
            inStream = brotliStream;
        }

        byte[] buffer = new byte[4 * 1024 * 1024];
        long totalExtracted = 0;

        try
        {
            foreach (var entry in fileEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string targetPath = Path.Combine(destDir, entry.Path);
                
                bool skipFile = false;
                if (File.Exists(targetPath) && onOverwriteConfirm != null)
                {
                    skipFile = !onOverwriteConfirm(entry.Path);
                }

                if (!skipFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                }

                long bytesRemaining = entry.OriginalSize;
                FileStream? destStream = skipFile ? null : new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
                bool fileExtractCompleted = false;

                try
                {
                    while (bytesRemaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                        int bytesRead = inStream.Read(buffer, 0, toRead);
                        
                        if (bytesRead == 0) throw new EndOfStreamException("Unexpected end of archive.");

                        if (destStream != null)
                        {
                            destStream.Write(buffer, 0, bytesRead);
                        }
                        
                        bytesRemaining -= bytesRead;
                        totalExtracted += bytesRead;
                        progress?.Report(totalExtracted);
                    }

                    fileExtractCompleted = true;
                }
                finally
                {
                    destStream?.Dispose();
                    if (!skipFile && !fileExtractCompleted && File.Exists(targetPath))
                    {
                        try { File.Delete(targetPath); } catch { /* Ignore cleanup errors */ }
                    }
                }
            }
        }
        finally
        {
            brotliStream?.Dispose();
            cryptoStream?.Dispose();
        }
    }

    private static void UnpackV1(FileStream fs, BinaryReader reader, string destDir, List<(string Path, long OriginalSize)> fileEntries, bool isEncrypted, byte[]? key, Func<string, bool>? onOverwriteConfirm, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[4 * 1024 * 1024];
        long totalExtracted = 0;

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        foreach (var entry in fileEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string targetPath = Path.Combine(destDir, entry.Path);
            
            bool skipFile = false;
            if (File.Exists(targetPath) && onOverwriteConfirm != null)
            {
                skipFile = !onOverwriteConfirm(entry.Path);
            }

            if (!skipFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            }

            long paddedSize = (entry.OriginalSize / 16 + 1) * 16;
            long bytesToReadFromArchive = entry.OriginalSize;

            FileStream? destStream = skipFile ? null : new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
            bool fileExtractCompleted = false;

            try
            {
                if (isEncrypted)
                {
                    byte[] iv = reader.ReadBytes(16);
                    bytesToReadFromArchive = paddedSize;
                    
                    using var decryptor = aes.CreateDecryptor(key!, iv);
                    long bytesRemaining = paddedSize;
                    while (bytesRemaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                        int bytesRead = fs.Read(buffer, 0, toRead);
                        if (bytesRead == 0) throw new EndOfStreamException("Unexpected end of archive.");

                        if (bytesRemaining == bytesRead) 
                        {
                            byte[] decrypted = decryptor.TransformFinalBlock(buffer, 0, bytesRead);
                            if (destStream != null) destStream.Write(decrypted, 0, decrypted.Length);
                            totalExtracted += decrypted.Length;
                        }
                        else
                        {
                            byte[] decrypted = new byte[bytesRead];
                            int decryptedCount = decryptor.TransformBlock(buffer, 0, bytesRead, decrypted, 0);
                            if (destStream != null) destStream.Write(decrypted, 0, decryptedCount);
                            totalExtracted += decryptedCount;
                        }
                        
                        bytesRemaining -= bytesRead;
                        progress?.Report(totalExtracted);
                    }
                }
                else
                {
                    long bytesRemaining = bytesToReadFromArchive;
                    while (bytesRemaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                        int bytesRead = fs.Read(buffer, 0, toRead);
                        if (bytesRead == 0) throw new EndOfStreamException("Unexpected end of archive.");

                        if (destStream != null) destStream.Write(buffer, 0, bytesRead);
                        bytesRemaining -= bytesRead;
                        totalExtracted += bytesRead;
                        progress?.Report(totalExtracted);
                    }
                }

                fileExtractCompleted = true;
            }
            finally
            {
                destStream?.Dispose();
                if (!skipFile && !fileExtractCompleted && File.Exists(targetPath))
                {
                    try { File.Delete(targetPath); } catch { /* Ignore cleanup errors */ }
                }
            }
        }
    }
}
