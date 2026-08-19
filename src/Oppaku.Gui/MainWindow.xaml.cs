using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Oppaku.Core.Services;
using Oppaku.Core.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Oppaku.Gui;

public class PartViewModel : INotifyPropertyChanged
{
    public int Index { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    
    private bool _isSelected;
    public bool IsSelected 
    { 
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public partial class MainWindow : Window
{
    private string? _sourceHash;
    private string? _preparedSourcePath;
    private readonly Extractor _extractor = new();
    private readonly Rebuilder _rebuilder = new();
    public ObservableCollection<PartViewModel> Parts { get; set; } = new();
    private readonly ObservableCollection<string> _logLines = new();

    public MainWindow()
    {
        InitializeComponent();
        Parts = new ObservableCollection<PartViewModel>();
        LstParts.ItemsSource = Parts;
        LstLog.ItemsSource = _logLines;
        Log("Oppaku ready. Please select a source and calculate parts.");
    }

    // ─── Logging ──────────────────────────────────────────────────────────────

    private void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logLines.Add(line);
        LstLog.ScrollIntoView(line);
    }

    private void SetProgress(double value, double max, string title, string detail = "")
    {
        TxtGlobalTitle.Text = title;
        PbGlobalProgress.IsIndeterminate = false;
        PbGlobalProgress.Maximum = max;
        PbGlobalProgress.Value = value;
        TxtProgressPercent.Text = max > 0 ? $"{value / max * 100:0.0}%" : "";
        TxtProgressDetail.Text = detail;
    }

    private void SetIndeterminate(string title, string detail = "")
    {
        TxtGlobalTitle.Text = title;
        PbGlobalProgress.IsIndeterminate = true;
        TxtProgressPercent.Text = "";
        TxtProgressDetail.Text = detail;
    }

    private void ClearProgress(string title = "Ready")
    {
        TxtGlobalTitle.Text = title;
        PbGlobalProgress.IsIndeterminate = false;
        PbGlobalProgress.Value = 0;
        TxtProgressPercent.Text = "";
        TxtProgressDetail.Text = "";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.00} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0.00} KB";
        return $"{bytes} B";
    }

    // ─── Browse Handlers ──────────────────────────────────────────────────────

