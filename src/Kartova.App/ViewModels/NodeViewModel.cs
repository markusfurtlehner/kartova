using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Kartova.Core.Files;
using Kartova.Core.Model;
using Kartova.Core.Treemap;

namespace Kartova.App.ViewModels;

/// <summary>
/// One visible row in the directory grid.
/// </summary>
/// <remarks>
/// Rows exist only for branches the user has actually expanded. The grid is driven by a flat
/// list of these (see <see cref="DirectoryTreeViewModel"/>) rather than by a nested
/// hierarchy, which is what lets a standard virtualizing list render a scan holding millions
/// of nodes without materialising more than a screenful of objects.
/// </remarks>
public sealed partial class NodeViewModel : ObservableObject
{
    private readonly bool _showSizeOnDisk;

    public NodeViewModel(FileNode node, bool showSizeOnDisk, int depth = 0)
    {
        Node = node;
        _showSizeOnDisk = showSizeOnDisk;
        Depth = depth;
        var color = TreemapRenderer.ColorFor(node);
        Swatch = new SolidColorBrush(Color.FromUInt32(color));
        BarBrush = node.IsDirectory && !node.IsSynthetic
            ? new SolidColorBrush(Color.FromUInt32(0xFF4C8DFF))
            : Swatch;
    }

    public FileNode Node { get; }

    /// <summary>Nesting level, used only to indent the name cell.</summary>
    public int Depth { get; }

    /// <summary>
    /// Left padding that expresses the hierarchy in a flat list. Kept tight, because deep
    /// paths otherwise squeeze the name column to nothing before the user can widen the pane.
    /// </summary>
    public Thickness Indent => new(Depth * 11, 0, 0, 0);

    [ObservableProperty] private bool _isExpanded;

    public bool HasChildren => Node.Children is { Length: > 0 };

    /// <summary>Disclosure glyph, or blank for a leaf.</summary>
    public string Chevron => !HasChildren ? string.Empty : IsExpanded ? "⌄" : "›";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Chevron));

    public string Name => Node.Name;

    public long EffectiveSize => _showSizeOnDisk ? Node.SizeOnDisk : Node.Size;

    public string SizeText => SizeFormatter.Format(EffectiveSize);

    /// <summary>Share of the parent directory, 0..1. Drives the inline bar.</summary>
    public double Fraction
    {
        get
        {
            var parent = Node.Parent;
            if (parent is null) return 1;
            var parentSize = _showSizeOnDisk ? parent.SizeOnDisk : parent.Size;
            return parentSize <= 0 ? 0 : Math.Clamp((double)EffectiveSize / parentSize, 0, 1);
        }
    }

    public string PercentText => SizeFormatter.FormatPercent(Fraction);

    public string ItemsText => Node.IsDirectory
        ? SizeFormatter.FormatCount(Node.FileCount + Node.DirCount)
        : string.Empty;

    public string LastModifiedText =>
        Node.LastWriteUtcTicks == 0 ? string.Empty : Node.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>Colour swatch matching the treemap, so the two panes read as one dataset.</summary>
    public IBrush Swatch { get; }

    /// <summary>
    /// Fill for the share bar.
    /// </summary>
    /// <remarks>
    /// Directories carry a deliberately muted colour in the treemap so file types stand out
    /// against them. That same muted slate is invisible as a thin bar on a dark track, so
    /// bars for directories use the accent instead of the swatch.
    /// </remarks>
    public IBrush BarBrush { get; }

    public bool IsDenied => Node.HasFlag(NodeFlags.AccessDenied);

    public string FullPath => Node.GetFullPath();

    /// <summary>Multi-line detail shown on hover, in both the grid and the treemap.</summary>
    public string Tooltip => BuildTooltip(Node, _showSizeOnDisk);

    public static string BuildTooltip(FileNode node, bool showSizeOnDisk)
    {
        var lines = new List<string> { node.GetFullPath(), string.Empty };

        lines.Add($"Size          {SizeFormatter.Format(node.Size)}");
        if (node.SizeOnDisk != node.Size)
            lines.Add($"On disk       {SizeFormatter.Format(node.SizeOnDisk)}");

        if (node.IsDirectory)
        {
            lines.Add($"Files         {SizeFormatter.FormatCount(node.FileCount)}");
            lines.Add($"Directories   {SizeFormatter.FormatCount(node.DirCount)}");
        }

        if (node.Parent is not null)
            lines.Add($"Of parent     {SizeFormatter.FormatPercent(node.FractionOfParent)}");

        if (node.LastWriteUtcTicks != 0)
            lines.Add($"Modified      {node.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm}");

        if (node.HasFlag(NodeFlags.AccessDenied))
            lines.Add("\nAccess denied — contents are not included.");
        if (node.HasFlag(NodeFlags.ReparsePoint))
            lines.Add("\nLink — not followed, so its target is counted only where it really lives.");
        if (node.HasFlag(NodeFlags.HardLinkDuplicate))
            lines.Add("\nHard link — the bytes are counted at the first path found.");
        if (node.HasFlag(NodeFlags.Unknown))
            lines.Add("\nSpace the volume reports as used but the scan could not see.");
        if (node.HasFlag(NodeFlags.FreeSpace))
            lines.Add("\nUnallocated space on this volume.");

        _ = showSizeOnDisk;
        return string.Join('\n', lines);
    }

    public override string ToString() => Name;
}
