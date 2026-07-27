using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirStat.App.Localization;
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
    Duplicates,
    Insights,
    Comparison,
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

        // The duplicate search runs over the tree the scan already produced, so it needs a
        // way to reach it, and a way to raise the same confirmation the main screen uses.
        Duplicates.GetScanRoot = () => _unfilteredRoot;
        Duplicates.ConfirmAsync = RequestConfirmationAsync;
        Duplicates.CopiesDeleted = OnDuplicatesDeletedAsync;

        Insights.GetScanRoot = () => _unfilteredRoot;
        Insights.ConfirmAsync = RequestConfirmationAsync;
        Insights.ItemsDeleted = OnDuplicatesDeletedAsync;

        Comparison.GetScanRoot = () => _unfilteredRoot;

        foreach (var name in settings.ExcludedDirectoryNames) Exclusions.Add(name);
    }

    /// <summary>The duplicate finder screen.</summary>
    public DuplicatesViewModel Duplicates { get; } = new();

    /// <summary>The insights screen.</summary>
    public InsightsViewModel Insights { get; } = new();

    /// <summary>Snapshot storage and comparison.</summary>
    public ComparisonViewModel Comparison { get; } = new();

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
            StatusMessage = Loc.Format("Status.PickerFailed", e.Message);
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
        ScanTargetText = paths.Count == 1 ? paths[0] : Loc.Format("Scan.Locations", paths.Count);
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
            StatusMessage = Loc.Format("Status.ScanFailed", e.Message);
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
        ScanRateText = Loc.Format("Scan.FilesPerSecond", SizeFormatter.FormatCount((long)progress.FilesPerSecond));
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

        // A fresh scan invalidates any previous derived analysis.
        Duplicates.Reset();
        Insights.Reset();
        Comparison.Reset();

        SelectedExtension = null;
        Extensions.Clear();
        foreach (var stat in result.Extensions.Take(400))
            Extensions.Add(new ExtensionViewModel(stat));

        TreemapRoot = result.Root;
        SelectedNode = result.Root;

        SummaryText = Loc.Format("Summary.Scan",
            SizeFormatter.Format(result.TotalBytes),
            SizeFormatter.FormatCount(result.TotalFiles),
            SizeFormatter.FormatCount(result.TotalDirectories),
            SizeFormatter.FormatDuration(result.Duration));

        HasDenied = result.DeniedPaths.Count > 0;
        DeniedText = HasDenied
            ? Loc.Format("Status.DeniedFolders", SizeFormatter.FormatCount(result.DeniedPaths.Count))
            : string.Empty;

        StatusMessage = result.WasCancelled ? Loc.T("Status.ScanCancelled") : string.Empty;
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
        UpdateSelectionDetail(value);
        UpdateBreadcrumbs();

        if (!_synchronizingSelection) SyncTreeToSelection(value);
    }

    private void UpdateSelectionDetail(FileNode node)
    {
        var parts = new List<string> { SizeFormatter.Format(Settings.ShowSizeOnDisk ? node.SizeOnDisk : node.Size) };

        if (node.IsDirectory)
        {
            parts.Add(Loc.Format("Summary.NFiles", SizeFormatter.FormatCount(node.FileCount)));
            parts.Add(Loc.Format("Summary.NFolders", SizeFormatter.FormatCount(node.DirCount)));
        }

        if (node.Parent is not null)
            parts.Add(Loc.Format("Summary.OfParent", SizeFormatter.FormatPercent(node.FractionOfParent)));

        SelectionDetailText = string.Join("   ·   ", parts);
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
        StatusMessage = Loc.T("Status.PathCopied");
    }

    // ---- deletion, behind an explicit confirmation

    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private string _confirmTitle = string.Empty;
    [ObservableProperty] private string _confirmBody = string.Empty;

    private bool _pendingPermanentDelete;

    /// <summary>Completes when the user answers the confirmation overlay.</summary>
    private TaskCompletionSource<bool>? _confirmation;

    /// <summary>
    /// Shows the confirmation overlay and waits for an answer.
    /// </summary>
    /// <remarks>
    /// Exposed so the duplicate finder can raise the same dialog rather than growing its
    /// own. Destructive actions should look identical wherever they are triggered from.
    /// </remarks>
    public Task<bool> RequestConfirmationAsync(string title, string body)
    {
        // A second request supersedes the first rather than leaving it hanging forever.
        _confirmation?.TrySetResult(false);

        ConfirmTitle = title;
        ConfirmBody = body;
        _confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsConfirmingDelete = true;

        return _confirmation.Task;
    }

    [RelayCommand]
    private Task RequestDeleteToTrashAsync() => RequestDeleteAsync(permanent: false);

    [RelayCommand]
    private Task RequestDeletePermanentlyAsync() => RequestDeleteAsync(permanent: true);

    private async Task RequestDeleteAsync(bool permanent)
    {
        if (SelectedNode is not { } node || node.IsSynthetic || node.IsRoot) return;

        _pendingPermanentDelete = permanent;

        var what = node.IsDirectory
            ? Loc.Format("Confirm.AndFiles", node.Name, SizeFormatter.FormatCount(node.FileCount))
            : node.Name;

        var confirmed = await RequestConfirmationAsync(
            Loc.T(permanent ? "Confirm.DeleteTitle" : "Confirm.TrashTitle"),
            Loc.Format(permanent ? "Confirm.DeleteBody" : "Confirm.TrashBody", what, node.GetFullPath()));

        if (confirmed) await DeleteConfirmedAsync();
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsConfirmingDelete = false;
        _confirmation?.TrySetResult(false);
        _confirmation = null;
    }

    [RelayCommand]
    private void ConfirmDelete()
    {
        IsConfirmingDelete = false;
        _confirmation?.TrySetResult(true);
        _confirmation = null;
    }

    private async Task DeleteConfirmedAsync()
    {
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

        StatusMessage = Loc.Format(_pendingPermanentDelete ? "Status.Deleted" : "Status.Trashed", node.Name);

        // Re-walk the containing folder so the tree and the map agree with the disk again.
        if (parent is not null) await RefreshNodeAsync(parent);
    }

    // ---------------------------------------------------------- duplicates

    [RelayCommand]
    private void ShowDuplicates()
    {
        if (_unfilteredRoot is null) return;
        Screen = AppScreen.Duplicates;
    }

    [RelayCommand]
    private void BackToResults() => Screen = AppScreen.Results;

    [RelayCommand]
    private void ShowInsights()
    {
        if (_unfilteredRoot is null) return;
        Screen = AppScreen.Insights;
    }

    [RelayCommand]
    private void ShowComparison()
    {
        if (_unfilteredRoot is null) return;
        Comparison.RefreshSnapshots();
        Screen = AppScreen.Comparison;
    }

    // ------------------------------------------------------------- chart view

    /// <summary>Show the scan as concentric rings rather than nested rectangles.</summary>
    [ObservableProperty] private bool _isSunburst;

    [RelayCommand]
    private void ToggleChart() => IsSunburst = !IsSunburst;

    /// <summary>Set by the view; hands back whatever the chart last rendered.</summary>
    public Func<Avalonia.Media.Imaging.Bitmap?>? GetChartImage { get; set; }

    [RelayCommand]
    private async Task SaveChartImageAsync()
    {
        if (PickSaveFileAsync is null || GetChartImage is null) return;

        var image = GetChartImage();
        if (image is null) return;

        var suggested = $"dirstat-{(IsSunburst ? "sunburst" : "treemap")}-{DateTime.Now:yyyyMMdd-HHmm}.png";

        string? target;
        try
        {
            target = await PickSaveFileAsync(suggested);
        }
        catch (Exception e)
        {
            StatusMessage = Loc.Format("Status.SaveDialogFailed", e.Message);
            return;
        }

        if (string.IsNullOrEmpty(target)) return;

        try
        {
            ImageExporter.SavePng(image, target);
            StatusMessage = Loc.Format("Status.ImageSaved", target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = Loc.Format("Status.ExportFailed", e.Message);
        }
    }

    // -------------------------------------------------------------- exclusions

    /// <summary>Folder names skipped wherever they appear, applied on the next scan.</summary>
    public ObservableCollection<string> Exclusions { get; } = [];

    [ObservableProperty] private bool _isExclusionsOpen;
    [ObservableProperty] private string _newExclusion = string.Empty;

    /// <summary>Names people most often want skipped, offered as one-tap additions.</summary>
    public IReadOnlyList<string> SuggestedExclusions { get; } =
        ["node_modules", "__pycache__", ".git", "bin", "obj", ".gradle", "target", ".venv"];

    [RelayCommand]
    private void ToggleExclusions()
    {
        IsExclusionsOpen = !IsExclusionsOpen;
        if (IsExclusionsOpen) IsSettingsOpen = false;
    }

    [RelayCommand]
    private void AddExclusion(string? name)
    {
        var value = (name ?? NewExclusion).Trim();
        if (value.Length == 0) return;

        if (!Exclusions.Any(e => string.Equals(e, value, StringComparison.OrdinalIgnoreCase)))
            Exclusions.Add(value);

        NewExclusion = string.Empty;
        PersistExclusions();
    }

    [RelayCommand]
    private void RemoveExclusion(string? name)
    {
        if (name is null) return;
        Exclusions.Remove(name);
        PersistExclusions();
    }

    private void PersistExclusions()
    {
        Settings.ExcludedDirectoryNames.Clear();
        Settings.ExcludedDirectoryNames.AddRange(Exclusions);
    }

    /// <summary>
    /// Brings the scan back in line with the disk after duplicates are removed.
    /// </summary>
    /// <remarks>
    /// Rescans the containing folders rather than the whole tree: the removals are scattered,
    /// but each one only changes its own parent, and rewalking a volume to reflect a handful
    /// of deletions would be absurd.
    /// </remarks>
    private async Task OnDuplicatesDeletedAsync(IReadOnlyList<FileNode> removed)
    {
        if (removed.Count == 0 || _unfilteredRoot is null) return;

        var parents = new HashSet<FileNode>();
        foreach (var node in removed)
            if (node.Parent is { } parent && IsStillAttached(parent))
                parents.Add(parent);

        // Refresh only the outermost affected folders; nested ones come along for free.
        foreach (var parent in parents.Where(p => !parents.Any(other =>
                     !ReferenceEquals(other, p) && IsDescendantOf(p, other))))
        {
            await RefreshNodeAsync(parent);
        }

        StatusMessage = Loc.Format("Status.RecoveredDuplicates", SizeFormatter.FormatCount(removed.Count));
    }

    private static bool IsDescendantOf(FileNode node, FileNode ancestor)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
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

            StatusMessage = Loc.Format("Status.Refreshed", node.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = Loc.Format("Status.RefreshFailed", e.Message);
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
            StatusMessage = Loc.Format("Status.SaveDialogFailed", e.Message);
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

            StatusMessage = Loc.Format("Status.Exported", target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = Loc.Format("Status.ExportFailed", e.Message);
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

    // ------------------------------------------------------------- language

    /// <summary>Languages offered in the title bar selector.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(AppLanguage.English, "EN", "English"),
        new(AppLanguage.German, "DE", "Deutsch"),
        new(AppLanguage.French, "FR", "Français"),
        new(AppLanguage.Spanish, "ES", "Español"),
    ];

    [ObservableProperty] private bool _isLanguageMenuOpen;

    [RelayCommand]
    private void ToggleLanguageMenu() => IsLanguageMenuOpen = !IsLanguageMenuOpen;

    [RelayCommand]
    private void SetLanguage(LanguageOption? option)
    {
        if (option is null) return;

        Loc.Current.Language = option.Language;
        Settings.Language = option.Language.ToString();
        IsLanguageMenuOpen = false;

        // Text built in code rather than bound has to be regenerated by hand.
        RefreshLocalizedText();
    }

    /// <summary>
    /// Rebuilds strings that were composed in code at the time they were set.
    /// </summary>
    /// <remarks>
    /// Bound labels retranslate themselves through the localizer's indexer, but summaries and
    /// status lines are formatted once and stored, so they would otherwise keep the wording of
    /// whichever language was active when they were produced.
    /// </remarks>
    private void RefreshLocalizedText()
    {
        if (_result is not null)
        {
            SummaryText = Loc.Format("Summary.Scan",
                SizeFormatter.Format(_result.TotalBytes),
                SizeFormatter.FormatCount(_result.TotalFiles),
                SizeFormatter.FormatCount(_result.TotalDirectories),
                SizeFormatter.FormatDuration(_result.Duration));

            DeniedText = HasDenied
                ? Loc.Format("Status.DeniedFolders", SizeFormatter.FormatCount(_result.DeniedPaths.Count))
                : string.Empty;

            Extensions.Clear();
            foreach (var stat in _result.Extensions.Take(400))
                Extensions.Add(new ExtensionViewModel(stat));
        }

        if (SelectedNode is { } node) UpdateSelectionDetail(node);

        StatusMessage = string.Empty;
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

/// <summary>A language offered in the title bar selector.</summary>
public sealed record LanguageOption(AppLanguage Language, string Code, string Name);

/// <summary>One hop in the treemap zoom trail.</summary>
public sealed class BreadcrumbViewModel(FileNode node, bool isCurrent)
{
    public FileNode Node { get; } = node;
    public bool IsCurrent { get; } = isCurrent;

    public string Label => Node.IsRoot ? Node.Name : Node.Name;
}
