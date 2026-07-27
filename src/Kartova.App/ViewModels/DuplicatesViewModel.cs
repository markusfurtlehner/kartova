using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kartova.App.Localization;
using Kartova.App.Services;
using Kartova.Core.Duplicates;
using Kartova.Core.Files;
using Kartova.Core.Model;

namespace Kartova.App.ViewModels;

/// <summary>Owns the duplicate search and its results.</summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    private readonly DuplicateFinder _finder = new();
    private CancellationTokenSource? _cancellation;
    private DuplicateResult _result = DuplicateResult.Empty;

    /// <summary>Called after the user removes copies, so the host can refresh the scan.</summary>
    public Func<IReadOnlyList<FileNode>, Task>? CopiesDeleted { get; set; }

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    // ------------------------------------------------------------------ state

    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _hasSearched;
    [ObservableProperty] private bool _showFolders;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private string _phaseText = string.Empty;
    [ObservableProperty] private string _progressDetailText = string.Empty;
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string _currentPath = string.Empty;

    /// <summary>
    /// True while the total work is still unknown. The early phases walk the tree and read
    /// prefixes without knowing how many bytes the full-hash stage will need, so a determinate
    /// bar would sit at zero and look stuck.
    /// </summary>
    [ObservableProperty] private bool _isProgressIndeterminate = true;

    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private string _fileTabText = "Files";
    [ObservableProperty] private string _folderTabText = "Folders";
    [ObservableProperty] private string _selectedText = "Nothing selected";
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyMessage = string.Empty;

    /// <summary>Files below this many KiB are ignored. Small files duplicate constantly.</summary>
    [ObservableProperty] private int _minimumSizeKb = 4;

    [ObservableProperty] private bool _verifyByteForByte;

    /// <summary>Set by the host so the search can run against the scanned tree.</summary>
    public Func<FileNode?>? GetScanRoot { get; set; }

    /// <summary>Set by the host to raise the delete confirmation shared with the main screen.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    // ----------------------------------------------------------------- search

    [RelayCommand]
    private async Task SearchAsync()
    {
        var root = GetScanRoot?.Invoke();
        if (root is null) return;

        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        IsSearching = true;
        HasSearched = true;
        StatusMessage = string.Empty;
        Groups.Clear();
        ProgressFraction = 0;
        PhaseText = Loc.T("Dup.Phase.Grouping");
        ProgressDetailText = string.Empty;

        var options = new DuplicateOptions
        {
            MinimumFileSize = Math.Max(1, MinimumSizeKb) * 1024L,
            FindDuplicateFolders = true,
            VerifyByteForByte = VerifyByteForByte,
        };

        // Constructed on the UI thread, so callbacks marshal back automatically.
        var progress = new Progress<DuplicateProgress>(OnProgress);

        try
        {
            _result = await _finder.FindAsync(root, options, progress, token);
            Populate();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.T("Dup.Cancelled");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = Loc.Format("Status.ScanFailed", e.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void CancelSearch() => _cancellation?.Cancel();

    private void OnProgress(DuplicateProgress progress)
    {
        PhaseText = progress.Phase switch
        {
            DuplicatePhase.Grouping => Loc.T("Dup.Phase.Grouping"),
            DuplicatePhase.Screening => Loc.T("Dup.Phase.Screening"),
            DuplicatePhase.Hashing => Loc.T("Dup.Phase.Hashing"),
            DuplicatePhase.MatchingFolders => Loc.T("Dup.Phase.Folders"),
            DuplicatePhase.Verifying => Loc.T("Dup.Phase.Verifying"),
            _ => Loc.T("Dup.Phase.Finishing"),
        };

        ProgressFraction = progress.Fraction;
        IsProgressIndeterminate = progress.BytesToHash <= 0;
        CurrentPath = progress.CurrentPath;

        var parts = new List<string>();
        if (progress.CandidateFiles > 0)
            parts.Add(Loc.Format("Dup.Candidates", SizeFormatter.FormatCount(progress.CandidateFiles)));
        if (progress.BytesHashed > 0)
            parts.Add(Loc.Format("Dup.BytesRead", SizeFormatter.Format(progress.BytesHashed)));
        if (progress.BytesPerSecond > 0)
            parts.Add(SizeFormatter.FormatRate(progress.BytesPerSecond));

        ProgressDetailText = string.Join("   ·   ", parts);
    }

    // ---------------------------------------------------------------- results

    private void Populate()
    {
        FileTabText = Loc.Format("Dup.FilesTab", _result.FileGroups.Count);
        FolderTabText = Loc.Format("Dup.FoldersTab", _result.FolderGroups.Count);

        var reclaimable = _result.WastedInFiles + _result.WastedInFolders;
        SummaryText = reclaimable > 0
            ? Loc.Format("Dup.Summary",
                SizeFormatter.Format(reclaimable),
                _result.FileGroups.Count + _result.FolderGroups.Count,
                SizeFormatter.Format(_result.BytesHashed),
                SizeFormatter.FormatDuration(_result.Duration))
            : Loc.Format("Dup.NoneFound", SizeFormatter.FormatDuration(_result.Duration));

        if (_result.WasCancelled) StatusMessage = Loc.T("Dup.Cancelled");

        ShowCurrentTab();
    }

    partial void OnShowFoldersChanged(bool value) => ShowCurrentTab();

    private void ShowCurrentTab()
    {
        Groups.Clear();

        // Paths are shown relative to what was scanned, so the shared prefix does not bury
        // the part that actually distinguishes one copy from another.
        var rootPath = GetScanRoot?.Invoke()?.GetFullPath() ?? string.Empty;

        var source = ShowFolders ? _result.FolderGroups : _result.FileGroups;
        foreach (var group in source)
            Groups.Add(new DuplicateGroupViewModel(group, rootPath, UpdateSelectionTotals));

        IsEmpty = Groups.Count == 0;
        EmptyMessage = ShowFolders
            ? Loc.T("Dup.EmptyFolders")
            : Loc.T("Dup.EmptyFiles");

        UpdateSelectionTotals();
    }

    [RelayCommand]
    private void ShowFileTab() => ShowFolders = false;

    [RelayCommand]
    private void ShowFolderTab() => ShowFolders = true;

    // -------------------------------------------------------------- selection

    private void UpdateSelectionTotals()
    {
        var bytes = Groups.Sum(g => g.SelectedBytes);
        var count = Groups.Sum(g => g.Copies.Count(c => c.IsSelected));

        HasSelection = count > 0;
        SelectedText = count == 0
            ? Loc.T("Dup.NothingSelected")
            : Loc.Format("Dup.Selected", SizeFormatter.FormatCount(count), SizeFormatter.Format(bytes));
    }

    [RelayCommand]
    private void KeepOldest() => ApplyRule(KeepRule.Oldest);

    [RelayCommand]
    private void KeepNewest() => ApplyRule(KeepRule.Newest);

    [RelayCommand]
    private void KeepShortestPath() => ApplyRule(KeepRule.ShortestPath);

    private void ApplyRule(KeepRule rule)
    {
        foreach (var group in Groups) group.ApplyKeepRule(rule);
        UpdateSelectionTotals();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var group in Groups) group.ClearSelection();
        UpdateSelectionTotals();
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var group in Groups) group.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var group in Groups) group.IsExpanded = false;
    }

    // --------------------------------------------------------------- deletion

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var doomed = Groups
            .SelectMany(g => g.Copies)
            .Where(c => c.IsSelected)
            .ToList();

        if (doomed.Count == 0) return;

        var bytes = doomed.Sum(c => c.Node.Size);
        var allFolders = doomed.All(c => c.IsFolder);

        var what = allFolders
            ? Plural(doomed.Count, "Dup.Folder", "Dup.FoldersPlural")
            : Plural(doomed.Count, "Dup.Item", "Dup.Items");

        var confirmed = ConfirmAsync is null || await ConfirmAsync(
            Loc.T("Dup.ConfirmTitle"),
            Loc.Format("Dup.ConfirmBody", what, SizeFormatter.Format(bytes)));

        if (!confirmed) return;

        IsSearching = true;
        try
        {
            var removed = new List<FileNode>();
            var failures = 0;

            foreach (var copy in doomed)
            {
                var result = await Task.Run(() => ShellService.MoveToTrash(copy.FullPath));
                if (result.Success) removed.Add(copy.Node);
                else failures++;
            }

            StatusMessage = failures == 0
                ? Loc.Format("Dup.Moved", Plural(removed.Count, "Dup.Item", "Dup.Items"), SizeFormatter.Format(bytes))
                : Loc.Format("Dup.MovedPartial", Plural(removed.Count, "Dup.Item", "Dup.Items"), failures);

            // Drop the removed copies, and any group that is no longer a duplicate.
            foreach (var group in Groups.ToList())
            {
                foreach (var copy in group.Copies.Where(c => removed.Contains(c.Node)).ToList())
                    group.Copies.Remove(copy);

                if (group.Copies.Count < 2) Groups.Remove(group);
            }

            IsEmpty = Groups.Count == 0;
            UpdateSelectionTotals();
            MarkResultsStale();

            if (CopiesDeleted is not null) await CopiesDeleted(removed);
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>
    /// Rewrites the headline figures after a deletion, so they cannot go on advertising space
    /// that has already been recovered.
    /// </summary>
    /// <remarks>
    /// The other tab's totals cannot be corrected without re-reading the disk, so rather than
    /// show two numbers of differing honesty, both are replaced by a prompt to search again.
    /// </remarks>
    private void MarkResultsStale()
    {
        FileTabText = ShowFolders ? Loc.T("Common.Files") : Loc.Format("Dup.FilesTab", Groups.Count);
        FolderTabText = ShowFolders ? Loc.Format("Dup.FoldersTab", Groups.Count) : Loc.T("Common.Folders");

        SummaryText = Loc.T("Dup.StaleTotals");
    }

    /// <summary>Formats a count with the right noun. "1 folders" reads like a bug.</summary>
    private static string Plural(int count, string singularKey, string pluralKey) =>
        $"{SizeFormatter.FormatCount(count)} {Loc.T(count == 1 ? singularKey : pluralKey)}";

    public void Reset()
    {
        _cancellation?.Cancel();
        _result = DuplicateResult.Empty;
        Groups.Clear();
        HasSearched = false;
        IsSearching = false;
        IsEmpty = false;
        StatusMessage = string.Empty;
        SummaryText = string.Empty;
        FileTabText = Loc.T("Common.Files");
        FolderTabText = Loc.T("Common.Folders");
        UpdateSelectionTotals();
    }
}
