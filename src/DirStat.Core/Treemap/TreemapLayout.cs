using DirStat.Core.Model;

namespace DirStat.Core.Treemap;

/// <summary>
/// Squarified treemap layout (Bruls, Huizing and van Wijk, 2000).
/// </summary>
/// <remarks>
/// <para>
/// The algorithm packs each directory's children into rows chosen to keep rectangles as
/// close to square as possible. Squares are what make a treemap readable: long thin slivers
/// are impossible to compare by eye and impossible to click.
/// </para>
/// <para>
/// Layout is bounded by pixels, not by tree size. Any rectangle that would fall below
/// <see cref="TreemapOptions.MinTileSide"/> is culled along with its whole subtree, so the
/// cost of laying out a ten-million-file tree is governed by the size of the window, not by
/// the number of files.
/// </para>
/// </remarks>
public static class TreemapLayout
{
    /// <summary>Lays out <paramref name="root"/> to fill a <paramref name="width"/> × <paramref name="height"/> area.</summary>
    public static TreemapModel Build(FileNode root, int width, int height, TreemapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new TreemapOptions();

        if (width <= 0 || height <= 0 || root.Size <= 0)
            return TreemapModel.Empty(root);

        // Pre-size generously: a full window rarely resolves more than a few tiles per pixel.
        var tiles = new List<TreemapTile>(Math.Min(1 << 18, width * height / 8 + 64));
        var state = new LayoutState(options, tiles);

        var bounds = new RectD(0, 0, width, height);
        tiles.Add(new TreemapTile(root, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, isLeaf: false));
        LayoutChildren(root, bounds, 1, state);

        return new TreemapModel
        {
            Root = root,
            Tiles = tiles.ToArray(),
            Width = width,
            Height = height,
            MaxDepth = state.DeepestLevel,
            CulledCount = state.Culled,
        };
    }

    private sealed class LayoutState(TreemapOptions options, List<TreemapTile> tiles)
    {
        public readonly TreemapOptions Options = options;
        public readonly List<TreemapTile> Tiles = tiles;
        public int Culled;
        public int DeepestLevel;

        /// <summary>Scratch buffer reused across rows to keep layout allocation-free.</summary>
        public readonly List<FileNode> Row = new(64);
    }

