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

    public MainWindow()
    {
        InitializeComponent();
        Parts = new ObservableCollection<PartViewModel>();
        LstParts.ItemsSource = Parts;
        TxtGlobalMessage.Text = "Please calculate parts first.";
    }

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
        TxtGlobalMessage.Text = "Please calculate parts first.";
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

    private async void BtnCalculateParts_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TxtSourceFile.Text)) return;
        
        BtnCalculateParts.IsEnabled = false;
        Parts.Clear();
        
        TxtGlobalTitle.Text = "Calculating Parts";
        TxtGlobalMessage.Text = "Preparing file/folder and computing whole-file SHA-256 hash...\nThis may take a while for large files.";
        PbGlobalProgress.IsIndeterminate = true;
        
        try
        {
            string sourcePath = TxtSourceFile.Text;
            
            await Task.Run(() =>
            {
                if (Directory.Exists(sourcePath))
                {
                    string zipPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(sourcePath) + ".oppaku.zip");
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    ZipFile.CreateFromDirectory(sourcePath, zipPath, CompressionLevel.Fastest, false);
                    _preparedSourcePath = zipPath;
                }
                else
                {
                    _preparedSourcePath = sourcePath;
                }

                _sourceHash = _extractor.ComputeSourceFileHash(_preparedSourcePath);
            });
            
            var fi = new FileInfo(_preparedSourcePath!);
            long chunkSizeBytes = GetChunkSizeBytes(out long value, out string unit);
            int totalChunks = (int)Math.Ceiling((double)fi.Length / chunkSizeBytes);
            
            for (int i = 0; i < totalChunks; i++)
            {
                Parts.Add(new PartViewModel { Index = i, DisplayName = $"Part {i} ({value} {unit})", IsSelected = false });
            }

            TxtGlobalTitle.Text = "Calculation Complete";
            TxtGlobalMessage.Text = $"Hash: {_sourceHash}\nTotal Parts: {totalChunks}. Select parts to extract.";
            CheckExtractReady();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtGlobalTitle.Text = "Error";
            TxtGlobalMessage.Text = "Error preparing source.";
        }
        finally
        {
            BtnCalculateParts.IsEnabled = true;
            PbGlobalProgress.IsIndeterminate = false;
        }
    }

    private void CheckExtractReady()
    {
        BtnExtract.IsEnabled = !string.IsNullOrEmpty(_sourceHash) 
            && !string.IsNullOrEmpty(TxtExtractOutput.Text)
            && Parts.Count > 0;
    }

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
        
        // Check for overwrite before locking UI
        foreach (var part in selectedParts)
        {
            string chunkFileName = $"{Path.GetFileName(preparedPath)}.part{part.Index}.oppk";
            string chunkPath = Path.Combine(output, chunkFileName);
            if (File.Exists(chunkPath))
            {
                var result = MessageBox.Show($"File '{chunkFileName}' already exists in destination. Do you want to overwrite it?", "Overwrite?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.No) return;
            }
        }

        BtnExtract.IsEnabled = false;
        BtnCalculateParts.IsEnabled = false;
        
        long chunkSizeBytes = GetChunkSizeBytes(out _, out _);
        string hash = _sourceHash!;

        TxtGlobalTitle.Text = "Extracting Parts";
        TxtGlobalMessage.Text = $"Oppaku is preparing to split {selectedParts.Count} parts from your source file...";
        PbGlobalProgress.Maximum = selectedParts.Count;
        PbGlobalProgress.Value = 0;
        PbGlobalProgress.IsIndeterminate = false;

        try
        {
            await Task.Run(() =>
            {
                int count = 0;
                foreach (var part in selectedParts)
                {
                    Dispatcher.Invoke(() => TxtGlobalMessage.Text = $"Oppaku is splitting part {part.Index} from '{Path.GetFileName(preparedPath)}'...");
                    _extractor.ExtractChunk(preparedPath, part.Index, chunkSizeBytes, output, hash);
                    count++;
                    Dispatcher.Invoke(() => PbGlobalProgress.Value = count);
                }
            });
            
            TxtGlobalTitle.Text = "Extraction Complete";
            TxtGlobalMessage.Text = "Selected parts written.";
            MessageBox.Show("Selected parts extracted successfully to destination.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Extraction Error", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtGlobalTitle.Text = "Extraction Failed";
            TxtGlobalMessage.Text = ex.Message;
        }
        finally
        {
            BtnExtract.IsEnabled = true;
            BtnCalculateParts.IsEnabled = true;
        }
    }

    // --- REBUILD TAB ---
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

        TxtGlobalTitle.Text = "Inserting Parts";
        TxtGlobalMessage.Text = $"Oppaku is preparing to insert {chunkFiles.Length} parts into target file...";
        PbGlobalProgress.IsIndeterminate = false;
        PbGlobalProgress.Maximum = chunkFiles.Length;
        PbGlobalProgress.Value = 0;

        try
        {
            string newTargetLoc = targetLoc;
            await Task.Run(() =>
            {
                int count = 0;
                foreach (var chunkFile in chunkFiles)
                {
                    Dispatcher.Invoke(() => TxtGlobalMessage.Text = $"Oppaku is merging '{Path.GetFileName(chunkFile)}' into target...");
                    if (File.Exists(chunkFile))
                    {
                        newTargetLoc = _rebuilder.InsertChunk(chunkFile, newTargetLoc);
                    }
                    count++;
                    Dispatcher.Invoke(() => PbGlobalProgress.Value = count);
                }
            });
            
            TxtRebuildOutput.Text = newTargetLoc;
            
            // Read progress to report back
            string progressPath = $"{newTargetLoc}.progress";
            string statusMsg = $"Successfully inserted {chunkFiles.Length} part(s).";
            if (File.Exists(progressPath))
            {
                var progress = System.Text.Json.JsonSerializer.Deserialize<RebuildProgress>(File.ReadAllText(progressPath));
                if (progress != null)
                {
                    var missing = Enumerable.Range(0, progress.Total).Except(progress.Received).ToList();
                    statusMsg += $"\nMerged {progress.Received.Count} / {progress.Total} parts total.";
                    if (missing.Count > 0)
                        statusMsg += $"\nMissing parts: {string.Join(", ", missing)}";
                    else
                        statusMsg += "\nAll parts merged! You can now Finalise.";
                }
            }
            
            TxtGlobalTitle.Text = "Insertion Complete";
            TxtGlobalMessage.Text = statusMsg;
            MessageBox.Show($"Successfully added {chunkFiles.Length} part(s).", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Rebuild Error", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtGlobalTitle.Text = "Insertion Failed";
            TxtGlobalMessage.Text = ex.Message;
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
            MessageBox.Show("Please select at least one valid part file (.oppk) so we can read the master hash from its header.", "Error");
            return;
        }

        string chunkFile = firstChunk;
        string targetFile = TxtRebuildOutput.Text;

        BtnFinalise.IsEnabled = false;

        TxtGlobalTitle.Text = "Finalising Rebuild";
        TxtGlobalMessage.Text = "Finalising and verifying full hash against original source.\nThis may take a while for large files.";
        PbGlobalProgress.IsIndeterminate = true;

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

            bool success = false;
            string errorMsg = "";
            await Task.Run(() =>
            {
                try {
                    _rebuilder.Finalise(targetFile, sourceHash);
                    success = true;
                } catch (Exception inner) {
                    errorMsg = inner.Message;
                }
            });

            if (success)
            {
                TxtGlobalTitle.Text = "Finalisation Complete!";
                TxtGlobalMessage.Text = $"File hash matches original source:\n{sourceHash}";
                MessageBox.Show("Rebuild successful! The file is completely intact.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
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
                
                TxtGlobalTitle.Text = "Finalisation Failed";
                TxtGlobalMessage.Text = $"{errorMsg}{extraInfo}";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Finalisation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtGlobalTitle.Text = "Finalisation Failed";
            TxtGlobalMessage.Text = ex.Message;
        }
        finally
        {
            BtnFinalise.IsEnabled = true;
            PbGlobalProgress.IsIndeterminate = false;
        }
    }
}
