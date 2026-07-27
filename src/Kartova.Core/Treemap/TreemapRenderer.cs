using Kartova.Core.Files;
using Kartova.Core.Model;

namespace Kartova.Core.Treemap;

/// <summary>
/// The rasterized output of a treemap: a pixel buffer plus a parallel buffer naming the
/// tile that owns each pixel.
/// </summary>
/// <remarks>
/// The owner buffer is what makes interaction free. Hit-testing is an array index rather
/// than a tree walk, so hover, tooltips and click-to-select cost the same whether the map
/// holds a thousand tiles or a million. It also means selection highlights can be drawn as
/// an overlay without ever re-rasterizing.
/// </remarks>
public sealed class TreemapRaster
{
    public required uint[] Pixels { get; init; }

    /// <summary>Index into <see cref="TreemapModel.Tiles"/>, or -1 where nothing is drawn.</summary>
    public required int[] Owners { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required TreemapModel Model { get; init; }

    /// <summary>Tile at a pixel, or null when the point is outside the map.</summary>
    public TreemapTile? HitTest(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return null;
        var owner = Owners[y * Width + x];
        if (owner < 0 || owner >= Model.Tiles.Length) return null;
        return Model.Tiles[owner];
    }

    /// <summary>Node at a pixel, or null when the point is outside the map.</summary>
    public FileNode? NodeAt(int x, int y) => HitTest(x, y)?.Node;
}

/// <summary>
/// Rasterizes a <see cref="TreemapModel"/> with WinDirStat-style cushion shading.
/// </summary>
/// <remarks>
/// <para>
/// Each nesting level adds a parabolic ridge to a height field described by four
/// coefficients over x², y², x and y. Differentiating that surface at a pixel gives a
/// surface normal, and lighting it produces the rounded, lit-from-upper-left look that makes
/// hierarchy visible without any borders at all.
/// </para>
/// <para>
/// Tiles arrive in depth-first pre-order, so the surface is carried down the tree in a small
/// per-depth array. That keeps the coefficients off the tiles themselves, saving 16 bytes on
/// every one of what can be hundreds of thousands of rectangles.
/// </para>
/// </remarks>
public sealed class TreemapRenderer
{
    /// <summary>Renders a model into freshly allocated buffers.</summary>
    public TreemapRaster Render(TreemapModel model, TreemapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new TreemapOptions();

        var width = model.Width;
        var height = model.Height;
        if (width <= 0 || height <= 0)
        {
            return new TreemapRaster
            {
                Pixels = [], Owners = [], Width = 0, Height = 0, Model = model,
            };
        }

        var pixels = new uint[width * height];
        var owners = new int[width * height];
        Array.Fill(owners, -1);

        Render(model, options, pixels, owners);

        return new TreemapRaster
        {
            Pixels = pixels,
            Owners = owners,
            Width = width,
            Height = height,
            Model = model,
        };
    }

    /// <summary>Renders into caller-supplied buffers, avoiding reallocation on resize.</summary>
    /// <remarks>
    /// Rendering splits at depth 1. In pre-order a top-level child and all its descendants
    /// occupy one contiguous run of the tile array, and those runs paint into disjoint
    /// rectangles — so they can be rasterized concurrently with no locking and no risk of
    /// two threads touching the same pixel.
    /// </remarks>
    public void Render(TreemapModel model, TreemapOptions options, uint[] pixels, int[] owners)
    {
        var tiles = model.Tiles;
        if (tiles.Length == 0) return;

        var context = new RenderContext(
            options, NormalizeLight(options), pixels, owners, model.Width, model.Height,
            Math.Max(model.MaxDepth + 2, 4));

        // The root establishes the surface every subtree builds on.
        var rootSurface = new double[4];
        var first = 0;
        if (tiles[0].Depth == 0)
        {
            if (options.CushionShading) AddRidge(rootSurface, tiles[0], options.CushionHeight);
            PaintTile(tiles[0], 0, rootSurface, context);
            first = 1;
        }

        var ranges = FindTopLevelRanges(tiles, first);

        if (ranges.Count <= 1)
        {
            foreach (var range in ranges) RenderRange(tiles, range, rootSurface, context);
            return;
        }

        Parallel.ForEach(
            ranges,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            range => RenderRange(tiles, range, rootSurface, context));
    }

