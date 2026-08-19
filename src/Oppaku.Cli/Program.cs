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
            Example: oppaku rebuild --chunks "".\part0.oppk;.\part1.oppk"" --dest .\rebuilt_file.zip

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
        return null;
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
            preparedPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(source) + ".oppaku.zip");
            if (File.Exists(preparedPath)) File.Delete(preparedPath);
            ZipFile.CreateFromDirectory(source, preparedPath, CompressionLevel.Fastest, false);
            Console.WriteLine($"      Zipped folder to temporary file.");
        }

        var extractor = new Extractor();
        Console.WriteLine($"[2/3] Computing whole-file SHA-256 hash. This may take a while...");
        string hash = extractor.ComputeSourceFileHash(preparedPath);
        
        var fi = new FileInfo(preparedPath);
        int totalChunks = (int)Math.Ceiling((double)fi.Length / chunkSizeBytes);
        Console.WriteLine($"      Hash: {hash}");
        Console.WriteLine($"      Total Parts: {totalChunks}");

        List<int> partsToExtract = new List<int>();
        if (partsStr.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            partsToExtract.AddRange(Enumerable.Range(0, totalChunks));
        }
        else
        {
            partsToExtract = partsStr.Split(',').Select(s => int.Parse(s.Trim())).ToList();
        }

        Console.WriteLine($"[3/3] Extracting {partsToExtract.Count} parts...");
        Directory.CreateDirectory(dest);
        
        int count = 0;
        foreach (var partIndex in partsToExtract)
        {
            if (partIndex < 0 || partIndex >= totalChunks)
                throw new ArgumentException($"Part index {partIndex} is out of bounds (0 to {totalChunks - 1}).");

            string chunkFileName = $"{Path.GetFileName(preparedPath)}.part{partIndex}.oppk";
            Console.WriteLine($"      -> Splitting part {partIndex} to '{chunkFileName}'");
            extractor.ExtractChunk(preparedPath, partIndex, chunkSizeBytes, dest, hash);
            count++;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nSuccessfully extracted {count} parts to {dest}");
        Console.ResetColor();
    }

    static void Rebuild(string[] args)
    {
        string chunksRaw = GetArg(args, "--chunks", true);
        string dest = GetArg(args, "--dest", true);

        string[] chunkFiles = chunksRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
        
        var rebuilder = new Rebuilder();
        string currentTarget = dest;

        Console.WriteLine($"[1/1] Merging {chunkFiles.Length} parts into target...");
        foreach (var file in chunkFiles)
        {
            if (!File.Exists(file))
                throw new FileNotFoundException($"Part file not found: {file}");
            
            Console.WriteLine($"      -> Merging '{Path.GetFileName(file)}'");
            currentTarget = rebuilder.InsertChunk(file, currentTarget);
        }

        string progressPath = $"{currentTarget}.progress";
        string statusMsg = "";
        if (File.Exists(progressPath))
        {
            var progress = System.Text.Json.JsonSerializer.Deserialize<RebuildProgress>(File.ReadAllText(progressPath));
            if (progress != null)
            {
                var missing = Enumerable.Range(0, progress.Total).Except(progress.Received).ToList();
                statusMsg = $"\nMerged {progress.Received.Count} / {progress.Total} parts total.";
                if (missing.Count > 0)
                    statusMsg += $"\nMissing parts: {string.Join(", ", missing)}";
                else
                    statusMsg += "\nAll parts merged! You can now run the 'finalise' command.";
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nSuccessfully inserted {chunkFiles.Length} part(s) into {currentTarget}");
        Console.WriteLine(statusMsg);
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

        Console.WriteLine($"[2/2] Finalising and verifying full hash. This may take a while...");
        var rebuilder = new Rebuilder();
        
        try
        {
            rebuilder.Finalise(targetFile, sourceHash);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\nFinalisation Complete!");
            Console.WriteLine($"File hash matches original source:\n{sourceHash}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            string progressPath = $"{targetFile}.progress";
            string extraInfo = "";
            if (File.Exists(progressPath))
            {
                var progress = System.Text.Json.JsonSerializer.Deserialize<RebuildProgress>(File.ReadAllText(progressPath));
                if (progress != null)
                {
                    var missing = Enumerable.Range(0, progress.Total).Except(progress.Received).ToList();
                    if (missing.Count > 0)
                        extraInfo = $"\nMissing parts: {string.Join(", ", missing)}";
                }
            }
            throw new Exception($"{ex.Message}{extraInfo}");
        }
    }
}
