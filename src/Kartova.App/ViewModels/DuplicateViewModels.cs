using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kartova.Core.Duplicates;
using Kartova.Core.Files;
using Kartova.Core.Model;
using Kartova.Core.Treemap;

namespace Kartova.App.ViewModels;

/// <summary>Which copy in each group to keep, when applying a bulk rule.</summary>
public enum KeepRule
{
    Oldest,
    Newest,
    ShortestPath,
}

/// <summary>One copy within a duplicate group.</summary>
public sealed partial class DuplicateCopyViewModel : ObservableObject
{
    private readonly Action _onSelectionChanged;

    public DuplicateCopyViewModel(FileNode node, bool isFolder, string rootPath, Action onSelectionChanged)
    {
        Node = node;
        IsFolder = isFolder;
        _onSelectionChanged = onSelectionChanged;
        FullPath = node.GetFullPath();
        Location = BuildLocation(FullPath, rootPath);
        Swatch = new SolidColorBrush(Color.FromUInt32(TreemapRenderer.ColorFor(node)));
    }

    public FileNode Node { get; }
    public bool IsFolder { get; }
    public string FullPath { get; }
    public IBrush Swatch { get; }

    /// <summary>Marked for removal. The last surviving copy can never be selected.</summary>
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged();

    public string Name => Node.Name;

    /// <summary>
    /// Where this copy lives, relative to what was scanned.
    /// </summary>
    /// <remarks>
    /// The absolute path is identical for every copy right up to the point where they
    /// diverge, so showing it in full buries the one part that matters under a prefix the
    /// user already knows. The full path is still on the tooltip.
    /// </remarks>
    public string Location { get; }

    private static string BuildLocation(string fullPath, string rootPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory)) return fullPath;

        if (!string.IsNullOrEmpty(rootPath) &&
            directory.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            var relative = directory[rootPath.Length..]
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // A copy sitting directly in the scanned folder has no relative part to show.
            return relative.Length == 0 ? "." : relative;
        }

        return directory;
    }

    public string SizeText => SizeFormatter.Format(Node.Size);

    public string ModifiedText =>
        Node.LastWriteUtcTicks == 0 ? "—" : Node.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string ContentsText => IsFolder
        ? $"{SizeFormatter.FormatCount(Node.FileCount)} files"
        : string.Empty;

    public string Tooltip => NodeViewModel.BuildTooltip(Node, showSizeOnDisk: false);
}

/// <summary>A set of identical files or folders.</summary>
public sealed partial class DuplicateGroupViewModel : ObservableObject
{
    private readonly Action _onSelectionChanged;
    private bool _updating;

    public DuplicateGroupViewModel(DuplicateGroup group, string rootPath, Action onSelectionChanged)
    {
        Group = group;
        _onSelectionChanged = onSelectionChanged;

        Copies = new ObservableCollection<DuplicateCopyViewModel>(
            group.Items.Select(item =>
                new DuplicateCopyViewModel(item, group.IsFolder, rootPath, OnCopyChanged)));
    }

    public DuplicateGroup Group { get; }
    public ObservableCollection<DuplicateCopyViewModel> Copies { get; }

    public bool IsFolder => Group.IsFolder;
    public string Name => Group.DisplayName;
    public int CopyCount => Group.CopyCount;
    public long ItemSize => Group.ItemSize;

    public string SizeText => SizeFormatter.Format(Group.ItemSize);
    public string WastedText => SizeFormatter.Format(Group.WastedBytes);
    public string CopiesText => $"{Group.CopyCount} copies";

    public IBrush Swatch => Copies.Count > 0 ? Copies[0].Swatch : Brushes.Gray;

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Expands or collapses the group. The whole header row is the target.</summary>
    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    /// <summary>Bytes freed by removing exactly what is currently ticked in this group.</summary>
    public long SelectedBytes => Copies.Count(c => c.IsSelected) * Group.ItemSize;

    public string SelectedText => SelectedBytes > 0 ? SizeFormatter.Format(SelectedBytes) : string.Empty;

    public bool HasSelection => Copies.Any(c => c.IsSelected);

    /// <summary>
    /// Applies a keep rule, ticking every copy except the one to retain.
    /// </summary>
    /// <remarks>
    /// A group always keeps one copy. Selecting every copy in a set of duplicates would
    /// destroy the data outright, which is never what "remove duplicates" means.
    /// </remarks>
    public void ApplyKeepRule(KeepRule rule)
    {
        var keeper = rule switch
        {
            KeepRule.Newest => Copies.MaxBy(c => c.Node.LastWriteUtcTicks),
            KeepRule.ShortestPath => Copies.MinBy(c => c.FullPath.Length),
            _ => Copies.MinBy(c => c.Node.LastWriteUtcTicks == 0 ? long.MaxValue : c.Node.LastWriteUtcTicks),
        } ?? Copies.FirstOrDefault();

        _updating = true;
        try
        {
            foreach (var copy in Copies) copy.IsSelected = !ReferenceEquals(copy, keeper);
        }
        finally
        {
            _updating = false;
        }

        RaiseSelectionState();
        _onSelectionChanged();
    }

    public void ClearSelection()
    {
        _updating = true;
        try
        {
            foreach (var copy in Copies) copy.IsSelected = false;
        }
        finally
        {
            _updating = false;
        }

        RaiseSelectionState();
        _onSelectionChanged();
    }

    private void OnCopyChanged()
    {
        if (_updating) return;

        // Never let the user tick every copy: one has to survive.
        if (Copies.Count > 0 && Copies.All(c => c.IsSelected))
        {
            _updating = true;
            try
            {
                Copies[0].IsSelected = false;
            }
            finally
            {
                _updating = false;
            }
        }

        RaiseSelectionState();
        _onSelectionChanged();
    }

    private void RaiseSelectionState()
    {
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(HasSelection));
    }
}
