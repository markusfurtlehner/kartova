using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirStat.App.Services;
using DirStat.Core.Files;
using DirStat.Core.Filtering;
using DirStat.Core.Model;
using DirStat.Core.Scanning;

namespace DirStat.App.ViewModels;

public enum AppScreen
{
    Volumes,
    Scanning,
    Results,
}

/// <summary>Root view model. Owns screen state, the scan lifecycle, and cross-pane selection.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DirectoryScanner _scanner = new();
    private CancellationTokenSource? _scanCancellation;
    private ScanResult? _result;

    /// <summary>The unfiltered tree. Kept so clearing the filter is instant.</summary>
    private FileNode? _unfilteredRoot;

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        SizeFormatter.UseBinaryUnits = settings.UseBinaryUnits;
        LoadVolumes();
        RefreshRecentPaths();
    }

    public AppSettings Settings { get; }

    // ------------------------------------------------------------- screens

    [ObservableProperty] private AppScreen _screen = AppScreen.Volumes;

    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<VolumeCardViewModel> Volumes { get; } = [];
    public ObservableCollection<string> RecentPaths { get; } = [];

    /// <summary>Set by the view so the view model can raise native pickers without knowing about windows.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickFoldersAsync { get; set; }

    /// <summary>Set by the view. Takes a suggested file name and returns the chosen path.</summary>
    public Func<string, Task<string?>>? PickSaveFileAsync { get; set; }

    private void LoadVolumes()
    {
        Volumes.Clear();
        foreach (var volume in VolumeProvider.Enumerate())
            Volumes.Add(new VolumeCardViewModel(volume));
    }

    private void RefreshRecentPaths()
    {
        RecentPaths.Clear();
        foreach (var path in Settings.RecentPaths.Where(Directory.Exists).Take(6))
            RecentPaths.Add(path);
    }

    // ------------------------------------------------------------ scanning

    [ObservableProperty] private string _scanFilesText = "0";
    [ObservableProperty] private string _scanDirectoriesText = "0";
    [ObservableProperty] private string _scanBytesText = "0 B";
    [ObservableProperty] private string _scanRateText = "—";
    [ObservableProperty] private string _scanElapsedText = "0.0 s";
    [ObservableProperty] private string _scanCurrentPath = string.Empty;
    [ObservableProperty] private string _scanTargetText = string.Empty;

    [RelayCommand]
    private Task ScanVolumeAsync(VolumeCardViewModel? card) =>
        card is null ? Task.CompletedTask : StartScanAsync([card.RootPath]);

    [RelayCommand]
    private Task ScanRecentAsync(string? path) =>
        string.IsNullOrEmpty(path) ? Task.CompletedTask : StartScanAsync([path]);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (PickFoldersAsync is null) return;

        IReadOnlyList<string> folders;
        try
        {
            folders = await PickFoldersAsync();
        }
        catch (Exception e)
        {
            // A platform file dialog can fail for reasons entirely outside the app —
            // a missing desktop portal being the usual one on Linux. Say so rather than
            // letting it surface as an unhandled exception from a command.
            StatusMessage = $"Could not open the folder picker: {e.Message}";
            return;
        }

        if (folders.Count > 0) await StartScanAsync(folders);
    }

    /// <summary>Scans paths given on the command line, skipping the picker entirely.</summary>
    public void ScanFromCommandLine(string[] args)
    {
        var paths = args.Where(a => Directory.Exists(a) || File.Exists(a)).ToArray();
        if (paths.Length > 0) _ = StartScanAsync(paths);
    }

    public async Task StartScanAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;

        Screen = AppScreen.Scanning;
        IsBusy = true;
        StatusMessage = string.Empty;
        ScanTargetText = paths.Count == 1 ? paths[0] : $"{paths.Count} locations";
        ResetScanCounters();

        var options = BuildScanOptions();

        // Constructed here, on the UI thread, so its callbacks marshal back automatically.
        var progress = new Progress<ScanProgress>(OnScanProgress);

        try
        {
            var result = await _scanner.ScanAsync(paths, options, progress, token);
            ApplyResult(result);

            foreach (var path in paths) Settings.RememberPath(path);
            RefreshRecentPaths();
        }
        catch (OperationCanceledException)
        {
            Screen = _result is null ? AppScreen.Volumes : AppScreen.Results;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = $"Scan failed: {e.Message}";
            Screen = AppScreen.Volumes;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ScanOptions BuildScanOptions()
    {
        var options = new ScanOptions
        {
            IncludeFreeSpace = Settings.ShowFreeSpace,
            SkipHidden = Settings.SkipHidden,
            DetectHardLinks = Settings.DetectHardLinks,
            ExactAllocation = Settings.ExactAllocation,
        };

        foreach (var name in Settings.ExcludedDirectoryNames)
            options.ExcludedDirectoryNames.Add(name);

        return options;
    }

    private void ResetScanCounters()
    {
        ScanFilesText = "0";
        ScanDirectoriesText = "0";
        ScanBytesText = "0 B";
        ScanRateText = "—";
        ScanElapsedText = "0.0 s";
        ScanCurrentPath = string.Empty;
    }

    private void OnScanProgress(ScanProgress progress)
    {
        ScanFilesText = SizeFormatter.FormatCount(progress.FilesSeen);
        ScanDirectoriesText = SizeFormatter.FormatCount(progress.DirectoriesSeen);
        ScanBytesText = SizeFormatter.Format(progress.BytesSeen);
        ScanRateText = $"{SizeFormatter.FormatCount((long)progress.FilesPerSecond)} files/s";
        ScanElapsedText = SizeFormatter.FormatDuration(progress.Elapsed);
        ScanCurrentPath = progress.CurrentPath;
    }

    [RelayCommand]
    private void CancelScan() => _scanCancellation?.Cancel();

    [RelayCommand]
    private void BackToVolumes()
    {
        LoadVolumes();
        Screen = AppScreen.Volumes;
    }

    // ------------------------------------------------------------- results

    [ObservableProperty] private DirectoryTreeViewModel? _tree;
    [ObservableProperty] private FileNode? _treemapRoot;
    [ObservableProperty] private FileNode? _selectedNode;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private string _deniedText = string.Empty;
    [ObservableProperty] private bool _hasDenied;
    [ObservableProperty] private string _selectionPathText = string.Empty;
    [ObservableProperty] private string _selectionDetailText = string.Empty;

    public ObservableCollection<ExtensionViewModel> Extensions { get; } = [];
    public ObservableCollection<BreadcrumbViewModel> Breadcrumbs { get; } = [];

    /// <summary>
    /// The file type picked in the third pane. Selecting one narrows the tree and the
    /// treemap to that type, which is how WinDirStat lets you answer "where are all my
    /// videos" without typing anything.
    /// </summary>
    [ObservableProperty] private ExtensionViewModel? _selectedExtension;

    partial void OnSelectedExtensionChanged(ExtensionViewModel? value)
    {
        if (value is null || _synchronizingSelection) return;

        // Extensionless files cannot be expressed as a *.ext pattern, so clear instead.
        FilterText = value.Extension.Length == 0 ? string.Empty : $"*{value.Extension}";
    }

    /// <summary>Guards against selection changes echoing between panes forever.</summary>
    private bool _synchronizingSelection;

    private void ApplyResult(ScanResult result)
    {
        _result = result;
        _unfilteredRoot = result.Root;

        BuildTree(result.Root);

        SelectedExtension = null;
        Extensions.Clear();
        foreach (var stat in result.Extensions.Take(400))
            Extensions.Add(new ExtensionViewModel(stat));

        TreemapRoot = result.Root;
        SelectedNode = result.Root;

        SummaryText =
            $"{SizeFormatter.Format(result.TotalBytes)}  ·  " +
            $"{SizeFormatter.FormatCount(result.TotalFiles)} files  ·  " +
            $"{SizeFormatter.FormatCount(result.TotalDirectories)} folders  ·  " +
            $"scanned in {SizeFormatter.FormatDuration(result.Duration)}";

        HasDenied = result.DeniedPaths.Count > 0;
        DeniedText = HasDenied
            ? $"{SizeFormatter.FormatCount(result.DeniedPaths.Count)} folders could not be read"
            : string.Empty;

        StatusMessage = result.WasCancelled ? "Scan cancelled — showing partial results." : string.Empty;
        Screen = AppScreen.Results;
    }

    /// <summary>Rebuilds the directory grid over the given tree.</summary>
    private void BuildTree(FileNode root)
    {
        if (Tree is not null) Tree.RowSelected -= OnTreeRowSelected;

        var tree = new DirectoryTreeViewModel(root, Settings.ShowSizeOnDisk);
        tree.RowSelected += OnTreeRowSelected;
        Tree = tree;
    }

    private void OnTreeRowSelected(FileNode node)
    {
        if (_synchronizingSelection) return;

        _synchronizingSelection = true;
        try
        {
            SelectedNode = node;
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

    partial void OnSelectedNodeChanged(FileNode? value)
    {
        if (value is null)
        {
            SelectionPathText = string.Empty;
            SelectionDetailText = string.Empty;
            return;
        }

        SelectionPathText = value.GetFullPath();

        var parts = new List<string> { SizeFormatter.Format(Settings.ShowSizeOnDisk ? value.SizeOnDisk : value.Size) };
        if (value.IsDirectory)
        {
            parts.Add($"{SizeFormatter.FormatCount(value.FileCount)} files");
            parts.Add($"{SizeFormatter.FormatCount(value.DirCount)} folders");
        }
        if (value.Parent is not null)
            parts.Add($"{SizeFormatter.FormatPercent(value.FractionOfParent)} of parent");

        SelectionDetailText = string.Join("   ·   ", parts);

        UpdateBreadcrumbs();

        if (!_synchronizingSelection) SyncTreeToSelection(value);
    }

    /// <summary>Expands and scrolls the grid to whatever the treemap or a breadcrumb selected.</summary>
    private void SyncTreeToSelection(FileNode node)
    {
        var tree = Tree;
        if (tree is null) return;

        _synchronizingSelection = true;
        try
        {
            tree.RevealAndSelect(node);
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();

        var target = TreemapRoot;
        if (target is null) return;

        var chain = new List<FileNode>();
        for (var current = target; current is not null; current = current.Parent) chain.Add(current);
        chain.Reverse();

        foreach (var node in chain)
            Breadcrumbs.Add(new BreadcrumbViewModel(node, node == target));
    }

    // -------------------------------------------------------------- zoom

    [RelayCommand]
    private void ZoomInto(FileNode? node)
    {
        if (node is null || !node.IsDirectory || node.Children is not { Length: > 0 }) return;
        TreemapRoot = node;
        SelectedNode = node;
        UpdateBreadcrumbs();
    }

    [RelayCommand]
    private void ZoomOut()
    {
        var parent = TreemapRoot?.Parent;
        if (parent is null) return;
        TreemapRoot = parent;
        SelectedNode = parent;
        UpdateBreadcrumbs();
    }

    [RelayCommand]
    private void ZoomToRoot()
    {
        var root = _unfilteredRoot;
        if (root is null) return;
        TreemapRoot = root;
        SelectedNode = root;
        UpdateBreadcrumbs();
    }

    // ------------------------------------------------------------ filtering

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _filterSummary = string.Empty;
    [ObservableProperty] private bool _isFiltered;

    private CancellationTokenSource? _filterDebounce;

    partial void OnFilterTextChanged(string value)
    {
        // Typing should not re-walk a million nodes on every keystroke.
        _filterDebounce?.Cancel();
        _filterDebounce = new CancellationTokenSource();
        var token = _filterDebounce.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, token);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyFilter(value));
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later keystroke.
            }
        }, CancellationToken.None);
    }

    private void ApplyFilter(string query)
    {
        var root = _unfilteredRoot;
        if (root is null) return;

        var criteria = FilterCriteria.Parse(query);

        if (criteria.IsEmpty)
        {
            IsFiltered = false;
            FilterSummary = string.Empty;
            // Drop the file-type highlight too, so clicking the same type again re-applies it.
            SelectedExtension = null;
            BuildTree(root);
            TreemapRoot = root;
            SelectedNode = root;
            UpdateBreadcrumbs();
            return;
        }

        var filtered = NodeFilter.Apply(root, criteria);
        filtered.SortBySizeDescending();

        IsFiltered = true;
        FilterSummary = filtered.FileCount == 0
            ? "No files match"
            : $"{SizeFormatter.FormatCount(filtered.FileCount)} files  ·  {SizeFormatter.Format(filtered.Size)}";

        // The filtered tree is a copy, so its nodes have no identity in the original and
        // tree-to-treemap selection sync simply finds no path. That is the intended behaviour.
        BuildTree(filtered);
        TreemapRoot = filtered;
        SelectedNode = filtered;
        UpdateBreadcrumbs();
    }

    // ------------------------------------------------------ file operations

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedNode is not { } node || node.IsSynthetic) return;
        Report(ShellService.Open(node.GetFullPath()));
    }

    [RelayCommand]
    private void RevealSelected()
    {
        if (SelectedNode is not { } node || node.IsSynthetic) return;
        Report(ShellService.Reveal(node.GetFullPath()));
    }

    [RelayCommand]
    private void OpenTerminalHere()
    {
        if (SelectedNode is not { } node || node.IsSynthetic) return;
        var path = node.GetFullPath();
        Report(ShellService.OpenTerminal(node.IsDirectory ? path : Path.GetDirectoryName(path) ?? path));
    }

    /// <summary>Set by the view; copies text to the system clipboard.</summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    [RelayCommand]
    private async Task CopyPathAsync()
    {
        if (SelectedNode is not { } node || node.IsSynthetic) return;
        if (CopyToClipboardAsync is null) return;

        await CopyToClipboardAsync(node.GetFullPath());
        StatusMessage = "Path copied to clipboard.";
    }

    // ---- deletion, behind an explicit confirmation

    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private string _confirmTitle = string.Empty;
    [ObservableProperty] private string _confirmBody = string.Empty;

    private bool _pendingPermanentDelete;

    [RelayCommand]
    private void RequestDeleteToTrash() => RequestDelete(permanent: false);

    [RelayCommand]
    private void RequestDeletePermanently() => RequestDelete(permanent: true);

    private void RequestDelete(bool permanent)
    {
        if (SelectedNode is not { } node || node.IsSynthetic || node.IsRoot) return;

        _pendingPermanentDelete = permanent;
        ConfirmTitle = permanent ? "Delete permanently?" : "Move to trash?";

        var what = node.IsDirectory
            ? $"{node.Name} and its {SizeFormatter.FormatCount(node.FileCount)} files"
            : node.Name;

        ConfirmBody = permanent
            ? $"{what} will be erased immediately. This cannot be undone.\n\n{node.GetFullPath()}"
            : $"{what} will be moved to the trash.\n\n{node.GetFullPath()}";

        IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        IsConfirmingDelete = false;

        if (SelectedNode is not { } node || node.IsSynthetic || node.IsRoot) return;

        var path = node.GetFullPath();
        var parent = node.Parent;

        var result = await Task.Run(() => _pendingPermanentDelete
            ? ShellService.DeletePermanently(path)
            : ShellService.MoveToTrash(path));

        if (!result.Success)
        {
            Report(result);
            return;
        }

        StatusMessage = _pendingPermanentDelete ? $"Deleted {node.Name}." : $"Moved {node.Name} to trash.";

        // Re-walk the containing folder so the tree and the map agree with the disk again.
        if (parent is not null) await RefreshNodeAsync(parent);
    }

    // ------------------------------------------------------------- refresh

    [RelayCommand]
    private async Task RefreshSelectedAsync()
    {
        var target = SelectedNode;
        if (target is null) return;
        if (!target.IsDirectory) target = target.Parent;
        if (target is null) return;

        await RefreshNodeAsync(target);
    }

    /// <summary>
    /// Rescans one directory and splices the result into the existing tree, propagating the
    /// size delta up to the root rather than rebuilding the whole scan.
    /// </summary>
    private async Task RefreshNodeAsync(FileNode node)
    {
        if (_unfilteredRoot is null) return;

        var path = node.GetFullPath();
        if (!Directory.Exists(path))
        {
            // The folder itself is gone; step up and refresh its parent instead.
            var parent = node.Parent;
            if (parent is null) return;
            await RefreshNodeAsync(parent);
            return;
        }

        IsBusy = true;
        try
        {
            var options = BuildScanOptions();
            options.IncludeFreeSpace = false; // only the root carries free-space nodes

            var rescan = await _scanner.ScanAsync([path], options);
            var fresh = rescan.Roots[0];

            var deltaSize = fresh.Size - node.Size;
            var deltaOnDisk = fresh.SizeOnDisk - node.SizeOnDisk;
            var deltaFiles = fresh.FileCount - node.FileCount;
            var deltaDirs = fresh.DirCount - node.DirCount;

            node.Children = fresh.Children ?? [];
            foreach (var child in node.Children) child.Parent = node;

            node.Size = fresh.Size;
            node.SizeOnDisk = fresh.SizeOnDisk;
            node.FileCount = fresh.FileCount;
            node.DirCount = fresh.DirCount;

            for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                ancestor.Size += deltaSize;
                ancestor.SizeOnDisk += deltaOnDisk;
                ancestor.FileCount += deltaFiles;
                ancestor.DirCount += deltaDirs;
            }

            _unfilteredRoot.SortBySizeDescending();

            // Rebuild the views over the amended tree.
            var keepZoom = TreemapRoot;
            BuildTree(_unfilteredRoot);
            TreemapRoot = null;
            TreemapRoot = keepZoom is not null && IsStillAttached(keepZoom) ? keepZoom : _unfilteredRoot;
            SelectedNode = node;

            StatusMessage = $"Refreshed {node.Name}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Refresh failed: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsStillAttached(FileNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
            if (ReferenceEquals(current, _unfilteredRoot))
                return true;
        return false;
    }

    // -------------------------------------------------------------- export

    [RelayCommand]
    private async Task ExportCsvAsync() => await ExportAsync("csv");

    [RelayCommand]
    private async Task ExportJsonAsync() => await ExportAsync("json");

    private async Task ExportAsync(string extension)
    {
        if (PickSaveFileAsync is null || _unfilteredRoot is null) return;

        var suggested = $"dirstat-{DateTime.Now:yyyyMMdd-HHmm}.{extension}";

        string? target;
        try
        {
            target = await PickSaveFileAsync(suggested);
        }
        catch (Exception e)
        {
            StatusMessage = $"Could not open the save dialog: {e.Message}";
            return;
        }

        if (string.IsNullOrEmpty(target)) return;

        var root = TreemapRoot ?? _unfilteredRoot;
        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                if (extension == "csv") ExportService.ExportCsv(root, target);
                else ExportService.ExportJson(root, target);
            });

            StatusMessage = $"Exported to {target}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Export failed: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------ settings

    [RelayCommand]
    private void ToggleUnits()
    {
        Settings.UseBinaryUnits = !Settings.UseBinaryUnits;
        SizeFormatter.UseBinaryUnits = Settings.UseBinaryUnits;
        RebuildAfterDisplayChange();
    }

    [RelayCommand]
    private void ToggleSizeOnDisk()
    {
        Settings.ShowSizeOnDisk = !Settings.ShowSizeOnDisk;
        RebuildAfterDisplayChange();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        Settings.Theme = Settings.Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        var app = Avalonia.Application.Current;
        if (app is not null)
            app.RequestedThemeVariant = Settings.Theme == AppTheme.Light
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;
    }

    [ObservableProperty] private bool _isSettingsOpen;

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    private void RebuildAfterDisplayChange()
    {
        if (_unfilteredRoot is null) return;

        var keepZoom = TreemapRoot;
        var keepSelection = SelectedNode;

        BuildTree(_unfilteredRoot);
        TreemapRoot = keepZoom ?? _unfilteredRoot;
        SelectedNode = keepSelection ?? _unfilteredRoot;

        if (_result is not null)
        {
            Extensions.Clear();
            foreach (var stat in _result.Extensions.Take(400))
                Extensions.Add(new ExtensionViewModel(stat));
        }
    }

    public void PersistSettings() => SettingsService.Save(Settings);

    public void PersistWindowState(double width, double height, bool maximized)
    {
        Settings.WindowWidth = width;
        Settings.WindowHeight = height;
        Settings.WindowMaximized = maximized;
    }

    private void Report(ShellResult result)
    {
        if (!result.Success && result.Message is not null) StatusMessage = result.Message;
    }
}

/// <summary>One hop in the treemap zoom trail.</summary>
public sealed class BreadcrumbViewModel(FileNode node, bool isCurrent)
{
    public FileNode Node { get; } = node;
    public bool IsCurrent { get; } = isCurrent;

    public string Label => Node.IsRoot ? Node.Name : Node.Name;
}
