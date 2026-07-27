using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Kartova.Core.Model;

namespace Kartova.App.ViewModels;

public enum TreeSortKey
{
    Size,
    Name,
    Items,
    Modified,
}

/// <summary>
/// A collection that can splice in a whole subtree with one notification.
/// </summary>
/// <remarks>
/// Expanding a folder with tens of thousands of children through the normal one-item-at-a-time
/// API would raise that many change notifications and stall the UI thread. A single reset is
/// dramatically cheaper, and the virtualizing panel only ever realises a screenful of rows.
/// </remarks>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppress;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppress) base.OnCollectionChanged(e);
    }

    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;

        _suppress = true;
        try
        {
            for (var i = 0; i < items.Count; i++) Items.Insert(index + i, items[i]);
        }
        finally
        {
            _suppress = false;
        }

        RaiseReset();
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0) return;

        _suppress = true;
        try
        {
            for (var i = 0; i < count; i++) Items.RemoveAt(index);
        }
        finally
        {
            _suppress = false;
        }

        RaiseReset();
    }

    public void ReplaceAll(IReadOnlyList<T> items)
    {
        _suppress = true;
        try
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);
        }
        finally
        {
            _suppress = false;
        }

        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>
/// Presents a scanned tree as a flat, expandable, sortable list of rows.
/// </summary>
/// <remarks>
/// Sorting never reorders <see cref="FileNode.Children"/> itself. The treemap's squarified
/// layout requires children in descending size order, so the grid keeps its own ordering and
/// leaves the underlying arrays untouched.
/// </remarks>
public sealed partial class DirectoryTreeViewModel : ObservableObject
{
    private readonly bool _showSizeOnDisk;
    private readonly Dictionary<FileNode, NodeViewModel> _rowsByNode = [];

    public DirectoryTreeViewModel(FileNode root, bool showSizeOnDisk)
    {
        _showSizeOnDisk = showSizeOnDisk;
        Root = root;

        var rootRow = CreateRow(root, depth: 0);
        Rows.ReplaceAll([rootRow]);

        // Open the first level so the scan is legible without a click.
        Expand(rootRow);
    }

    public FileNode Root { get; }

    public RangeObservableCollection<NodeViewModel> Rows { get; } = [];

    [ObservableProperty] private NodeViewModel? _selectedRow;

    [ObservableProperty] private TreeSortKey _sortKey = TreeSortKey.Size;

    [ObservableProperty] private bool _sortDescending = true;

    /// <summary>Raised when the user picks a row, so the host can sync the other panes.</summary>
    public event Action<FileNode>? RowSelected;

    partial void OnSelectedRowChanged(NodeViewModel? value)
    {
        if (value is not null) RowSelected?.Invoke(value.Node);
    }

    private NodeViewModel CreateRow(FileNode node, int depth)
    {
        var row = new NodeViewModel(node, _showSizeOnDisk, depth);
        _rowsByNode[node] = row;
        return row;
    }

    // ------------------------------------------------------ expand/collapse

    public void Toggle(NodeViewModel row)
    {
        if (row.IsExpanded) Collapse(row);
        else Expand(row);
    }

    public void Expand(NodeViewModel row)
    {
        if (row.IsExpanded || !row.HasChildren) return;

        var index = Rows.IndexOf(row);
        if (index < 0) return;

        row.IsExpanded = true;

        var children = SortChildren(row.Node);
        var inserted = new List<NodeViewModel>(children.Count);
        foreach (var child in children) inserted.Add(CreateRow(child, row.Depth + 1));

        Rows.InsertRange(index + 1, inserted);
    }

    public void Collapse(NodeViewModel row)
    {
        if (!row.IsExpanded) return;

        var index = Rows.IndexOf(row);
        if (index < 0) return;

        row.IsExpanded = false;

        // Every following row deeper than this one belongs to its subtree.
        var count = 0;
        for (var i = index + 1; i < Rows.Count && Rows[i].Depth > row.Depth; i++) count++;

        for (var i = index + 1; i <= index + count && i < Rows.Count; i++)
            _rowsByNode.Remove(Rows[i].Node);

        Rows.RemoveRange(index + 1, count);
    }

