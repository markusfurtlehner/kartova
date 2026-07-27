using DirStat.Core.Model;

namespace DirStat.Core.Treemap;

/// <summary>An axis-aligned rectangle in device pixels.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double Area => Width * Height;
    public double ShortSide => Math.Min(Width, Height);

    public bool Contains(double px, double py) =>
        px >= X && px < X + Width && py >= Y && py < Y + Height;

    public static readonly RectD Empty = new(0, 0, 0, 0);
}

/// <summary>One laid-out rectangle in the treemap.</summary>
public readonly struct TreemapTile
{
    public readonly FileNode Node;
    public readonly float X;
    public readonly float Y;
    public readonly float Width;
    public readonly float Height;

    /// <summary>Nesting level below the treemap root; the root itself is 0.</summary>
    public readonly short Depth;

    /// <summary>True when the tile has no children drawn inside it — a file, or a culled directory.</summary>
    public readonly bool IsLeaf;

    public TreemapTile(FileNode node, double x, double y, double width, double height, int depth, bool isLeaf)
    {
        Node = node;
        X = (float)x;
        Y = (float)y;
        Width = (float)width;
        Height = (float)height;
        Depth = (short)depth;
        IsLeaf = isLeaf;
    }

    public bool Contains(double px, double py) =>
        px >= X && px < X + Width && py >= Y && py < Y + Height;

    public RectD Bounds => new(X, Y, Width, Height);
}

/// <summary>
/// A completed treemap layout: every tile, in depth-first pre-order.
/// </summary>
/// <remarks>
/// Pre-order matters. The renderer relies on a parent always being emitted before its
/// children so it can carry the cushion surface down the tree in a small per-depth array
/// instead of storing four coefficients on every tile.
/// </remarks>
public sealed class TreemapModel
{
    public required FileNode Root { get; init; }
    public required TreemapTile[] Tiles { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int MaxDepth { get; init; }

    /// <summary>Tiles culled because they fell below one pixel. Reported for diagnostics.</summary>
    public required int CulledCount { get; init; }

    public static TreemapModel Empty(FileNode root) => new()
    {
        Root = root,
        Tiles = [],
        Width = 0,
        Height = 0,
        MaxDepth = 0,
        CulledCount = 0,
    };
}

/// <summary>Tunables for layout and cushion rendering.</summary>
public sealed class TreemapOptions
{
    /// <summary>
    /// Apply WinDirStat-style cushion shading. This is what makes nested structure legible
    /// in a dense map; flat fills lose all sense of hierarchy.
    /// </summary>
    public bool CushionShading { get; set; } = true;

    /// <summary>Height of the ridge added at each nesting level.</summary>
    public double CushionHeight { get; set; } = 0.88;

    /// <summary>Per-level decay of the ridge height, so deep nesting stays subtle.</summary>
    public double CushionDecay { get; set; } = 0.78;

    /// <summary>Ambient light floor, keeping shadowed faces from going fully black.</summary>
    public double Ambient { get; set; } = 0.32;

    /// <summary>Draw an inset frame around directories that are large enough to show one.</summary>
    public bool DirectoryFrames { get; set; } = true;

    /// <summary>Rectangles with either side below this many pixels are not drawn.</summary>
    public double MinTileSide { get; set; } = 1.0;

    /// <summary>Stop descending past this depth. Zero means unlimited.</summary>
    public int MaxDepth { get; set; }

    /// <summary>Light direction. Defaults to upper-left, matching WinDirStat.</summary>
    public double LightX { get; set; } = -0.65;
    public double LightY { get; set; } = -0.50;
    public double LightZ { get; set; } = 0.57;

    public TreemapOptions Clone() => (TreemapOptions)MemberwiseClone();
}
