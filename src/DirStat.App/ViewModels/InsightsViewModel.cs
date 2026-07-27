using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirStat.App.Localization;
using DirStat.App.Services;
using DirStat.Core.Files;
using DirStat.Core.Insights;
using DirStat.Core.Model;
using DirStat.Core.Treemap;

namespace DirStat.App.ViewModels;

/// <summary>One item inside an insight group.</summary>
public sealed partial class InsightItemViewModel : ObservableObject
{
    private readonly Action _onSelectionChanged;

    public InsightItemViewModel(Insight insight, string rootPath, Action onSelectionChanged)
    {
        Insight = insight;
        _onSelectionChanged = onSelectionChanged;
        FullPath = insight.Node.GetFullPath();
        RelativePath = MakeRelative(FullPath, rootPath);
        Swatch = new SolidColorBrush(Color.FromUInt32(TreemapRenderer.ColorFor(insight.Node)));
    }

    public Insight Insight { get; }
    public FileNode Node => Insight.Node;
    public string FullPath { get; }
    public string RelativePath { get; }
    public IBrush Swatch { get; }

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged();

    public string SizeText => Node.Size > 0 ? SizeFormatter.Format(Node.Size) : "—";

    public string DetailText => Node.LastWriteUtcTicks == 0
        ? string.Empty
        : Node.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd");

    private static string MakeRelative(string full, string root)
    {
        if (string.IsNullOrEmpty(root) || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return full;

        var relative = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Length == 0 ? full : relative;
    }
}

/// <summary>A category of findings, with the explanation that goes with it.</summary>
public sealed partial class InsightGroupViewModel : ObservableObject
{
    private readonly Action _onSelectionChanged;
    private bool _updating;

    public InsightGroupViewModel(InsightGroup group, string rootPath, Action onSelectionChanged)
    {
        Group = group;
        _onSelectionChanged = onSelectionChanged;

        Items = new ObservableCollection<InsightItemViewModel>(
            group.Items.Select(i => new InsightItemViewModel(i, rootPath, OnItemChanged)));
    }

    public InsightGroup Group { get; }
    public ObservableCollection<InsightItemViewModel> Items { get; }

    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public string Title => Group.Category is { } category
        ? Loc.T(category.Id)
        : Loc.T($"Insights.{Group.Kind}");

    public string Help => Group.Category is { } category
        ? Loc.T($"Confidence.{category.Confidence}Help")
        : Loc.T($"Insights.{Group.Kind}Help");

    /// <summary>Short badge naming how safe this category is to act on.</summary>
    public string ConfidenceLabel => Group.Category is { } category
        ? Loc.T($"Confidence.{category.Confidence}")
        : string.Empty;

    public bool HasConfidence => Group.Category is not null;

    /// <summary>
    /// Badge colour by confidence: green for rebuildable, amber for likely, red for review.
    /// A person should be able to tell how careful to be without reading a word.
    /// </summary>
    public IBrush ConfidenceBrush => new SolidColorBrush(Color.FromUInt32(
        Group.Category?.Confidence switch
        {
            JunkConfidence.Rebuildable => 0xFF37D6B0,
            JunkConfidence.Likely => 0xFFFFB24C,
            JunkConfidence.Review => 0xFFFF6B8A,
            _ => 0xFF8A93A6,
        }));

    public string SizeText => SizeFormatter.Format(Group.TotalBytes);
    public string CountText => Loc.Format("Insights.Items", SizeFormatter.FormatCount(Group.Count));

    public long SelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.Node.Size);
    public bool HasSelection => Items.Any(i => i.IsSelected);

    public void SelectAll(bool selected)
    {
        _updating = true;
        try
        {
            foreach (var item in Items) item.IsSelected = selected;
        }
        finally
        {
            _updating = false;
        }

        RaiseSelectionState();
        _onSelectionChanged();
    }

    private void OnItemChanged()
    {
        if (_updating) return;
        RaiseSelectionState();
        _onSelectionChanged();
    }

    private void RaiseSelectionState()
    {
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(HasSelection));
    }
}

/// <summary>Owns the insights pass and its results.</summary>
public sealed partial class InsightsViewModel : ObservableObject
{
    private InsightResult _result = InsightResult.Empty;

    public ObservableCollection<InsightGroupViewModel> Groups { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasRun;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private string _selectedText = string.Empty;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Years after which a file counts as stale, as shown on the slider.</summary>
    [ObservableProperty] private int _staleYears = 2;

    public Func<FileNode?>? GetScanRoot { get; set; }
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }
    public Func<IReadOnlyList<FileNode>, Task>? ItemsDeleted { get; set; }

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        var root = GetScanRoot?.Invoke();
        if (root is null) return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var options = new InsightOptions
            {
                StaleAfter = TimeSpan.FromDays(365.0 * Math.Max(1, StaleYears)),
            };

            _result = await Task.Run(() => InsightAnalyzer.Analyze(root, options));
            Populate(root);
            HasRun = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Populate(FileNode root)
    {
        Groups.Clear();

        var rootPath = root.GetFullPath();
        foreach (var group in _result.Groups)
            Groups.Add(new InsightGroupViewModel(group, rootPath, UpdateSelection));

        IsEmpty = Groups.Count == 0;
        SummaryText = IsEmpty
            ? Loc.T("Insights.Empty")
            : Loc.Format("Insights.Reclaimable",
                SizeFormatter.Format(_result.TotalBytes), Groups.Count);

        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var bytes = Groups.Sum(g => g.SelectedBytes);
        var count = Groups.Sum(g => g.Items.Count(i => i.IsSelected));

        HasSelection = count > 0;
        SelectedText = count == 0
            ? Loc.T("Dup.NothingSelected")
            : Loc.Format("Dup.Selected", SizeFormatter.FormatCount(count), SizeFormatter.Format(bytes));
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var group in Groups) group.SelectAll(false);
        UpdateSelection();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var doomed = Groups.SelectMany(g => g.Items).Where(i => i.IsSelected).ToList();
        if (doomed.Count == 0) return;

        var bytes = doomed.Sum(i => i.Node.Size);

        var confirmed = ConfirmAsync is null || await ConfirmAsync(
            Loc.T("Dup.ConfirmTitle"),
            Loc.Format("Dup.ConfirmBody",
                Loc.Format("Insights.Items", SizeFormatter.FormatCount(doomed.Count)),
                SizeFormatter.Format(bytes)));

        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var removed = new List<FileNode>();
            var failures = 0;

            foreach (var item in doomed)
            {
                var result = await Task.Run(() => ShellService.MoveToTrash(item.FullPath));
                if (result.Success) removed.Add(item.Node);
                else failures++;
            }

            StatusMessage = failures == 0
                ? Loc.Format("Dup.Moved",
                    Loc.Format("Insights.Items", SizeFormatter.FormatCount(removed.Count)),
                    SizeFormatter.Format(bytes))
                : Loc.Format("Dup.MovedPartial",
                    Loc.Format("Insights.Items", SizeFormatter.FormatCount(removed.Count)), failures);

            foreach (var group in Groups.ToList())
            {
                foreach (var item in group.Items.Where(i => removed.Contains(i.Node)).ToList())
                    group.Items.Remove(item);

                if (group.Items.Count == 0) Groups.Remove(group);
            }

            IsEmpty = Groups.Count == 0;
            UpdateSelection();

            if (ItemsDeleted is not null) await ItemsDeleted(removed);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Reset()
    {
        _result = InsightResult.Empty;
        Groups.Clear();
        HasRun = false;
        IsEmpty = false;
        SummaryText = string.Empty;
        StatusMessage = string.Empty;
        UpdateSelection();
    }
}