    /// <summary>
    /// Splits the tile array into one contiguous run per top-level child.
    /// </summary>
    private static List<(int Start, int End)> FindTopLevelRanges(TreemapTile[] tiles, int first)
    {
        var ranges = new List<(int, int)>(16);
        var start = -1;

        for (var i = first; i < tiles.Length; i++)
        {
            if (tiles[i].Depth != 1) continue;
            if (start >= 0) ranges.Add((start, i));
            start = i;
        }

        if (start >= 0) ranges.Add((start, tiles.Length));
        else if (first < tiles.Length) ranges.Add((first, tiles.Length));

        return ranges;
    }

    private void RenderRange(
        TreemapTile[] tiles, (int Start, int End) range, double[] rootSurface, RenderContext context)
    {
        var options = context.Options;

        // Surface stack local to this thread. Index d holds the surface of the tile at depth d,
        // which is exactly what a tile at depth d+1 needs as its parent.
        var surfaces = new double[context.DepthCapacity][];
        for (var i = 0; i < surfaces.Length; i++) surfaces[i] = new double[4];
        Array.Copy(rootSurface, surfaces[1], 4);

        for (var t = range.Start; t < range.End; t++)
        {
            ref readonly var tile = ref tiles[t];
            if (tile.Width <= 0 || tile.Height <= 0) continue;

            var depth = tile.Depth;
            if (depth + 1 >= context.DepthCapacity) continue;

            var own = surfaces[depth + 1];

            if (options.CushionShading)
            {
                Array.Copy(surfaces[depth], own, 4);
                AddRidge(own, tile, options.CushionHeight * Math.Pow(options.CushionDecay, depth));
            }

            PaintTile(tile, t, own, context);
        }
    }

    /// <summary>
    /// Paints one tile. Interior nodes are filled as well as framed, not merely outlined:
    /// children that fell below a pixel were culled, and without a fill those gaps would
    /// read as holes in the map. Children are painted afterwards and cover the interior.
    /// </summary>
    private static void PaintTile(in TreemapTile tile, int index, double[] surface, RenderContext context)
    {
        // An interior fill survives only in the gaps left by culled sub-pixel children, so it
        // gets a flat fill rather than per-pixel lighting. The cushion would be invisible under
        // the children that cover it, and computing it at every level doubled render time.
        var cushion = context.Options.CushionShading && tile.IsLeaf;

        FillTile(tile, index, surface, context.Options, context.Light, cushion,
            context.Pixels, context.Owners, context.Width, context.Height);

        if (!tile.IsLeaf && context.Options.DirectoryFrames)
            DrawFrame(tile, index, context.Pixels, context.Owners, context.Width, context.Height);
    }

    private readonly record struct RenderContext(
        TreemapOptions Options,
        Light Light,
        uint[] Pixels,
        int[] Owners,
        int Width,
        int Height,
        int DepthCapacity);

    /// <summary>
    /// Snaps a tile's floating-point bounds to whole pixels.
    /// </summary>
    /// <remarks>
    /// Rounding to nearest rather than expanding with floor/ceil is what keeps the owner
    /// buffer exact. Because rounding is monotonic, one tile's right edge lands on precisely
    /// the same pixel column as its neighbour's left edge — so adjacent tiles neither
    /// overlap nor leave a seam, and every pixel has exactly one truthful owner.
    /// </remarks>
    private static (int X0, int Y0, int X1, int Y1) PixelBounds(in TreemapTile tile, int width, int height)
    {
        var x0 = Math.Clamp((int)Math.Round(tile.X, MidpointRounding.AwayFromZero), 0, width);
        var y0 = Math.Clamp((int)Math.Round(tile.Y, MidpointRounding.AwayFromZero), 0, height);
        var x1 = Math.Clamp((int)Math.Round(tile.X + tile.Width, MidpointRounding.AwayFromZero), 0, width);
        var y1 = Math.Clamp((int)Math.Round(tile.Y + tile.Height, MidpointRounding.AwayFromZero), 0, height);
        return (x0, y0, x1, y1);
    }

    private readonly record struct Light(double X, double Y, double Z);

    private static Light NormalizeLight(TreemapOptions options)
    {
        var x = options.LightX;
        var y = options.LightY;
        var z = options.LightZ;
        var length = Math.Sqrt(x * x + y * y + z * z);
        if (length <= double.Epsilon) return new Light(0, 0, 1);
        return new Light(x / length, y / length, z / length);
    }