    private void BtnBrowseSourceFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select Source File" };
        if (dialog.ShowDialog() == true)
        {
            TxtSourceFile.Text = dialog.FileName;
            ResetExtractState();
        }
    }

    private void BtnBrowseSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Source Folder" };
        if (dialog.ShowDialog() == true)
        {
            TxtSourceFile.Text = dialog.FolderName;
            ResetExtractState();
        }
    }

    private void BtnBrowseExtractOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Output Folder (USB)" };
        if (dialog.ShowDialog() == true)
        {
            TxtExtractOutput.Text = dialog.FolderName;
            CheckExtractReady();
        }
    }

    private void ResetExtractState()
    {
        _sourceHash = null;
        _preparedSourcePath = null;
        Parts.Clear();
        BtnExtract.IsEnabled = false;
        Log("Source changed — please recalculate parts.");
        ClearProgress();
    }

    private long GetChunkSizeBytes(out long value, out string unit)
    {
        value = long.Parse(CboChunkSize.Text ?? "100");
        unit = ((ComboBoxItem)CboChunkUnit.SelectedItem).Content.ToString() ?? "MB";
        
        return unit switch
        {
            "KB" => value * 1024,
            "MB" => value * 1024 * 1024,
            "GB" => value * 1024 * 1024 * 1024,
            _ => value * 1024 * 1024
        };
    }

    // ─── Calculate Parts ──────────────────────────────────────────────────────

    private async void BtnCalculateParts_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TxtSourceFile.Text)) return;
        
        BtnCalculateParts.IsEnabled = false;
        Parts.Clear();
        
        Log("─────────────────────────────────");
        Log($"Source: {TxtSourceFile.Text}");

        string sourcePath = TxtSourceFile.Text;
        
        try
        {
            // Step 1 — zip if folder
            if (Directory.Exists(sourcePath))
            {
                SetIndeterminate("Packing Folder", "Packing folder into zero-space archive...");
                Log("Source is a folder — packing to zero-space archive...");

                await Task.Run(() =>
                {
                    string packPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(sourcePath) + ".oppaku-dir");
                    if (File.Exists(packPath)) File.Delete(packPath);
                    FolderPacker.Pack(sourcePath, packPath);
                    _preparedSourcePath = packPath;
                });

                Log($"Packing complete → {Path.GetFileName(_preparedSourcePath!)}");
            }
            else
            {
                _preparedSourcePath = sourcePath;
            }

            // Step 2 — hash
            var fi = new FileInfo(_preparedSourcePath!);
            long fileSize = fi.Length;
            Log($"File size: {FormatBytes(fileSize)}");
            Log("Computing SHA-256 hash...");
            SetProgress(0, fileSize, "Computing Hash", $"0 B / {FormatBytes(fileSize)}");

            string preparedPath = _preparedSourcePath!;
            string hash = "";

            var hashProgress = new Progress<long>(bytesRead =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    SetProgress(bytesRead, fileSize, "Computing Hash",
                        $"{FormatBytes(bytesRead)} / {FormatBytes(fileSize)} hashed");
                });
            });

            await Task.Run(() => hash = _extractor.ComputeSourceFileHash(preparedPath, hashProgress));
            _sourceHash = hash;

            // Step 3 — compute parts
            long chunkSizeBytes = GetChunkSizeBytes(out long value, out string unit);
            int totalChunks = (int)Math.Ceiling((double)fileSize / chunkSizeBytes);
            
            for (int i = 0; i < totalChunks; i++)
            {
                long partBytes = (i < totalChunks - 1) ? chunkSizeBytes : (fileSize - (long)i * chunkSizeBytes);
                Parts.Add(new PartViewModel
                {
                    Index = i,
                    DisplayName = $"Part {i}  —  {value} {unit}  ({FormatBytes(partBytes)})",
                    IsSelected = false
                });
            }

            Log($"Hash: {_sourceHash}");
            Log($"Total parts: {totalChunks} × {value} {unit}  (last part may be smaller)");
            SetProgress(fileSize, fileSize, "Hash Complete", $"All {FormatBytes(fileSize)} hashed ✓");
            CheckExtractReady();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Log($"ERROR: {ex.Message}");
            ClearProgress("Error");
        }
        finally
        {
            BtnCalculateParts.IsEnabled = true;
        }
    }

    private void CheckExtractReady()
    {
        BtnExtract.IsEnabled = !string.IsNullOrEmpty(_sourceHash) 
            && !string.IsNullOrEmpty(TxtExtractOutput.Text)
            && Parts.Count > 0;
    }

    // ─── Extract ──────────────────────────────────────────────────────────────

    private async void BtnExtract_Click(object sender, RoutedEventArgs e)
    {
        var selectedParts = Parts.Where(p => p.IsSelected).ToList();
        if (selectedParts.Count == 0)
        {
            MessageBox.Show("Please select at least one part to extract.");
            return;
        }

        string output = TxtExtractOutput.Text;
        string preparedPath = _preparedSourcePath!;
        
        foreach (var part in selectedParts)
        {
            string chunkFileName = $"{Path.GetFileName(preparedPath)}.part{part.Index}.oppk";
            string chunkPath = Path.Combine(output, chunkFileName);
            if (File.Exists(chunkPath))
            {
                var result = MessageBox.Show($"'{chunkFileName}' already exists. Overwrite?", "Overwrite?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.No) return;
            }
        }

        BtnExtract.IsEnabled = false;
        BtnCalculateParts.IsEnabled = false;
        
        long chunkSizeBytes = GetChunkSizeBytes(out long sizeVal, out string sizeUnit);
        string hash = _sourceHash!;
        var fi = new FileInfo(preparedPath);

        Log("─────────────────────────────────");
        Log($"Extracting {selectedParts.Count} part(s) → {output}");

        try
        {
            int partsDone = 0;
            foreach (var part in selectedParts)
            {
                long partActualSize = Math.Min(chunkSizeBytes, fi.Length - (long)part.Index * chunkSizeBytes);
                
                Log($"→ Part {part.Index}: {FormatBytes(partActualSize)}...");
                SetProgress(partsDone, selectedParts.Count, $"Extracting Part {part.Index} of {selectedParts.Count - 1}",
                    $"Starting part {part.Index}...");

                int capturedIndex = part.Index;
                int capturedDone = partsDone;

                var partProgress = new Progress<long>(bytesWritten =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        double overall = capturedDone + (double)bytesWritten / partActualSize;
                        SetProgress(overall, selectedParts.Count,
                            $"Extracting Part {capturedIndex}",
                            $"Part {capturedIndex}: {FormatBytes(bytesWritten)} / {FormatBytes(partActualSize)}");
                    });
                });

                await Task.Run(() => _extractor.ExtractChunk(preparedPath, part.Index, chunkSizeBytes, output, hash, partProgress));

                partsDone++;
                Log($"✓ Part {part.Index} complete ({FormatBytes(partActualSize)})");
                SetProgress(partsDone, selectedParts.Count, "Extracting Parts",
                    $"{partsDone} / {selectedParts.Count} parts done");
            }

            Log($"All {selectedParts.Count} parts extracted successfully.");
            SetProgress(selectedParts.Count, selectedParts.Count, "Extraction Complete",
                $"{selectedParts.Count} parts written to destination ✓");
            MessageBox.Show("Selected parts extracted successfully to destination.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Extraction Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Log($"ERROR: {ex.Message}");
            ClearProgress("Extraction Failed");
        }
        finally
        {
            BtnExtract.IsEnabled = true;
            BtnCalculateParts.IsEnabled = true;
        }
    }

    // ─── Rebuild Tab ──────────────────────────────────────────────────────────

    private void BtnBrowseChunk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select Part File(s) (.oppk)", Filter = "Oppaku Parts (*.oppk)|*.oppk|All Files (*.*)|*.*", Multiselect = true };
        if (dialog.ShowDialog() == true)
        {
            TxtChunkFile.Text = string.Join(";", dialog.FileNames);
            CheckRebuildReady();
        }
    }

    private void BtnBrowseTargetFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Destination Folder (New File)" };
        if (dialog.ShowDialog() == true)
        {
            TxtRebuildOutput.Text = dialog.FolderName;
            CheckRebuildReady();
        }
    }

    private void BtnBrowseTargetFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select Target File (Append Mode)", Filter = "All Files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
        {
            TxtRebuildOutput.Text = dialog.FileName;
            CheckRebuildReady();
        }
    }

    private void CheckRebuildReady()
    {
        bool ready = !string.IsNullOrEmpty(TxtChunkFile.Text) && !string.IsNullOrEmpty(TxtRebuildOutput.Text);
        BtnRebuild.IsEnabled = ready;
        BtnFinalise.IsEnabled = !string.IsNullOrEmpty(TxtRebuildOutput.Text);
    }

    private async void BtnRebuild_Click(object sender, RoutedEventArgs e)
    {
        BtnRebuild.IsEnabled = false;
        
        string[] chunkFiles = TxtChunkFile.Text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
        string targetLoc = TxtRebuildOutput.Text;

        Log("─────────────────────────────────");
        Log($"Inserting {chunkFiles.Length} part(s) → {targetLoc}");

        SetProgress(0, chunkFiles.Length, "Inserting Parts", "Starting...");

        try
        {
            string newTargetLoc = targetLoc;
            int count = 0;

            foreach (var chunkFile in chunkFiles)
            {
                if (!File.Exists(chunkFile))
                {
                    Log($"✗ File not found: {chunkFile}");
                    continue;
                }

                var fi = new FileInfo(chunkFile);
                // Read actual chunk payload size from header so we can show real byte progress
                long chunkPayloadSize = fi.Length; // fallback
                try
                {
                    using var s = new FileStream(chunkFile, FileMode.Open, FileAccess.Read);
                    using var r = new BinaryReader(s, System.Text.Encoding.UTF8);
                    var meta = ChunkMetadata.ReadFrom(r);
                    chunkPayloadSize = meta.ActualChunkSize;
                }
                catch { /* non-critical, use file length */ }

                Log($"→ Merging '{fi.Name}' ({FormatBytes(chunkPayloadSize)})...");
                SetProgress(0, chunkPayloadSize, $"Merging Part {count + 1} / {chunkFiles.Length}",
                    $"0 B / {FormatBytes(chunkPayloadSize)} written...");

                string captured = chunkFile;
                long capturedSize = chunkPayloadSize;

                var partProgress = new Progress<long>(bytesWritten =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        SetProgress(bytesWritten, capturedSize,
                            $"Merging Part {count + 1} / {chunkFiles.Length}",
                            $"{FormatBytes(bytesWritten)} / {FormatBytes(capturedSize)} written");
                    });
                });

                await Task.Run(() => newTargetLoc = _rebuilder.InsertChunk(captured, newTargetLoc, partProgress));

                count++;
                Log($"✓ '{fi.Name}' merged ({FormatBytes(chunkPayloadSize)}).");
                SetProgress(count, chunkFiles.Length, "Inserting Parts",
                    $"{count} / {chunkFiles.Length} parts inserted");
            }

            TxtRebuildOutput.Text = newTargetLoc;
            
            var rebuildState = _rebuilder.GetProgress(newTargetLoc);
            bool autoFinalise = false;
            if (rebuildState != null)
            {
                var missing = Enumerable.Range(0, rebuildState.Total).Except(rebuildState.Received).ToList();
                Log($"Status: {rebuildState.Received.Count} / {rebuildState.Total} parts in file.");
                if (missing.Count > 0)
                {
                    Log($"Missing parts: {string.Join(", ", missing)}");
                }
                else
                {
                    Log("All parts inserted!");
                    autoFinalise = true;
                }
            }

            SetProgress(chunkFiles.Length, chunkFiles.Length, "Insertion Complete",
                $"{count} part(s) written ✓");

            if (autoFinalise)
            {
                Log("Auto-starting finalisation...");
                BtnFinalise_Click(sender, e);
            }
            else
            {
                MessageBox.Show($"Successfully added {count} part(s).", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Rebuild Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Log($"ERROR: {ex.Message}");
            ClearProgress("Insertion Failed");
        }
        finally
        {
            BtnRebuild.IsEnabled = true;
        }
    }

    private async void BtnFinalise_Click(object sender, RoutedEventArgs e)
    {
        string[] chunkFiles = TxtChunkFile.Text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
        string? firstChunk = chunkFiles.FirstOrDefault();

        if (string.IsNullOrEmpty(firstChunk) || !File.Exists(firstChunk)) 
        {
            MessageBox.Show("Please select at least one valid part file (.oppk) so we can read the master hash.", "Error");
            return;
        }

        string chunkFile = firstChunk;
        string targetFile = TxtRebuildOutput.Text;

        BtnFinalise.IsEnabled = false;

        Log("─────────────────────────────────");
        Log($"Finalising: {targetFile}");
        SetIndeterminate("Finalising Rebuild", "Reading master hash from chunk header...");

        try
        {
            string sourceHash = "";
            await Task.Run(() =>
            {
                using var stream = new FileStream(chunkFile, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8);
                var meta = ChunkMetadata.ReadFrom(reader);
                sourceHash = meta.SourceFileHash;
            });

            Log($"Master hash: {sourceHash}");

            // Get content size from embedded progress (excludes the 4KB metadata zone)
            var embedState = _rebuilder.GetProgress(targetFile);
            long contentSize = embedState?.ContentSize ?? (new FileInfo(targetFile).Length - Oppaku.Core.Services.SparseFileHelper.MetadataReserve);
            long displaySize = Math.Max(contentSize, 0);

            Log($"Verifying file hash ({FormatBytes(displaySize)})...");
            SetProgress(0, displaySize, "Verifying Hash", $"0 B / {FormatBytes(displaySize)}");

            bool success = false;
            string errorMsg = "";

            var finalProgress = new Progress<long>(bytesHashed =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    SetProgress(bytesHashed, displaySize, "Verifying Hash",
                        $"{FormatBytes(bytesHashed)} / {FormatBytes(displaySize)} verified");
                });
            });

            await Task.Run(() =>
            {
                try {
                    _rebuilder.Finalise(targetFile, sourceHash, finalProgress);
                    success = true;
                } catch (Exception inner) {
                    errorMsg = inner.Message;
                }
            });

            if (success)
            {
                Log("✓ Hash matches — file is intact!");
                
                if (FolderPacker.IsPackedFolder(targetFile))
                {
                    string destDir = targetFile.EndsWith(".oppaku-dir") 
                        ? targetFile.Substring(0, targetFile.Length - 11) 
                        : targetFile + "_extracted";
                    
                    SetProgress(0, displaySize, "Unpacking Folder", "Unpacking with zero-space hole-punching...");
                    var unpackProgress = new Progress<long>(bytes =>
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            SetProgress(bytes, displaySize, "Unpacking Folder",
                                $"{FormatBytes(bytes)} / {FormatBytes(displaySize)} unpacked");
                        });
                    });

                    await Task.Run(() => FolderPacker.Unpack(targetFile, destDir, unpackProgress));
                    Log($"✓ Extracted folder to {destDir}");
                }

                SetProgress(displaySize, displaySize, "Finalisation Complete!", "File integrity verified ✓");
                MessageBox.Show("Rebuild successful! The file is completely intact.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Log($"✗ Finalisation failed: {errorMsg}");
                var failState = _rebuilder.GetProgress(targetFile);
                if (failState != null)
                {
                    var missing = Enumerable.Range(0, failState.Total).Except(failState.Received).ToList();
                    if (missing.Count > 0)
                        Log($"Missing parts: {string.Join(", ", missing)}");
                }
                ClearProgress("Finalisation Failed");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Finalisation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Log($"ERROR: {ex.Message}");
            ClearProgress("Finalisation Failed");
        }
        finally
        {
            BtnFinalise.IsEnabled = true;
        }
    }
}