    /// <summary>Places every child of <paramref name="node"/> inside <paramref name="bounds"/>.</summary>
    private static void LayoutChildren(FileNode node, RectD bounds, int depth, LayoutState state)
    {
        var options = state.Options;
        if (options.MaxDepth > 0 && depth > options.MaxDepth) return;

        var children = node.Children;
        if (children is null || children.Length == 0) return;

        if (depth > state.DeepestLevel) state.DeepestLevel = depth;

        // Directory frames consume a pixel of padding, but only where there is room to spare.
        var inner = bounds;
        if (options.DirectoryFrames && depth > 0 && bounds.Width > 6 && bounds.Height > 6)
            inner = new RectD(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2);

        if (inner.Width < options.MinTileSide || inner.Height < options.MinTileSide) return;

        // Children arrive sorted descending by size (see FileNode.SortBySizeDescending), which
        // the squarified algorithm requires. Zero-size entries would consume no area, so they
        // are excluded from the packing rather than producing degenerate rectangles.
        var count = 0;
        long total = 0;
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i].Size <= 0) break; // sorted, so everything after is also zero
            count++;
            total += children[i].Size;
        }

        if (count == 0 || total <= 0) return;

        Squarify(children, count, total, inner, depth, state);
    }

    /// <summary>
    /// Core squarify loop. Consumes children left to right, packing each into the row that
    /// keeps the worst aspect ratio lowest, then recurses into whatever remains.
    /// </summary>
    private static void Squarify(
        FileNode[] children, int count, long totalSize, RectD area, int depth, LayoutState state)
    {
        var options = state.Options;
        var index = 0;
        var remainingSize = totalSize;
        var rect = area;

        while (index < count)
        {
            if (rect.Width < options.MinTileSide || rect.Height < options.MinTileSide)
            {
                state.Culled += count - index;
                return;
            }

            if (remainingSize <= 0) return;

            // Area units per byte for what is left to place.
            var scale = rect.Area / remainingSize;
            var shortSide = rect.ShortSide;

            // Grow the row while the worst aspect ratio keeps improving.
            var rowCount = 0;
            double rowArea = 0;
            double rowMin = double.MaxValue;
            double rowMax = 0;
            var bestWorst = double.MaxValue;

            while (index + rowCount < count)
            {
                var candidate = children[index + rowCount].Size * scale;
                var newArea = rowArea + candidate;
                var newMin = Math.Min(rowMin, candidate);
                var newMax = Math.Max(rowMax, candidate);
                var worst = Worst(newArea, newMin, newMax, shortSide);

                if (rowCount > 0 && worst > bestWorst) break;

                rowArea = newArea;
                rowMin = newMin;
                rowMax = newMax;
                bestWorst = worst;
                rowCount++;
            }

            if (rowCount == 0) rowCount = 1; // always make progress

            rect = PlaceRow(children, index, rowCount, rowArea, rect, depth, state);

            for (var i = 0; i < rowCount; i++) remainingSize -= children[index + i].Size;
            index += rowCount;
        }
    }

    /// <summary>
    /// Worst (largest) aspect ratio in a row of the given total area, laid across
    /// <paramref name="side"/>. Straight from the paper.
    /// </summary>
    private static double Worst(double rowArea, double minArea, double maxArea, double side)
    {
        if (rowArea <= 0 || side <= 0 || minArea <= 0) return double.MaxValue;
        var s2 = rowArea * rowArea;
        var w2 = side * side;
        return Math.Max(w2 * maxArea / s2, s2 / (w2 * minArea));
    }

    /// <summary>
    /// Emits one row of tiles along the shorter side and returns the rectangle left over.
    /// </summary>
    private static RectD PlaceRow(
        FileNode[] children, int start, int rowCount, double rowArea, RectD rect, int depth, LayoutState state)
    {
        var options = state.Options;
        var horizontal = rect.Width >= rect.Height;

        // Row thickness follows from its area and the side it spans.
        var span = horizontal ? rect.Height : rect.Width;
        var thickness = span > 0 ? rowArea / span : 0;

        // Never overrun the parent; floating point can nudge the last row past the edge.
        thickness = Math.Min(thickness, horizontal ? rect.Width : rect.Height);
        if (thickness <= 0) return RectD.Empty;

        double offset = 0;
        double placedSize = 0;
        for (var i = 0; i < rowCount; i++) placedSize += children[start + i].Size;
        if (placedSize <= 0) return rect;

        for (var i = 0; i < rowCount; i++)
        {
            var child = children[start + i];

            // Distribute the span proportionally, giving the final tile the exact remainder
            // so the row always closes flush against the parent edge.
            var isLast = i == rowCount - 1;
            var extent = isLast ? span - offset : span * (child.Size / placedSize);
            if (extent < 0) extent = 0;

            var tile = horizontal
                ? new RectD(rect.X, rect.Y + offset, thickness, extent)
                : new RectD(rect.X + offset, rect.Y, extent, thickness);

            offset += extent;

            if (tile.Width < options.MinTileSide || tile.Height < options.MinTileSide)
            {
                state.Culled++;
                continue;
            }

            var hasChildren = child.Children is { Length: > 0 } && child.Size > 0;
            var willRecurse = hasChildren &&
                              (options.MaxDepth == 0 || depth < options.MaxDepth) &&
                              tile.Width > options.MinTileSide * 3 &&
                              tile.Height > options.MinTileSide * 3;

            state.Tiles.Add(new TreemapTile(
                child, tile.X, tile.Y, tile.Width, tile.Height, depth, isLeaf: !willRecurse));

            if (willRecurse) LayoutChildren(child, tile, depth + 1, state);
        }

        // Remaining area is the parent minus the strip just consumed.
        return horizontal
            ? new RectD(rect.X + thickness, rect.Y, rect.Width - thickness, rect.Height)
            : new RectD(rect.X, rect.Y + thickness, rect.Width, rect.Height - thickness);
    }
}
