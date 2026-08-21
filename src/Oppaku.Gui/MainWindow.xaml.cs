using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Oppaku.Core.Models;
using Oppaku.Core.Services;
using Oppaku.Gui.Services;
using Oppaku.Gui.Themes;

namespace Oppaku.Gui;

// --- Models ---
public class FsItem : INotifyPropertyChanged
{
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    
    public string SizeDisplay => IsDirectory ? "--" : FormatBytes(Size);
    public string TypeDisplay => IsDirectory ? "File Folder" : Path.GetExtension(FullPath).ToUpperInvariant() + " File";
    public string DateDisplay => LastModified.ToString("g");
    
    public ImageSource? Icon => SystemIconHelper.GetIcon(FullPath, IsDirectory);

    public ObservableCollection<FsItem> Children { get; set; } = new();
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); if (value) LoadChildren(); }
    }
    
    public Action<FsItem>? LoadChildrenAction { get; set; }
    private bool _hasLoadedChildren;

    public void LoadChildren()
    {
        if (_hasLoadedChildren || !IsDirectory) return;
        _hasLoadedChildren = true;
        Children.Clear();
        LoadChildrenAction?.Invoke(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.00} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0.00} KB";
        return $"{bytes} B";
    }
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FsItem> _treeRoot = new();
    private readonly ObservableCollection<FsItem> _currentFiles = new();
    private readonly ObservableCollection<string> _logLines = new();
    
    private readonly Extractor _extractor = new();
    private readonly Rebuilder _rebuilder = new();
    private bool _isLogExpanded = false;
    private FileSystemWatcher? _dirWatcher;

    private CancellationTokenSource? _activeTaskCts;
    private bool IsTaskRunning => _activeTaskCts != null && !_activeTaskCts.IsCancellationRequested;

    public MainWindow()
    {
        ThemeManager.Initialize();
        InitializeComponent();
        UpdateThemeMenuChecks(ThemeManager.CurrentTheme);
        ThemeManager.ThemeChanged += UpdateThemeMenuChecks;

        TvDirs.ItemsSource = _treeRoot;
        LvFiles.ItemsSource = _currentFiles;
        LstLog.ItemsSource = _logLines;

        EnableSmoothScrolling(TvDirs);
        EnableSmoothScrolling(LvFiles);
        EnableSmoothScrolling(LstLog);
        
        LoadRootNodes();
        Log("Oppaku Archive Manager ready.");
        UpdateStatus();
        
        this.Closing += MainWindow_Closing;
    }

    private void BtnThemeSelector_Click(object sender, RoutedEventArgs e)
    {
        if (BtnThemeSelector.ContextMenu != null)
        {
            BtnThemeSelector.ContextMenu.PlacementTarget = BtnThemeSelector;
            BtnThemeSelector.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            BtnThemeSelector.ContextMenu.IsOpen = true;
        }
    }

    private void MenuTheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string tagStr)
        {
            if (Enum.TryParse<ThemePreset>(tagStr, out var preset))
            {
                ThemeManager.ApplyTheme(preset);
                Log($"Theme set to: {preset}");
            }
        }
    }

    private void UpdateThemeMenuChecks(ThemePreset current)
    {
        if (MenuThemeSandstone != null) MenuThemeSandstone.Header = (current == ThemePreset.SandstoneLight ? "✓ " : "   ") + "🏜️ Sandstone Light";
        if (MenuThemeEspresso != null) MenuThemeEspresso.Header  = (current == ThemePreset.EspressoDark ? "✓ " : "   ") + "☕ Espresso Dark";
        if (MenuThemeObsidian != null) MenuThemeObsidian.Header  = (current == ThemePreset.ObsidianSlate ? "✓ " : "   ") + "🌌 Obsidian Slate";
    }

    private bool ConfirmAndCancelActiveTask()
    {
        if (!IsTaskRunning) return true;

        var result = MessageBox.Show(
            "A task is currently in progress.\n\nDo you really want to cancel the running process and revert incomplete operations?", 
            "Task in Progress", 
            MessageBoxButton.YesNo, 
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            Log("⚠️ Cancellation requested by user...");
            _activeTaskCts?.Cancel();
            return true;
        }

        return false;
    }

    private void BtnCancelGlobalTask_Click(object sender, RoutedEventArgs e)
    {
        ConfirmAndCancelActiveTask();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (IsTaskRunning)
        {
            var result = MessageBox.Show(
                "Oppaku is currently performing a task.\n\nAre you sure you want to cancel the task and quit?", 
                "Task in Progress", 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Warning);
                
            if (result == MessageBoxResult.Yes)
            {
                _activeTaskCts?.Cancel();
            }
            else
            {
                e.Cancel = true;
            }
        }
    }

    // --- File System Navigation ---

    private void LoadRootNodes()
    {
        _treeRoot.Clear();
        
        void AddFolder(string name, string path)
        {
            if (Directory.Exists(path))
            {
                var item = new FsItem
                {
                    Name = name,
                    FullPath = path,
                    IsDirectory = true,
                    LoadChildrenAction = PopulateChildren
                };
                item.Children.Add(new FsItem { Name = "..." });
                _treeRoot.Add(item);
            }
        }

        var thisPc = new FsItem
        {
            Name = "This PC",
            FullPath = "This PC",
            IsDirectory = true
        };
        _treeRoot.Add(thisPc);

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddFolder("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        AddFolder("Downloads", Path.Combine(userProfile, "Downloads"));
        AddFolder("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddFolder("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddFolder("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        AddFolder("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            string label = string.IsNullOrEmpty(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
            AddFolder($"{label} ({drive.Name})", drive.Name);
        }
    }

    private void PopulateChildren(FsItem node)
    {
        try
        {
            var dirs = Directory.GetDirectories(node.FullPath);
            foreach (var d in dirs)
            {
                var dirInfo = new DirectoryInfo(d);
                if (dirInfo.Attributes.HasFlag(FileAttributes.Hidden) || dirInfo.Attributes.HasFlag(FileAttributes.System)) continue;
                
                var child = new FsItem
                {
                    Name = dirInfo.Name,
                    FullPath = dirInfo.FullName,
                    IsDirectory = true,
                    LoadChildrenAction = PopulateChildren
                };
                child.Children.Add(new FsItem { Name = "..." });
                node.Children.Add(child);
            }
        }
        catch { /* Ignore access denied */ }
    }

    private void TvDirs_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FsItem node)
        {
            NavigateTo(node.FullPath);
        }
    }

    private void TvItem_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FsItem node)
        {
            NavigateTo(node.FullPath);
        }
    }

    private void NavigateTo(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        
        if (_dirWatcher != null)
        {
            _dirWatcher.EnableRaisingEvents = false;
            _dirWatcher.Dispose();
            _dirWatcher = null;
        }
        
        TxtCurrentPath.Text = path;
        _currentFiles.Clear();
        
        if (path == "This PC")
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                string label = string.IsNullOrEmpty(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                _currentFiles.Add(new FsItem
                {
                    Name = $"{label} ({drive.Name})",
                    FullPath = drive.Name,
                    IsDirectory = true,
                    LastModified = DateTime.Now
                });
            }
            UpdateStatus();
            return;
        }

        try
        {
            var di = new DirectoryInfo(path);
            foreach (var d in di.GetDirectories().Where(x => !x.Attributes.HasFlag(FileAttributes.Hidden)))
            {
                _currentFiles.Add(new FsItem
                {
                    Name = d.Name, FullPath = d.FullName, IsDirectory = true, LastModified = d.LastWriteTime
                });
            }
            foreach (var f in di.GetFiles().Where(x => !x.Attributes.HasFlag(FileAttributes.Hidden)))
            {
                _currentFiles.Add(new FsItem
                {
                    Name = f.Name, FullPath = f.FullName, IsDirectory = false, Size = f.Length, LastModified = f.LastWriteTime
                });
            }
        }
        catch (Exception ex)
        {
            Log($"Cannot access {path}: {ex.Message}");
        }
        UpdateStatus();
        
        try
        {
            if (Directory.Exists(path))
            {
                _dirWatcher = new FileSystemWatcher(path);
                _dirWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
                _dirWatcher.Created += (s, e) => Dispatcher.InvokeAsync(RefreshCurrentDirectory);
                _dirWatcher.Deleted += (s, e) => Dispatcher.InvokeAsync(RefreshCurrentDirectory);
                _dirWatcher.Renamed += (s, e) => Dispatcher.InvokeAsync(RefreshCurrentDirectory);
                _dirWatcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex)
        {
            Log($"Could not start directory watcher: {ex.Message}");
        }
    }

    private void RefreshCurrentDirectory()
    {
        string path = TxtCurrentPath.Text;
        if (string.IsNullOrEmpty(path) || path == "This PC") return;
        
        var selectedNames = LvFiles.SelectedItems.Cast<FsItem>().Select(x => x.Name).ToList();
        
        NavigateTo(path);
        
        if (selectedNames.Count > 0)
        {
            foreach (var item in _currentFiles)
            {
                if (selectedNames.Contains(item.Name))
                {
                    LvFiles.SelectedItems.Add(item);
                }
            }
        }
    }

    private void BtnNavBack_Click(object sender, RoutedEventArgs e)
    {
        string current = TxtCurrentPath.Text;
        if (current == "This PC" || string.IsNullOrEmpty(current)) return;
        var parent = Directory.GetParent(current);
        if (parent != null) NavigateTo(parent.FullName);
        else NavigateTo("This PC");
    }

    private void TxtCurrentPath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            string path = TxtCurrentPath.Text;
            if (path == "This PC" || Directory.Exists(path))
            {
                NavigateTo(path);
            }
            else
            {
                MessageBox.Show($"Directory not found: {path}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void LvFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LvFiles.SelectedItem is FsItem item)
        {
            if (item.IsDirectory) NavigateTo(item.FullPath);
            else OpenFile(item.FullPath);
        }
    }

    private void LvFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();

        // If the Insert Chunks panel is open, mirror the selection of .oppk files exactly
        if (_activeRebuildFilesTxt != null)
        {
            var oppkFiles = LvFiles.SelectedItems
                .Cast<FsItem>()
                .Where(f => !f.IsDirectory && f.FullPath.EndsWith(".oppk", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.FullPath)
                .ToList();
                
            Log($"Debug: SelectionChanged fired. Total selected: {LvFiles.SelectedItems.Count}, oppk found: {oppkFiles.Count}");

            _activeRebuildFilesTxt.Text = string.Join(";", oppkFiles);
            Log($"Debug: Updated textbox to: {_activeRebuildFilesTxt.Text}");
        }
    }

    private void MenuOpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        string path = TxtCurrentPath.Text;
        if (LvFiles.SelectedItem is FsItem item) path = item.FullPath;
        if (Directory.Exists(path) || File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    // Tracks the files TextBox inside the active Insert Chunks dialog, if open
    private TextBox? _activeRebuildFilesTxt;

    private void OpenFile(string path)
    {
        if (path.EndsWith(".oppaku-archive", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".oppk", StringComparison.OrdinalIgnoreCase))
        {
            Log($"Selected: {Path.GetFileName(path)}");
        }
        else
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log($"Failed to open file: {ex.Message}"); }
        }
    }

    // --- Status & Logging ---

    private void UpdateStatus()
    {
        int count = LvFiles.SelectedItems.Count;
        long totalSize = 0;
        foreach (FsItem item in LvFiles.SelectedItems)
        {
            if (!item.IsDirectory) totalSize += item.Size;
        }
        TxtStatusLeft.Text = count > 0 ? $"{count} item(s) selected" : "Ready";
        TxtStatusRight.Text = count > 0 ? FormatBytes(totalSize) : "";
    }

    private void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Dispatcher.Invoke(() => {
            _logLines.Add(line);
            LstLog.ScrollIntoView(line);
        });
    }

    private void BtnToggleLog_Click(object sender, RoutedEventArgs e)
    {
        _isLogExpanded = !_isLogExpanded;
        LogRow.Height = _isLogExpanded ? new GridLength(140) : new GridLength(0);
        BtnToggleLog.Content = _isLogExpanded ? "Log \u25BC" : "Log \u25B2";
    }

    private void SetProgress(double value, double max, string title, string detail = "")
    {
        TxtGlobalTitle.Text = title;
        PbGlobalProgress.IsIndeterminate = false;
        PbGlobalProgress.Minimum = 0;
        PbGlobalProgress.Maximum = Math.Max(max, 1);
        PbGlobalProgress.Value = Math.Min(Math.Max(value, 0), PbGlobalProgress.Maximum);
        TxtProgressPercent.Text = max > 0 ? $"{value / max * 100:0.0}%" : "";
        TxtProgressDetail.Text = detail;
        if (BtnCancelGlobalTask != null) BtnCancelGlobalTask.Visibility = IsTaskRunning ? Visibility.Visible : Visibility.Collapsed;
        if (!_isLogExpanded) BtnToggleLog_Click(this, new RoutedEventArgs());
    }

    private void SetIndeterminate(string title, string detail = "")
    {
        TxtGlobalTitle.Text = title;
        PbGlobalProgress.IsIndeterminate = true;
        TxtProgressPercent.Text = "";
        TxtProgressDetail.Text = detail;
        if (BtnCancelGlobalTask != null) BtnCancelGlobalTask.Visibility = IsTaskRunning ? Visibility.Visible : Visibility.Collapsed;
        if (!_isLogExpanded) BtnToggleLog_Click(this, new RoutedEventArgs());
    }

    private void ClearProgress(string title = "Ready")
    {
        TxtGlobalTitle.Text = title;
        PbGlobalProgress.IsIndeterminate = false;
        PbGlobalProgress.Value = 0;
        TxtProgressPercent.Text = "";
        TxtProgressDetail.Text = "";
        if (BtnCancelGlobalTask != null) BtnCancelGlobalTask.Visibility = Visibility.Collapsed;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.00} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0.00} KB";
        return $"{bytes} B";
    }

    // --- Dialog System ---

    private void ShowDialog(UIElement content)
    {
        ActionPanel.Content = content;
    }

    private void CloseDialog()
    {
        if (IsTaskRunning)
        {
            ConfirmAndCancelActiveTask();
            return;
        }
        ActionPanel.Content = null;
        _activeRebuildFilesTxt = null;
    }

    private static TextBlock CreateHeaderLabel(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,15) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TxtPrimary");
        return tb;
    }

    private static TextBlock CreateLabel(string text, Thickness? margin = null)
    {
        var tb = new TextBlock { Text = text, Margin = margin ?? new Thickness(0,0,0,5) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TxtPrimary");
        return tb;
    }

    private static TextBlock CreateSecondaryLabel(string text, Thickness? margin = null, bool wrap = false)
    {
        var tb = new TextBlock { Text = text, Margin = margin ?? new Thickness(0,0,0,10), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TxtSecondary");
        return tb;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static void EnableSmoothScrolling(ItemsControl control)
    {
        VirtualizingPanel.SetScrollUnit(control, ScrollUnit.Pixel);
        VirtualizingPanel.SetVirtualizationMode(control, VirtualizationMode.Recycling);
        ScrollViewer.SetIsDeferredScrollingEnabled(control, false);
        
        control.PreviewMouseWheel += (s, e) =>
        {
            var scroller = FindVisualChild<ScrollViewer>(control);
            if (scroller != null)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - (e.Delta * 0.4));
                }
                else
                {
                    scroller.ScrollToVerticalOffset(scroller.VerticalOffset - (e.Delta * 0.4));
                }
                e.Handled = true;
            }
        };
    }

    private static Grid CreateDialogContainer(UIElement fixedTop, UIElement? flexibleMiddle, UIElement fixedBottom)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Fixed Top (Header + Form inputs)
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Flexible Middle (e.g. Scrollable parts list)
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Fixed Bottom (Action Buttons)

        Grid.SetRow((FrameworkElement)fixedTop, 0);
        grid.Children.Add(fixedTop);

        if (flexibleMiddle != null)
        {
            Grid.SetRow((FrameworkElement)flexibleMiddle, 1);
            grid.Children.Add(flexibleMiddle);
        }

        Grid.SetRow((FrameworkElement)fixedBottom, 2);
        grid.Children.Add(fixedBottom);

        return grid;
    }

    private (TextBox TextBox, FrameworkElement Container) CreateBrowseField(string defaultPath, bool isFile = false, string filter = "")
    {
        var dock = new DockPanel { Margin = new Thickness(0,0,0,10) };
        var txt = new TextBox { Text = defaultPath, Margin = new Thickness(0,0,5,0) };
        var btn = new Button { Content = "Browse", Style = (Style)FindResource("GhostBtn"), Padding = new Thickness(10,2,10,2) };
        btn.Click += (s, e) => {
            if (isFile)
            {
                var d = new Microsoft.Win32.OpenFileDialog { Filter = filter };
                if (d.ShowDialog() == true) txt.Text = d.FileName;
            }
            else
            {
                var d = new Microsoft.Win32.OpenFolderDialog();
                if (d.ShowDialog() == true) txt.Text = d.FolderName;
            }
        };
        DockPanel.SetDock(btn, Dock.Right);
        dock.Children.Add(btn);
        dock.Children.Add(txt);
        return (txt, dock);
    }

    // --- Actions ---

    // 1. EXTRACT CHUNKS (V2 functionality)
    private void BtnToolExtractChunks_Click(object sender, RoutedEventArgs e)
    {
        if (IsTaskRunning)
        {
            MessageBox.Show("A task is currently running. Please wait for it to finish or cancel it first.", "Task in Progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = LvFiles.SelectedItems.Cast<FsItem>().ToList();
        if (selected.Count == 0 && !string.IsNullOrEmpty(TxtCurrentPath.Text) && TxtCurrentPath.Text != "This PC")
        {
            selected.Add(new FsItem { FullPath = TxtCurrentPath.Text, Name = Path.GetFileName(TxtCurrentPath.Text) });
        }
        
        if (selected.Count != 1)
        {
            MessageBox.Show("Please select exactly one file or folder to split into chunks.");
            return;
        }

        string sourcePath = selected[0].FullPath;
        
        var fixedTop = new StackPanel();
        fixedTop.Children.Add(CreateHeaderLabel("Extract Chunks"));
        fixedTop.Children.Add(CreateSecondaryLabel($"Source: {sourcePath}", wrap: true));
        
        fixedTop.Children.Add(CreateLabel("Output Directory:"));
        var outField = CreateBrowseField("", false);
        fixedTop.Children.Add(outField.Container);
        
        fixedTop.Children.Add(CreateLabel("Chunk Size:"));
        var sizePanel = new DockPanel { Margin = new Thickness(0,0,0,10) };
        var cboUnit = new ComboBox { Width = 60 };
        cboUnit.Items.Add("KB");
        cboUnit.Items.Add("MB");
        cboUnit.Items.Add("GB");
        cboUnit.SelectedIndex = 1;
        DockPanel.SetDock(cboUnit, Dock.Right);
        var txtSize = new TextBox { Text = "100", Margin = new Thickness(0,0,5,0) };
        sizePanel.Children.Add(cboUnit);
        sizePanel.Children.Add(txtSize);
        fixedTop.Children.Add(sizePanel);

        var lstParts = new ListBox {
            Margin = new Thickness(0,0,0,10),
            Visibility = Visibility.Collapsed,
            Background = (System.Windows.Media.Brush)FindResource("BgInput"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };
        ScrollViewer.SetVerticalScrollBarVisibility(lstParts, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(lstParts, ScrollBarVisibility.Disabled);
        EnableSmoothScrolling(lstParts);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,5,0,0) };
        
        var btnCancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostBtn"), Margin = new Thickness(0,0,10,0) };
        
        Stream? currentStream = null;
        string? currentHash = null;
        long currentTotalLength = 0;
        int currentTotalParts = 0;

        btnCancel.Click += (s,ev) => {
            if (IsTaskRunning)
            {
                ConfirmAndCancelActiveTask();
                return;
            }
            currentStream?.Dispose();
            CloseDialog();
        };

        var btnExtract = new Button { Content = "Extract", Style = (Style)FindResource("AccentBtn"), IsEnabled = false };
        var btnCalc = new Button { Content = "Calculate Parts", Style = (Style)FindResource("GhostBtn"), Margin = new Thickness(0,0,10,0) };

        btnCalc.Click += async (s,ev) => {
            if (IsTaskRunning) { MessageBox.Show("A task is already running."); return; }
            if (!long.TryParse(txtSize.Text, out long size)) return;
            
            long multiplier = 1024 * 1024;
            if (cboUnit.SelectedItem?.ToString() == "KB") multiplier = 1024;
            else if (cboUnit.SelectedItem?.ToString() == "GB") multiplier = 1024 * 1024 * 1024;
            long chunkSize = size * multiplier;
            
            btnCalc.IsEnabled = false;
            lstParts.Items.Clear();
            lstParts.Visibility = Visibility.Visible;
            
            Log("─────────────────────────────────");
            _activeTaskCts = new CancellationTokenSource();
            var ct = _activeTaskCts.Token;

            try
            {
                currentStream?.Dispose();
                
                if (Directory.Exists(sourcePath))
                {
                    SetIndeterminate("Scanning Folder", "Building virtual stream...");
                    currentStream = new VirtualFolderStream(sourcePath);
                }
                else
                {
                    currentStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }

                SetProgress(0, currentStream.Length, "Computing Hash", "Scanning files...");
                var hashProgress = new Progress<long>(b => {
                    Dispatcher.Invoke(() => SetProgress(b, currentStream.Length, "Computing Hash", $"Scanned: {FormatBytes(b)} / {FormatBytes(currentStream.Length)}"));
                });
                currentHash = await Task.Run(() => _extractor.ComputeSourceStreamHash(currentStream, hashProgress, ct), ct);
                currentTotalLength = currentStream.Length;
                currentTotalParts = (int)Math.Ceiling((double)currentTotalLength / chunkSize);
                
                for (int i = 0; i < currentTotalParts; i++)
                {
                    var chk = new CheckBox { Content = $"Part {i} ({FormatBytes(Math.Min(chunkSize, currentTotalLength - (long)i * chunkSize))})", IsChecked = true, Foreground = (System.Windows.Media.Brush)FindResource("TxtPrimary"), Margin = new Thickness(0,2,0,2) };
                    lstParts.Items.Add(chk);
                }

                btnExtract.IsEnabled = true;
                ClearProgress("Calculation Complete");
            }
            catch (OperationCanceledException)
            {
                Log("⚠️ Hash calculation cancelled.");
                ClearProgress("Cancelled");
                currentStream?.Dispose();
                currentStream = null;
                currentHash = null;
            }
            catch (Exception ex)
            {
                Log($"Calculate error: {ex.Message}");
                ClearProgress("Failed");
            }
            finally
            {
                _activeTaskCts?.Dispose();
                _activeTaskCts = null;
                btnCalc.IsEnabled = true;
            }
        };

        btnExtract.Click += async (s,ev) => {
            if (IsTaskRunning) { MessageBox.Show("A task is already running."); return; }
            if (string.IsNullOrEmpty(outField.TextBox.Text) || currentStream == null || currentHash == null) return;
            string outDir = outField.TextBox.Text;
            
            long multiplier = 1024 * 1024;
            if (cboUnit.SelectedItem?.ToString() == "KB") multiplier = 1024;
            else if (cboUnit.SelectedItem?.ToString() == "GB") multiplier = 1024 * 1024 * 1024;
            long chunkSize = long.Parse(txtSize.Text) * multiplier;
            
            var selectedIndices = new List<int>();
            for (int i = 0; i < lstParts.Items.Count; i++)
            {
                if (lstParts.Items[i] is CheckBox chk && chk.IsChecked == true)
                    selectedIndices.Add(i);
            }
            if (selectedIndices.Count == 0) selectedIndices = Enumerable.Range(0, currentTotalParts).ToList();
            
            btnExtract.IsEnabled = false;
            _activeTaskCts = new CancellationTokenSource();
            var ct = _activeTaskCts.Token;
            try
            {
                await DoExtractChunks(currentStream, Path.GetFileName(sourcePath), outDir, chunkSize, currentHash, selectedIndices, ct);
            }
            finally
            {
                _activeTaskCts?.Dispose();
                _activeTaskCts = null;
                btnExtract.IsEnabled = true;
            }
        };
        
        btnPanel.Children.Add(btnCancel);
        btnPanel.Children.Add(btnCalc);
        btnPanel.Children.Add(btnExtract);
        
        ShowDialog(CreateDialogContainer(fixedTop, lstParts, btnPanel));
    }

    private async Task DoExtractChunks(Stream sourceStream, string fileName, string outputDir, long chunkSizeBytes, string hash, List<int> selectedIndices, CancellationToken ct)
    {
        Log("─────────────────────────────────");
        try
        {
            long fileSize = sourceStream.Length;
            
            int totalChunks = (int)Math.Ceiling((double)fileSize / chunkSizeBytes);
            Log($"Hash: {hash}. Total parts: {totalChunks}");
            
            long totalBytesToExtract = selectedIndices.Sum(i => Math.Min(chunkSizeBytes, fileSize - (long)i * chunkSizeBytes));
            if (totalBytesToExtract <= 0) totalBytesToExtract = 1;

            long totalBytesExtractedSoFar = 0;
            int extractedCount = 0;

            foreach (int i in selectedIndices)
            {
                ct.ThrowIfCancellationRequested();
                long partActualSize = Math.Min(chunkSizeBytes, fileSize - (long)i * chunkSizeBytes);
                long baseBytes = totalBytesExtractedSoFar;

                SetProgress(baseBytes, totalBytesToExtract, "Extracting Chunks", $"Writing part {i} ({FormatBytes(baseBytes)} / {FormatBytes(totalBytesToExtract)})...");
                var prog = new Progress<long>(b => {
                    long currentTotal = baseBytes + b;
                    Dispatcher.Invoke(() => SetProgress(
                        currentTotal, 
                        totalBytesToExtract, 
                        "Extracting Chunks", 
                        $"Part {i} ({extractedCount + 1}/{selectedIndices.Count}): {FormatBytes(currentTotal)} / {FormatBytes(totalBytesToExtract)}"
                    ));
                });
                await Task.Run(() => _extractor.ExtractChunk(sourceStream, fileName, i, chunkSizeBytes, outputDir, hash, prog, ct), ct);
                
                totalBytesExtractedSoFar += partActualSize;
                extractedCount++;
                Log($"✓ Part {i} complete ({FormatBytes(partActualSize)})");
            }
            
            ClearProgress("Chunking Complete");
            MessageBox.Show($"Successfully extracted {selectedIndices.Count} chunks ({FormatBytes(totalBytesExtractedSoFar)}).", "Success");
            if (Directory.Exists(TxtCurrentPath.Text)) NavigateTo(TxtCurrentPath.Text);
        }
        catch (OperationCanceledException)
        {
            Log("⚠️ Chunk extraction cancelled. Incomplete chunk removed.");
            ClearProgress("Cancelled");
            MessageBox.Show("Chunk extraction was cancelled. Any partially written chunk was removed, and previously completed chunks were kept.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            if (Directory.Exists(TxtCurrentPath.Text)) NavigateTo(TxtCurrentPath.Text);
        }
        catch (Exception ex)
        {
            Log($"Error chunking: {ex.Message}");
            ClearProgress("Failed");
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 2. REBUILD (Insert Chunks)
    private void BtnToolRebuild_Click(object sender, RoutedEventArgs e)
    {
        if (IsTaskRunning)
        {
            MessageBox.Show("A task is currently running. Please wait for it to finish or cancel it first.", "Task in Progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = LvFiles.SelectedItems.Cast<FsItem>().Where(f => f.FullPath.EndsWith(".oppk", StringComparison.OrdinalIgnoreCase)).ToList();
        OpenRebuildDialog(selected.Select(f => f.FullPath).ToArray());
    }

    private void OpenRebuildDialog(string[] preloadedFiles)
    {
        var fixedTop = new StackPanel();
        fixedTop.Children.Add(CreateHeaderLabel("Insert Chunks"));
        
        fixedTop.Children.Add(CreateLabel("Selected .oppk files:"));
        var txtFiles = new TextBox { Text = string.Join(";", preloadedFiles), Margin = new Thickness(0,0,0,10) };
        fixedTop.Children.Add(txtFiles);
        
        // Register the active TextBox so clicking more .oppk files appends here
        _activeRebuildFilesTxt = txtFiles;
        
        fixedTop.Children.Add(CreateLabel("Target Rebuild File / Directory:"));
        var outField = CreateBrowseField(TxtCurrentPath.Text == "This PC" ? "" : TxtCurrentPath.Text, true, "All Files (*.*)|*.*");
        fixedTop.Children.Add(outField.Container);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,5,0,0) };
        var btnCancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostBtn"), Margin = new Thickness(0,0,10,0) };
        btnCancel.Click += (s,ev) => {
            if (IsTaskRunning)
            {
                ConfirmAndCancelActiveTask();
                return;
            }
            CloseDialog();
        };
        var btnStart = new Button { Content = "Insert", Style = (Style)FindResource("AccentBtn") };
        
        btnStart.Click += async (s,ev) => {
            if (IsTaskRunning) { MessageBox.Show("A task is already running."); return; }
            string filesStr = txtFiles.Text;
            string outDir = outField.TextBox.Text;
            if (string.IsNullOrEmpty(filesStr) || string.IsNullOrEmpty(outDir)) return;
            var files = filesStr.Split(';', StringSplitOptions.RemoveEmptyEntries).ToArray();
            
            btnStart.IsEnabled = false;
            _activeTaskCts = new CancellationTokenSource();
            var ct = _activeTaskCts.Token;
            try
            {
                await DoRebuild(files, outDir, ct);
            }
            finally
            {
                _activeTaskCts?.Dispose();
                _activeTaskCts = null;
                btnStart.IsEnabled = true;
            }
        };
        
        btnPanel.Children.Add(btnCancel);
        btnPanel.Children.Add(btnStart);
        
        ShowDialog(CreateDialogContainer(fixedTop, null, btnPanel));
    }

    private async Task DoRebuild(string[] chunkFiles, string targetDirOrFile, CancellationToken ct)
    {
        Log("─────────────────────────────────");
        try
        {
            var validChunkFiles = chunkFiles.Where(File.Exists).ToArray();
            if (validChunkFiles.Length == 0)
            {
                MessageBox.Show("No valid .oppk chunk files were found.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Calculate total byte payload size across all selected chunks
            long totalBytesToInsert = 0;
            var chunkPayloadSizes = new Dictionary<string, long>();
            foreach (var f in validChunkFiles)
            {
                long size = 0;
                try
                {
                    using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var r = new BinaryReader(s, System.Text.Encoding.UTF8);
                    var m = ChunkMetadata.ReadFrom(r);
                    size = m.ActualChunkSize;
                }
                catch
                {
                    size = new FileInfo(f).Length;
                }
                chunkPayloadSizes[f] = size;
                totalBytesToInsert += size;
            }
            if (totalBytesToInsert <= 0) totalBytesToInsert = 1;

            string newTargetLoc = targetDirOrFile;
            long totalBytesCompletedSoFar = 0;

            for (int i = 0; i < validChunkFiles.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = validChunkFiles[i];
                long currentChunkSize = chunkPayloadSizes[f];
                long baseBytes = totalBytesCompletedSoFar;

                SetProgress(baseBytes, totalBytesToInsert, "Inserting Parts", $"Inserting {Path.GetFileName(f)} ({FormatBytes(baseBytes)} / {FormatBytes(totalBytesToInsert)})...");
                
                var prog = new Progress<long>(bytesInThisChunk => {
                    long currentTotal = baseBytes + bytesInThisChunk;
                    Dispatcher.Invoke(() => SetProgress(
                        currentTotal, 
                        totalBytesToInsert, 
                        "Inserting Parts", 
                        $"Part {i + 1}/{validChunkFiles.Length} ({Path.GetFileName(f)}): {FormatBytes(currentTotal)} / {FormatBytes(totalBytesToInsert)}"
                    ));
                });

                await Task.Run(() => newTargetLoc = _rebuilder.InsertChunk(f, newTargetLoc, prog, ct), ct);
                
                totalBytesCompletedSoFar += currentChunkSize;
                SetProgress(totalBytesCompletedSoFar, totalBytesToInsert, "Inserting Parts", $"{FormatBytes(totalBytesCompletedSoFar)} / {FormatBytes(totalBytesToInsert)}");

                var state = _rebuilder.GetProgress(newTargetLoc);
                if (state != null)
                {
                    var missing = Enumerable.Range(0, state.Total).Except(state.Received).ToList();
                    string missingStr = missing.Count > 0 ? string.Join(", ", missing) : "None";
                    Log($"✓ Inserted {Path.GetFileName(f)} ({FormatBytes(currentChunkSize)}) | Parts required: {missingStr}");
                }
                else
                {
                    Log($"✓ Inserted {Path.GetFileName(f)} ({FormatBytes(currentChunkSize)})");
                }
            }

            ct.ThrowIfCancellationRequested();

            var rebuildState = _rebuilder.GetProgress(newTargetLoc);
            if (rebuildState != null && rebuildState.Received.Count == rebuildState.Total)
            {
                Log("All parts inserted! Auto-starting finalisation...");
                await DoFinalise(validChunkFiles[0], newTargetLoc, ct);
            }
            else
            {
                ClearProgress("Insertion Complete");
                MessageBox.Show($"Inserted {validChunkFiles.Length} chunk(s) ({FormatBytes(totalBytesCompletedSoFar)}). More required.", "Success");
                if (Directory.Exists(TxtCurrentPath.Text)) NavigateTo(TxtCurrentPath.Text);
            }
        }
        catch (OperationCanceledException)
        {
            Log("⚠️ Chunk insertion cancelled. Target sparse file preserved; missing parts remain required.");
            ClearProgress("Cancelled");
            MessageBox.Show("Chunk insertion was cancelled. The target file has been preserved, and any uninserted parts remain required.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            if (Directory.Exists(TxtCurrentPath.Text)) NavigateTo(TxtCurrentPath.Text);
        }
        catch (Exception ex)
        {
            Log($"Error rebuilding: {ex.Message}");
            ClearProgress("Failed");
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DoFinalise(string chunkFile, string targetFile, CancellationToken ct)
    {
        try
        {
            SetIndeterminate("Finalising Rebuild", "Reading master hash from chunk header...");
            string sourceHash = "";
            await Task.Run(() => {
                using var stream = new FileStream(chunkFile, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8);
                var meta = ChunkMetadata.ReadFrom(reader);
                sourceHash = meta.SourceFileHash;
            }, ct);

            ct.ThrowIfCancellationRequested();

            var embedState = _rebuilder.GetProgress(targetFile);
            long displaySize = Math.Max(embedState?.ContentSize ?? 1, 0);

            SetProgress(0, displaySize, "Verifying Hash", "Verifying file integrity...");
            var prog = new Progress<long>(b => {
                Dispatcher.Invoke(() => SetProgress(b, displaySize, "Verifying Hash"));
            });

            await Task.Run(() => _rebuilder.Finalise(targetFile, sourceHash, prog, ct), ct);
            Log("✓ Hash matches — file is intact!");
            
            ct.ThrowIfCancellationRequested();

            if (FolderPacker.IsPackedFolder(targetFile))
            {
                string destDir = targetFile.EndsWith(".oppaku-dir") ? targetFile.Substring(0, targetFile.Length - 11) : targetFile + "_extracted";
                SetIndeterminate("Unpacking Folder", "Restoring folder...");
                await Task.Run(() => FolderPacker.Unpack(targetFile, destDir, new Progress<long>(), ct), ct);
                Log($"✓ Extracted folder to {destDir}");
            }
            
            ClearProgress("Finalisation Complete");
            MessageBox.Show("Rebuild successful! The file is completely intact.", "Success");
            if (Directory.Exists(TxtCurrentPath.Text)) NavigateTo(TxtCurrentPath.Text);
        }
        catch (OperationCanceledException)
        {
            Log("⚠️ Finalisation cancelled.");
            ClearProgress("Cancelled");
            MessageBox.Show("Finalisation / verification was cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            if (Directory.Exists(TxtCurrentPath.Text)) NavigateTo(TxtCurrentPath.Text);
        }
        catch (Exception ex)
        {
            Log($"Finalisation failed: {ex.Message}");
            ClearProgress("Failed");
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 3. CREATE ARCHIVE (V3)
    private void BtnToolCreateArchive_Click(object sender, RoutedEventArgs e)
    {
        if (IsTaskRunning)
        {
            MessageBox.Show("A task is currently running. Please wait for it to finish or cancel it first.", "Task in Progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = LvFiles.SelectedItems.Cast<FsItem>().ToList();
        if (selected.Count == 0 && !string.IsNullOrEmpty(TxtCurrentPath.Text) && TxtCurrentPath.Text != "This PC")
        {
            selected.Add(new FsItem { FullPath = TxtCurrentPath.Text, Name = Path.GetFileName(TxtCurrentPath.Text) });
        }
        if (selected.Count != 1)
        {
            MessageBox.Show("Please select exactly one file or folder to archive.");
            return;
        }

        string sourcePath = selected[0].FullPath;
        
        var fixedTop = new StackPanel();
        fixedTop.Children.Add(CreateHeaderLabel("Create Archive"));
        fixedTop.Children.Add(CreateSecondaryLabel($"Source: {sourcePath}", wrap: true));
        
        fixedTop.Children.Add(CreateLabel("Compression Level:"));
        var cboCompression = new ComboBox { Margin = new Thickness(0,0,0,15) };
        cboCompression.ItemsSource = Enum.GetValues(typeof(OppakuCompressionLevel));
        cboCompression.SelectedItem = OppakuCompressionLevel.Normal;
        fixedTop.Children.Add(cboCompression);
        
        fixedTop.Children.Add(CreateLabel("Password (optional):"));
        var txtPwd = new PasswordBox { Margin = new Thickness(0,0,0,20) };
        fixedTop.Children.Add(txtPwd);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,5,0,0) };
        var btnCancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostBtn"), Margin = new Thickness(0,0,10,0) };
        btnCancel.Click += (s,ev) => {
            if (IsTaskRunning)
            {
                ConfirmAndCancelActiveTask();
                return;
            }
            CloseDialog();
        };
        var btnStart = new Button { Content = "Create", Style = (Style)FindResource("AccentBtn") };
        
        btnStart.Click += async (s,ev) => {
            if (IsTaskRunning) { MessageBox.Show("A task is already running."); return; }
            string targetDir = Directory.Exists(sourcePath) ? sourcePath : (Path.GetDirectoryName(sourcePath) ?? "C:\\");
            string baseName = Path.GetFileName(sourcePath);
            string defaultName = baseName + ".oppaku-archive";
            int counter = 1;
            
            while (File.Exists(Path.Combine(targetDir, defaultName)))
            {
                defaultName = $"{baseName} ({counter}).oppaku-archive";
                counter++;
            }

            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Archive As",
                Filter = "Oppaku Archive (*.oppaku-archive)|*.oppaku-archive",
                FileName = defaultName,
                InitialDirectory = targetDir
            };
            if (saveDialog.ShowDialog() == true)
            {
                string pwd = txtPwd.Password;
                var compression = (OppakuCompressionLevel)cboCompression.SelectedItem;
                btnStart.IsEnabled = false;
                _activeTaskCts = new CancellationTokenSource();
                var ct = _activeTaskCts.Token;
                try
                {
                    await DoCreateArchive(sourcePath, saveDialog.FileName, string.IsNullOrEmpty(pwd) ? null : pwd, compression, ct);
                }
                finally
                {
                    _activeTaskCts?.Dispose();
                    _activeTaskCts = null;
                    btnStart.IsEnabled = true;
                }
            }
        };
        
        btnPanel.Children.Add(btnCancel);
        btnPanel.Children.Add(btnStart);
        
        ShowDialog(CreateDialogContainer(fixedTop, null, btnPanel));
    }

    private async Task DoCreateArchive(string sourcePath, string outPath, string? pwd, OppakuCompressionLevel compression, CancellationToken ct)
    {
        Log("─────────────────────────────────");
        SetIndeterminate("Creating Archive", "Packing into secure archive...");
        try
        {
            var prog = new Progress<long>(b => {
                Dispatcher.Invoke(() => SetIndeterminate("Creating Archive", $"Compressing: {FormatBytes(b)} processed..."));
            });
            await Task.Run(() => ArchivePacker.Pack(sourcePath, outPath, pwd, compression, prog, ct), ct);
            Log("✓ Archive created successfully");
            ClearProgress("Complete");
            MessageBox.Show("Archive created successfully.", "Success");
            if (Directory.Exists(TxtCurrentPath.Text)) NavigateTo(TxtCurrentPath.Text);
        }
        catch (OperationCanceledException)
        {
            Log("⚠️ Archive creation cancelled. Incomplete archive deleted.");
            ClearProgress("Cancelled");
            MessageBox.Show("Archive creation was cancelled. The incomplete archive file was deleted.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            if (Directory.Exists(TxtCurrentPath.Text)) NavigateTo(TxtCurrentPath.Text);
        }
        catch (Exception ex)
        {
            Log($"Error creating archive: {ex.Message}");
            ClearProgress("Failed");
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 4. EXTRACT ARCHIVE (V3)
    private void BtnToolExtractArchive_Click(object sender, RoutedEventArgs e)
    {
        if (IsTaskRunning)
        {
            MessageBox.Show("A task is currently running. Please wait for it to finish or cancel it first.", "Task in Progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = LvFiles.SelectedItems.Cast<FsItem>().Where(f => f.FullPath.EndsWith(".oppaku-archive", StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Please select an .oppaku-archive file.");
            return;
        }

        string sourcePath = selected[0].FullPath;
        
        var fixedTop = new StackPanel();
        fixedTop.Children.Add(CreateHeaderLabel("Extract Archive"));
        fixedTop.Children.Add(CreateSecondaryLabel($"Archive: {Path.GetFileName(sourcePath)}", wrap: true));
        
        fixedTop.Children.Add(CreateLabel("Output Directory:"));
        var outField = CreateBrowseField(TxtCurrentPath.Text == "This PC" ? "" : TxtCurrentPath.Text, false);
        fixedTop.Children.Add(outField.Container);

        fixedTop.Children.Add(CreateLabel("Password (if required):"));
        var txtPwd = new PasswordBox { Margin = new Thickness(0,0,0,20) };
        fixedTop.Children.Add(txtPwd);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,5,0,0) };
        var btnCancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostBtn"), Margin = new Thickness(0,0,10,0) };
        btnCancel.Click += (s,ev) => {
            if (IsTaskRunning)
            {
                ConfirmAndCancelActiveTask();
                return;
            }
            CloseDialog();
        };
        var btnStart = new Button { Content = "Extract", Style = (Style)FindResource("AccentBtn") };
        
        btnStart.Click += async (s,ev) => {
            if (IsTaskRunning) { MessageBox.Show("A task is already running."); return; }
            string outDir = outField.TextBox.Text;
            string pwd = txtPwd.Password;
            if (string.IsNullOrEmpty(outDir)) return;
            
            btnStart.IsEnabled = false;
            _activeTaskCts = new CancellationTokenSource();
            var ct = _activeTaskCts.Token;
            try
            {
                await DoExtractArchive(sourcePath, outDir, string.IsNullOrEmpty(pwd) ? null : pwd, ct);
            }
            finally
            {
                _activeTaskCts?.Dispose();
                _activeTaskCts = null;
                btnStart.IsEnabled = true;
            }
        };
        
        btnPanel.Children.Add(btnCancel);
        btnPanel.Children.Add(btnStart);
        
        ShowDialog(CreateDialogContainer(fixedTop, null, btnPanel));
    }

    private async Task DoExtractArchive(string archivePath, string outPath, string? pwd, CancellationToken ct)
    {
        Log("─────────────────────────────────");
        var fi = new FileInfo(archivePath);
        SetProgress(0, fi.Length, "Extracting Archive", "Extracting...");
        try
        {
            var prog = new Progress<long>(b => {
                Dispatcher.Invoke(() => SetProgress(b, fi.Length, "Extracting Archive"));
            });
            
            Func<string, bool> onOverwriteConfirm = (filePath) => 
            {
                bool overwrite = false;
                Dispatcher.Invoke(() => {
                    var result = MessageBox.Show($"The file '{Path.GetFileName(filePath)}' already exists in the destination.\nDo you want to overwrite it?", "File Conflict", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    overwrite = (result == MessageBoxResult.Yes);
                });
                return overwrite;
            };

            await Task.Run(() => ArchivePacker.Unpack(archivePath, outPath, pwd, onOverwriteConfirm, prog, ct), ct);
            Log("✓ Archive extracted successfully");
            ClearProgress("Complete");
            MessageBox.Show("Archive extracted successfully.", "Success");
            if (Directory.Exists(TxtCurrentPath.Text)) NavigateTo(TxtCurrentPath.Text);
        }
        catch (OperationCanceledException)
        {
            Log("⚠️ Archive extraction cancelled. Incomplete file removed.");
            ClearProgress("Cancelled");
            MessageBox.Show("Archive extraction was cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            if (Directory.Exists(TxtCurrentPath.Text)) NavigateTo(TxtCurrentPath.Text);
        }
        catch (Exception ex)
        {
            Log($"Error extracting archive: {ex.Message}");
            ClearProgress("Failed");
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 5. HASH CHECK
    private async void BtnToolHashCheck_Click(object sender, RoutedEventArgs e)
    {
        if (IsTaskRunning)
        {
            MessageBox.Show("A task is currently running. Please wait for it to finish or cancel it first.", "Task in Progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = LvFiles.SelectedItems.Cast<FsItem>().ToList();
        if (selected.Count != 1)
        {
            MessageBox.Show("Please select exactly one file to verify.");
            return;
        }

        string targetFile = selected[0].FullPath;
        var state = _rebuilder.GetProgress(targetFile);

        if (state == null)
        {
            MessageBox.Show("This file does not appear to be a rebuild-in-progress, or it has already been finalised.", "No Metadata Found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (state.Received.Count < state.Total)
        {
            var missing = Enumerable.Range(0, state.Total).Except(state.Received).ToList();
            MessageBox.Show($"Cannot verify. Missing parts: {string.Join(", ", missing)}", "Incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(state.ExpectedHash))
        {
            MessageBox.Show("Missing original hash in metadata. Cannot verify standalone.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Log("─────────────────────────────────");
        SetIndeterminate("Hash Check", "Verifying file integrity...");
        _activeTaskCts = new CancellationTokenSource();
        var ct = _activeTaskCts.Token;
        try
        {
            var prog = new Progress<long>(b => {
                Dispatcher.Invoke(() => SetProgress(b, state.ContentSize, "Verifying Hash"));
            });

            await Task.Run(() => _rebuilder.Finalise(targetFile, state.ExpectedHash, prog, ct), ct);
            
            Log("✓ Hash matches — file is intact!");
            
            ct.ThrowIfCancellationRequested();

            if (FolderPacker.IsPackedFolder(targetFile))
            {
                string destDir = targetFile.EndsWith(".oppaku-dir") ? targetFile.Substring(0, targetFile.Length - 11) : targetFile + "_extracted";
                SetIndeterminate("Unpacking Folder", "Restoring folder...");
                await Task.Run(() => FolderPacker.Unpack(targetFile, destDir, new Progress<long>(), ct), ct);
                Log($"✓ Extracted folder to {destDir}");
            }
            
            ClearProgress("Verification Complete");
            MessageBox.Show("Rebuild successful! The file is completely intact and finalised.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            if (Directory.Exists(TxtCurrentPath.Text)) NavigateTo(TxtCurrentPath.Text);
        }
        catch (OperationCanceledException)
        {
            Log("⚠️ Verification cancelled.");
            ClearProgress("Cancelled");
            MessageBox.Show("Hash verification was cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            if (Directory.Exists(TxtCurrentPath.Text)) NavigateTo(TxtCurrentPath.Text);
        }
        catch (Exception ex)
        {
            Log($"Verification failed: {ex.Message}");
            ClearProgress("Failed");
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _activeTaskCts?.Dispose();
            _activeTaskCts = null;
        }
    }
}