    /// <summary>
    /// Adds a parabolic ridge spanning the tile to the accumulated height field.
    /// </summary>
    /// <remarks>
    /// The surface is stored as coefficients of x², y², x and y. Adding a ridge that peaks in
    /// the middle of the tile and falls to zero at its edges is what gives each rectangle its
    /// rounded, pillow-like face; accumulating ridges from every ancestor is what makes the
    /// nesting readable.
    /// </remarks>
    private static void AddRidge(double[] surface, in TreemapTile tile, double height)
    {
        double left = tile.X, right = tile.X + tile.Width;
        double top = tile.Y, bottom = tile.Y + tile.Height;

        var spanX = right - left;
        if (spanX > 0)
        {
            surface[2] += 4 * height * (right + left) / spanX;
            surface[0] -= 4 * height / spanX;
        }

        var spanY = bottom - top;
        if (spanY > 0)
        {
            surface[3] += 4 * height * (bottom + top) / spanY;
            surface[1] -= 4 * height / spanY;
        }
    }

    private static void FillTile(
        in TreemapTile tile, int tileIndex, double[] surface, TreemapOptions options,
        in Light light, bool cushion, uint[] pixels, int[] owners, int width, int height)
    {
        var baseColor = ColorFor(tile.Node);

        var (x0, y0, x1, y1) = PixelBounds(tile, width, height);
        if (x1 <= x0 || y1 <= y0) return;

        var br = (baseColor >> 16) & 0xFF;
        var bg = (baseColor >> 8) & 0xFF;
        var bb = baseColor & 0xFF;

        if (!cushion)
        {
            for (var y = y0; y < y1; y++)
            {
                var row = y * width;
                for (var x = x0; x < x1; x++)
                {
                    pixels[row + x] = baseColor;
                    owners[row + x] = tileIndex;
                }
            }
            return;
        }

        var ambient = options.Ambient;
        var diffuse = 1.0 - ambient;

        var coefX2 = surface[0];
        var coefY2 = surface[1];
        var coefX = surface[2];
        var coefY = surface[3];

        for (var y = y0; y < y1; y++)
        {
            var row = y * width;
            var py = y + 0.5;

            // The y component of the normal is constant across a scanline.
            var ny = -(2 * coefY2 * py + coefY);
            var nyLight = ny * light.Y;
            var nySquared = ny * ny;

            for (var x = x0; x < x1; x++)
            {
                var px = x + 0.5;
                var nx = -(2 * coefX2 * px + coefX);

                // Lambertian term against the unit normal (nx, ny, 1).
                var cosine = (nx * light.X + nyLight + light.Z)
                             / Math.Sqrt(nx * nx + nySquared + 1.0);
                if (cosine < 0) cosine = 0;

                var intensity = ambient + diffuse * cosine;

                pixels[row + x] = 0xFF000000u
                                | ((uint)Math.Min(255, (int)(br * intensity)) << 16)
                                | ((uint)Math.Min(255, (int)(bg * intensity)) << 8)
                                | (uint)Math.Min(255, (int)(bb * intensity));
                owners[row + x] = tileIndex;
            }
        }
    }

    /// <summary>
    /// Outlines an interior node so directory boundaries stay legible, the way SpaceSniffer
    /// frames nested folders. Drawn before children, which then cover the interior.
    /// </summary>
    private static void DrawFrame(
        in TreemapTile tile, int tileIndex, uint[] pixels, int[] owners, int width, int height)
    {
        var (x0, y0, x1, y1) = PixelBounds(tile, width, height);
        if (x1 - x0 < 3 || y1 - y0 < 3) return;

        const uint frame = 0xFF10151F;

        for (var x = x0; x < x1; x++)
        {
            var top = y0 * width + x;
            pixels[top] = frame;
            owners[top] = tileIndex;

            var bottom = (y1 - 1) * width + x;
            pixels[bottom] = frame;
            owners[bottom] = tileIndex;
        }

        for (var y = y0; y < y1; y++)
        {
            var row = y * width;
            pixels[row + x0] = frame;
            owners[row + x0] = tileIndex;
            pixels[row + x1 - 1] = frame;
            owners[row + x1 - 1] = tileIndex;
        }
    }

    /// <summary>Base colour for a node before shading.</summary>
    public static uint ColorFor(FileNode node)
    {
        if (node.HasFlag(NodeFlags.FreeSpace)) return FileTypeColors.FreeSpace;
        if (node.HasFlag(NodeFlags.Unknown)) return FileTypeColors.UnknownSpace;
        if (node.IsDirectory) return FileTypeColors.Directory;
        // Span overload: this runs once per tile per render, so it must not allocate.
        return FileTypeColors.ForExtension(node.ExtensionSpan);
    }
}