    /// <summary>Expands whatever is needed to bring <paramref name="node"/> into view, then selects it.</summary>
    public void RevealAndSelect(FileNode node)
    {
        var chain = new List<FileNode>();
        for (var current = node; current is not null; current = current.Parent) chain.Add(current);
        chain.Reverse();

        if (chain.Count == 0 || !ReferenceEquals(chain[0], Root)) return;

        // Expand every ancestor from the root down, so the target row exists.
        for (var i = 0; i < chain.Count - 1; i++)
        {
            if (!_rowsByNode.TryGetValue(chain[i], out var row)) return;
            if (!row.IsExpanded) Expand(row);
        }

        if (_rowsByNode.TryGetValue(node, out var target)) SelectedRow = target;
    }

    // ------------------------------------------------------------- sorting

    public void SetSort(TreeSortKey key)
    {
        // Clicking the active column flips direction, the way every file manager behaves.
        if (SortKey == key) SortDescending = !SortDescending;
        else
        {
            SortKey = key;
            // Sizes and counts are most useful largest-first; names read best A to Z.
            SortDescending = key != TreeSortKey.Name;
        }

        Resort();
    }

    /// <summary>Rebuilds the flat list, preserving which branches were open.</summary>
    private void Resort()
    {
        var expanded = new HashSet<FileNode>(
            _rowsByNode.Where(kv => kv.Value.IsExpanded).Select(kv => kv.Key));

        var selected = SelectedRow?.Node;

        _rowsByNode.Clear();
        var flat = new List<NodeViewModel>(Rows.Count);
        Rebuild(Root, 0, expanded, flat);
        Rows.ReplaceAll(flat);

        if (selected is not null && _rowsByNode.TryGetValue(selected, out var row)) SelectedRow = row;
    }

    private void Rebuild(FileNode node, int depth, HashSet<FileNode> expanded, List<NodeViewModel> output)
    {
        var row = CreateRow(node, depth);
        output.Add(row);

        if (!expanded.Contains(node) || !row.HasChildren) return;

        row.IsExpanded = true;
        foreach (var child in SortChildren(node)) Rebuild(child, depth + 1, expanded, output);
    }

    private List<FileNode> SortChildren(FileNode node)
    {
        var children = node.Children;
        if (children is null || children.Length == 0) return [];

        var list = new List<FileNode>(children);

        Comparison<FileNode> comparison = SortKey switch
        {
            TreeSortKey.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            TreeSortKey.Items => (a, b) => (a.FileCount + a.DirCount).CompareTo(b.FileCount + b.DirCount),
            TreeSortKey.Modified => (a, b) => a.LastWriteUtcTicks.CompareTo(b.LastWriteUtcTicks),
            _ => (a, b) => EffectiveSize(a).CompareTo(EffectiveSize(b)),
        };

        list.Sort(SortDescending ? (a, b) => comparison(b, a) : comparison);
        return list;
    }

    private long EffectiveSize(FileNode node) => _showSizeOnDisk ? node.SizeOnDisk : node.Size;

    // ------------------------------------------------- column header state

    public string SizeHeader => Header("Size", TreeSortKey.Size);
    public string NameHeader => Header("Name", TreeSortKey.Name);
    public string ItemsHeader => Header("Items", TreeSortKey.Items);
    public string ModifiedHeader => Header("Modified", TreeSortKey.Modified);

    private string Header(string label, TreeSortKey key) =>
        SortKey == key ? $"{label} {(SortDescending ? "▾" : "▴")}" : label;

    partial void OnSortKeyChanged(TreeSortKey value) => RaiseHeaders();
    partial void OnSortDescendingChanged(bool value) => RaiseHeaders();

    private void RaiseHeaders()
    {
        OnPropertyChanged(nameof(SizeHeader));
        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(ItemsHeader));
        OnPropertyChanged(nameof(ModifiedHeader));
    }
}
