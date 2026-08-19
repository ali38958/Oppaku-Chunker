using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using Oppaku.Core.Models;
using Oppaku.Core.Services;

namespace Oppaku.Cli;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
        {
            ShowHelp();
            return;
        }

        string command = args[0].ToLowerInvariant();
        
        try
        {
            switch (command)
            {
                case "extract":
                    Extract(args);
                    break;
                case "rebuild":
                    Rebuild(args);
                    break;
                case "finalise":
                case "finalize":
                    Finalise(args);
                    break;
                default:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Unknown command: {command}");
                    Console.ResetColor();
                    ShowHelp();
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] {ex.Message}");
            Console.ResetColor();
        }
    }

    static void ShowHelp()
    {
        Console.WriteLine(@"
Oppaku - File Chunker CLI
=========================
Commands:
  extract   Extract parts from a source file or folder.
            Usage: oppaku extract --source <path> --dest <folder> --size <num> --unit <KB|MB|GB> --parts <all | 0,1,2>
            Example: oppaku extract --source .\myfolder --dest .\output --size 100 --unit MB --parts all

  rebuild   Insert chunk(s) into a target file or folder.
            Usage: oppaku rebuild --chunks <file1;file2> --dest <file/folder>
            Example: oppaku rebuild --chunks "".\\part0.oppk;.\\part1.oppk"" --dest .\rebuilt_file.zip

  finalise  Verify and finalise a rebuilt file. (can also use 'finalize')
            Usage: oppaku finalise --chunk <any_chunk.oppk> --dest <file>
            Example: oppaku finalise --chunk .\part0.oppk --dest .\rebuilt_file.zip
");
    }

    static string GetArg(string[] args, string name, bool required = false)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        if (required)
            throw new ArgumentException($"Missing required argument: {name}");
        return null!;
    }

    static void PrintProgress(long current, long total, string label)
    {
        if (total <= 0) return;
        int pct = (int)(current * 100 / total);
        int filled = pct / 4; // 25 chars wide bar
        string bar = $"[{new string('█', filled)}{new string('░', 25 - filled)}]";
        Console.Write($"\r  {bar} {pct,3}%  {label}   ");
    }

    static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.00} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0.00} KB";
        return $"{bytes} B";
    }

    static void Extract(string[] args)
    {
        string source = GetArg(args, "--source", true);
        string dest = GetArg(args, "--dest", true);
        string sizeStr = GetArg(args, "--size", true);
        string unitStr = GetArg(args, "--unit", true);
        string partsStr = GetArg(args, "--parts", true);

        if (!long.TryParse(sizeStr, out long sizeValue))
            throw new ArgumentException("Size must be a valid number.");

        long chunkSizeBytes = unitStr.ToUpper() switch
        {
            "KB" => sizeValue * 1024,
            "MB" => sizeValue * 1024 * 1024,
            "GB" => sizeValue * 1024 * 1024 * 1024,
            _ => throw new ArgumentException("Unit must be KB, MB, or GB.")
        };

        Console.WriteLine($"[1/3] Preparing source: {source}");
        string preparedPath = source;
        if (Directory.Exists(source))
        {
            preparedPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(source) + ".oppaku-dir");
            if (File.Exists(preparedPath)) File.Delete(preparedPath);
            FolderPacker.Pack(source, preparedPath);
            Console.WriteLine("      Packed folder to zero-space compatible archive.");
        }

        var fi = new FileInfo(preparedPath);
        Console.WriteLine($"[2/3] Computing SHA-256 hash of {FormatBytes(fi.Length)}...");

        var extractor = new Extractor();
        string hash = "";

        var hashProgress = new Progress<long>(bytesRead =>
            PrintProgress(bytesRead, fi.Length, $"{FormatBytes(bytesRead)} / {FormatBytes(fi.Length)} hashed"));

        hash = extractor.ComputeSourceFileHash(preparedPath, hashProgress);
        Console.WriteLine(); // newline after progress bar

        int totalChunks = (int)Math.Ceiling((double)fi.Length / chunkSizeBytes);
        Console.WriteLine($"      Hash : {hash}");
        Console.WriteLine($"      Parts: {totalChunks} × {sizeValue} {unitStr}");

        List<int> partsToExtract = new();
        if (partsStr.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            partsToExtract.AddRange(Enumerable.Range(0, totalChunks));
        }
        else
        {
            partsToExtract = partsStr.Split(',').Select(s => int.Parse(s.Trim())).ToList();
        }

        Console.WriteLine($"[3/3] Extracting {partsToExtract.Count} part(s) to '{dest}'...");
        Directory.CreateDirectory(dest);
        
        int count = 0;
        foreach (var partIndex in partsToExtract)
        {
            if (partIndex < 0 || partIndex >= totalChunks)
                throw new ArgumentException($"Part index {partIndex} is out of bounds (0 to {totalChunks - 1}).");

            long partSize = Math.Min(chunkSizeBytes, fi.Length - (long)partIndex * chunkSizeBytes);
            string chunkFileName = $"{Path.GetFileName(preparedPath)}.part{partIndex}.oppk";
            Console.WriteLine($"  → Part {partIndex}: {FormatBytes(partSize)}");

            var partProgress = new Progress<long>(bytesWritten =>
                PrintProgress(bytesWritten, partSize, $"{FormatBytes(bytesWritten)} / {FormatBytes(partSize)} written"));

            extractor.ExtractChunk(preparedPath, partIndex, chunkSizeBytes, dest, hash, partProgress);
            Console.WriteLine($"\r  ✓ Part {partIndex} written → {chunkFileName}              ");
            count++;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nSuccessfully extracted {count} part(s) to '{dest}'");
        Console.ResetColor();
    }

    static void Rebuild(string[] args)
    {
        string chunksRaw = GetArg(args, "--chunks", true);
        string dest = GetArg(args, "--dest", true);

        string[] chunkFiles = chunksRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(s => s.Trim()).ToArray();
        
        var rebuilder = new Rebuilder();
        string currentTarget = dest;

        Console.WriteLine($"[1/1] Merging {chunkFiles.Length} part(s) into target...");

        int count = 0;
        foreach (var file in chunkFiles)
        {
            if (!File.Exists(file))
                throw new FileNotFoundException($"Part file not found: {file}");

            // Read payload size from header for accurate progress
            long chunkPayloadSize = new FileInfo(file).Length;
            try
            {
                using var s = new FileStream(file, FileMode.Open, FileAccess.Read);
                using var r = new BinaryReader(s, System.Text.Encoding.UTF8);
                var meta = ChunkMetadata.ReadFrom(r);
                chunkPayloadSize = meta.ActualChunkSize;
            }
            catch { /* fallback to file size */ }

            count++;
            Console.WriteLine($"  → [{count}/{chunkFiles.Length}] '{Path.GetFileName(file)}' ({FormatBytes(chunkPayloadSize)})");

            long capturedSize = chunkPayloadSize;
            var partProgress = new Progress<long>(bytesWritten =>
                PrintProgress(bytesWritten, capturedSize, $"{FormatBytes(bytesWritten)} / {FormatBytes(capturedSize)} written"));

            currentTarget = rebuilder.InsertChunk(file, currentTarget, partProgress);
            Console.WriteLine($"\r  ✓ Merged.                                                  ");
        }

        // Report status from embedded metadata
        var state = rebuilder.GetProgress(currentTarget);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nSuccessfully inserted {chunkFiles.Length} part(s) into '{currentTarget}'");
        if (state != null)
        {
            var missing = Enumerable.Range(0, state.Total).Except(state.Received).ToList();
            Console.WriteLine($"Progress: {state.Received.Count} / {state.Total} parts in file.");
            if (missing.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Missing parts: {string.Join(", ", missing)}");
            }
            else
            {
                Console.WriteLine("All parts inserted — auto-starting finalisation!");
                Console.ResetColor();
                Finalise(new string[] { "finalise", "--chunk", chunkFiles[0], "--dest", currentTarget });
                return; // Exit Rebuild, Finalise handles the rest
            }
        }
        Console.ResetColor();
    }

    static void Finalise(string[] args)
    {
        string chunkFile = GetArg(args, "--chunk", true);
        string targetFile = GetArg(args, "--dest", true);

        if (!File.Exists(chunkFile))
            throw new FileNotFoundException($"Chunk file not found: {chunkFile}");

        Console.WriteLine($"[1/2] Reading master hash from '{Path.GetFileName(chunkFile)}'...");
        string sourceHash;
        using (var stream = new FileStream(chunkFile, FileMode.Open, FileAccess.Read))
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8))
        {
            var meta = ChunkMetadata.ReadFrom(reader);
            sourceHash = meta.SourceFileHash;
        }
        Console.WriteLine($"      Hash: {sourceHash}");

        var rebuilder = new Rebuilder();

        // Get content size from embedded metadata for accurate progress display
        var state = rebuilder.GetProgress(targetFile);
        long contentSize = state?.ContentSize ?? new FileInfo(targetFile).Length;

        Console.WriteLine($"[2/2] Verifying {FormatBytes(contentSize)}...");

        var finalProgress = new Progress<long>(bytesHashed =>
            PrintProgress(bytesHashed, contentSize, $"{FormatBytes(bytesHashed)} / {FormatBytes(contentSize)} verified"));

        try
        {
            rebuilder.Finalise(targetFile, sourceHash, finalProgress);
            Console.WriteLine(); // newline after progress bar

            if (FolderPacker.IsPackedFolder(targetFile))
            {
                string destDir = targetFile.EndsWith(".oppaku-dir") 
                    ? targetFile.Substring(0, targetFile.Length - 11) 
                    : targetFile + "_extracted";

                Console.WriteLine($"[3/3] Unpacking folder to '{destDir}' using zero-space hole-punching...");
                var unpackProgress = new Progress<long>(bytes =>
                    PrintProgress(bytes, contentSize, $"{FormatBytes(bytes)} / {FormatBytes(contentSize)} unpacked"));
                
                FolderPacker.Unpack(targetFile, destDir, unpackProgress);
                Console.WriteLine();
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n✓ Finalisation Complete!");
            Console.WriteLine($"  File is intact and matches original source.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.WriteLine(); // newline after progress bar

            // Report missing parts from embedded metadata
            var failState = rebuilder.GetProgress(targetFile);
            string extraInfo = "";
            if (failState != null)
            {
                var missing = Enumerable.Range(0, failState.Total).Except(failState.Received).ToList();
                if (missing.Count > 0)
                    extraInfo = $"\nMissing parts: {string.Join(", ", missing)}";
            }
            throw new Exception($"{ex.Message}{extraInfo}");
        }
    }
}
