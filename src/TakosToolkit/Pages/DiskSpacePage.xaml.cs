using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TakosToolkit.Controls;
using TakosToolkit.Core;
using TakosToolkit.Core.DiskScanning;
using TakosToolkit.Core.SystemInfo;

namespace TakosToolkit.Pages;

public partial class DiskSpacePage : Page
{
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CancellationTokenSource? _cts;
    private ScanProgress? _progress;
    private readonly Stopwatch _stopwatch = new();
    private FileNode? _current;
    private int _statsVersion;

    public DiskSpacePage()
    {
        InitializeComponent();

        _progressTimer.Tick += (_, _) => UpdateProgressText();
        Treemap.HoveredNodeChanged += (_, node) => ShowHoverDetails(node);
        Treemap.NodeActivated += (_, node) => Activate(node);
        MouseUp += Page_MouseUp;

        AdminButton.Visibility = SystemSnapshot.IsAdministrator() ? Visibility.Collapsed : Visibility.Visible;
        LoadDrives();
    }

    private void LoadDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                PathBox.Items.Add(drive.Name);
        }

        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        PathBox.Text = systemDrive;
    }

    // ---- Scanning --------------------------------------------------------------------------

    /// <summary>Starts a scan of <paramref name="path"/> (used by the --disk command-line switch).</summary>
    public void Scan(string path)
    {
        PathBox.Text = path;
        _ = StartScanAsync();
    }

    private async Task StartScanAsync()
    {
        string path = PathBox.Text.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':')
            path += "\\";

        if (!Directory.Exists(path))
        {
            ProgressText.Text = $"Folder not found: {path}";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _progress = new ScanProgress();

        SetBusy(true);
        Treemap.Root = null;
        _current = null;
        LargestFilesGrid.ItemsSource = null;
        ExtensionsGrid.ItemsSource = null;
        LegendList.ItemsSource = null;
        LocationText.Text = "Scanning " + path;
        _stopwatch.Restart();
        _progressTimer.Start();

        try
        {
            var root = await DiskScanner.ScanAsync(path, _progress, _cts.Token);
            _stopwatch.Stop();
            ShowFolder(root);

            bool admin = SystemSnapshot.IsAdministrator();
            string method = _progress.Method == ScanMethod.Mft
                ? " · read from NTFS MFT"
                : _progress.MftFallbackReason is { } reason
                    ? admin ? $" · fast MFT scan unavailable ({reason.TrimEnd('.')})" : " · restart as admin for a much faster scan"
                    : "";
            string errors = _progress.Errors > 0
                ? $" · {_progress.Errors:N0} folders couldn't be read" + (admin ? "" : " (try Restart as admin)")
                : "";
            ProgressText.Text =
                $"Scanned {root.FileCount:N0} files in {_progress.Folders:N0} folders in {_stopwatch.Elapsed.TotalSeconds:0.0} s{method}{errors}";
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Scan cancelled.";
            LocationText.Text = "Choose a drive or folder and press Scan.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProgressText.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            _progressTimer.Stop();
            SetBusy(false);
        }
    }

    private void UpdateProgressText()
    {
        if (_progress is null)
            return;
        ProgressText.Text =
            $"{_progress.Files:N0} files · {ByteSize.Format(_progress.Bytes)} · {_stopwatch.Elapsed.TotalSeconds:0.0} s · {_progress.CurrentPath}";
    }

    private void SetBusy(bool busy)
    {
        ScanButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PathBox.IsEnabled = !busy;
        UpButton.IsEnabled = !busy && _current?.Parent is not null;
    }

    // ---- Navigation ------------------------------------------------------------------------

    private void ShowFolder(FileNode folder)
    {
        _current = folder;
        Treemap.Root = folder;
        UpButton.IsEnabled = folder.Parent is not null;
        LocationText.Text = $"{folder.FullPath}   ·   {ByteSize.Format(folder.Size)}   ·   {folder.FileCount:N0} files";
        _ = LoadStatisticsAsync(folder);
    }

    private async Task LoadStatisticsAsync(FileNode folder)
    {
        int version = ++_statsVersion;
        var stats = await Task.Run(() => ScanStatistics.Compute(folder));
        if (version != _statsVersion)
            return; // the user moved on to another folder

        double total = Math.Max(1, folder.Size);
        LargestFilesGrid.ItemsSource = stats.LargestFiles
            .Select(f => new FileRow(f.Name, f.Size, ByteSize.Format(f.Size), f.Parent?.FullPath ?? "", f))
            .ToList();
        ExtensionsGrid.ItemsSource = stats.Extensions
            .Select(e => new ExtensionRow(e.Extension, e.Size, ByteSize.Format(e.Size), (e.Size / total).ToString("0.0%", CultureInfo.CurrentCulture), e.Count))
            .ToList();
        LegendList.ItemsSource = stats.Categories
            .Select(c => new LegendRow(FileCategorizer.DisplayName(c.Category), TreemapControl.Palette.CategoryBrush(c.Category), ByteSize.Format(c.Size)))
            .ToList();
    }

    private void Activate(FileNode node)
    {
        if (node.IsDirectory)
            ShowFolder(node);
        else
            ShowInExplorer(node);
    }

    private void GoUp()
    {
        if (_current?.Parent is { } parent)
            ShowFolder(parent);
    }

    private void ShowHoverDetails(FileNode? node)
    {
        if (node is null)
            return;

        string details = node.IsDirectory
            ? $"{ByteSize.Format(node.Size)} · {node.FileCount:N0} files"
            : $"{ByteSize.Format(node.Size)} · modified {node.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd}";
        HoverText.Text = $"{node.FullPath}   ·   {details}";
    }

    private static void ShowInExplorer(FileNode node)
    {
        string path = node.FullPath;
        var psi = node.IsDirectory && node.Parent is null
            ? new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            : new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"");
        Process.Start(psi);
    }

    // ---- Event handlers --------------------------------------------------------------------

    private async void Scan_Click(object sender, RoutedEventArgs e) => await StartScanAsync();

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await StartScanAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    private void Page_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
            GoUp();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to scan" };
        if (dialog.ShowDialog() == true)
        {
            PathBox.Text = dialog.FolderName;
            _ = StartScanAsync();
        }
    }

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Reopen on this page and rescan the same location. A trailing backslash before the
            // closing quote would escape it, so "C:\" is passed as "C:".
            string path = (_current?.Root.Name ?? PathBox.Text).Trim().TrimEnd('\\');
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--disk \"{path}\"",
            });
            Application.Current.Shutdown();
        }
        catch (Win32Exception)
        {
            // The user declined the UAC prompt.
        }
    }

    private void TreemapMenu_Opened(object sender, RoutedEventArgs e)
    {
        var node = Treemap.SelectedNode;
        ZoomMenuItem.Visibility = node?.IsDirectory == true ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in ((ContextMenu)sender).Items.OfType<MenuItem>())
            item.IsEnabled = node is not null;
    }

    private void ZoomMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Treemap.SelectedNode is { IsDirectory: true } node)
            ShowFolder(node);
    }

    private void ShowInExplorerMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Treemap.SelectedNode is { } node)
            ShowInExplorer(node);
    }

    private void CopyPathMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Treemap.SelectedNode is { } node)
            Clipboard.SetText(node.FullPath);
    }

    private void LargestFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LargestFilesGrid.SelectedItem is FileRow row)
            ShowInExplorer(row.Node);
    }

    private sealed record FileRow(string Name, long Size, string SizeText, string Folder, FileNode Node);

    private sealed record ExtensionRow(string Extension, long Size, string SizeText, string PercentText, long Count);

    private sealed record LegendRow(string Name, Brush Brush, string SizeText);
}
