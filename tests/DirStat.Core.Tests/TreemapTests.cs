using DirStat.Core.Model;
using DirStat.Core.Treemap;
using Xunit;

namespace DirStat.Core.Tests;

public class TreemapTests
{
    /// <summary>Builds an in-memory tree from (name, size) leaves, sized and sorted like a real scan.</summary>
    private static FileNode Tree(params (string Name, long Size)[] leaves)
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root)
        {
            Children = leaves.Select(l => new FileNode(l.Name) { Size = l.Size }).ToArray(),
        };
        foreach (var child in root.Children!)
        {
            child.Parent = root;
            root.Size += child.Size;
        }
        root.SortBySizeDescending();
        return root;
    }

    private static FileNode NestedTree()
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root);
        var branches = new List<FileNode>();

        for (var i = 0; i < 5; i++)
        {
            var dir = new FileNode($"dir{i}", NodeFlags.Directory) { Parent = root };
            var kids = new List<FileNode>();
            for (var j = 0; j < 6; j++)
            {
                var file = new FileNode($"f{i}_{j}.dat") { Parent = dir, Size = (i + 1) * (j + 1) * 1000 };
                kids.Add(file);
                dir.Size += file.Size;
            }
            dir.Children = kids.ToArray();
            branches.Add(dir);
            root.Size += dir.Size;
        }

        root.Children = branches.ToArray();
        root.SortBySizeDescending();
        return root;
    }

    /// <summary>Flat layout options: no frames, no culling, so coverage can be checked exactly.</summary>
    private static TreemapOptions Exact() => new()
    {
        DirectoryFrames = false,
        CushionShading = false,
        MinTileSide = 0.0001,
    };

    [Fact]
    public void Leaf_tiles_cover_the_entire_canvas()
    {
        var root = Tree(("a", 5000), ("b", 3000), ("c", 1500), ("d", 500));
        var model = TreemapLayout.Build(root, 800, 600, Exact());

        var covered = model.Tiles
            .Where(t => t.Depth > 0)
            .Sum(t => (double)t.Width * t.Height);

        Assert.Equal(800.0 * 600.0, covered, precision: 3);
    }

    [Fact]
    public void Tile_area_is_proportional_to_node_size()
    {
        var root = Tree(("a", 6000), ("b", 3000), ("c", 1000));
        var model = TreemapLayout.Build(root, 1000, 1000, Exact());

        var canvas = 1000.0 * 1000.0;
        foreach (var tile in model.Tiles.Where(t => t.Depth == 1))
        {
            var expected = canvas * tile.Node.Size / root.Size;
            var actual = (double)tile.Width * tile.Height;
            Assert.Equal(expected, actual, precision: 2);
        }
    }

    [Fact]
    public void Sibling_tiles_never_overlap()
    {
        var model = TreemapLayout.Build(NestedTree(), 900, 700, Exact());
        var leaves = model.Tiles.Where(t => t.IsLeaf).ToArray();

        for (var i = 0; i < leaves.Length; i++)
        {
            for (var j = i + 1; j < leaves.Length; j++)
            {
                Assert.False(Intersects(leaves[i], leaves[j]),
                    $"'{leaves[i].Node.Name}' overlaps '{leaves[j].Node.Name}'");
            }
        }

        static bool Intersects(in TreemapTile a, in TreemapTile b)
        {
            // A shared edge is not an overlap, so require genuine area in common. The
            // tolerance is a hundredth of a pixel: tile coordinates are stored as float,
            // which at canvas-scale magnitudes carries roughly 1e-5 of representation error.
            // Anything that matters visually is at least a whole pixel across.
            const double epsilon = 0.01;
            var overlapX = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
            var overlapY = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
            return overlapX > epsilon && overlapY > epsilon;
        }
    }

    [Fact]
    public void Every_tile_stays_inside_the_canvas()
    {
        var model = TreemapLayout.Build(NestedTree(), 640, 480, Exact());

        foreach (var tile in model.Tiles)
        {
            Assert.True(tile.X >= -0.001, $"{tile.Node.Name} starts left of the canvas");
            Assert.True(tile.Y >= -0.001, $"{tile.Node.Name} starts above the canvas");
            Assert.True(tile.X + tile.Width <= 640.001, $"{tile.Node.Name} runs past the right edge");
            Assert.True(tile.Y + tile.Height <= 480.001, $"{tile.Node.Name} runs past the bottom edge");
        }
    }

    [Fact]
    public void Children_are_laid_out_inside_their_parent()
    {
        var model = TreemapLayout.Build(NestedTree(), 900, 700, Exact());
        var byNode = model.Tiles.ToDictionary(t => t.Node, t => t);

        foreach (var tile in model.Tiles.Where(t => t.Depth > 1))
        {
            Assert.True(byNode.TryGetValue(tile.Node.Parent!, out var parent),
                $"no tile emitted for parent of {tile.Node.Name}");

            Assert.True(tile.X >= parent.X - 0.001 &&
                        tile.Y >= parent.Y - 0.001 &&
                        tile.X + tile.Width <= parent.X + parent.Width + 0.001 &&
                        tile.Y + tile.Height <= parent.Y + parent.Height + 0.001,
                $"{tile.Node.Name} escapes its parent {parent.Node.Name}");
        }
    }

    [Fact]
    public void Aspect_ratios_stay_reasonable_for_similar_sizes()
    {
        // Equal-sized children on a square canvas should come out close to square.
        var root = Tree(Enumerable.Range(0, 16).Select(i => ($"f{i}", 1000L)).ToArray());
        var model = TreemapLayout.Build(root, 1000, 1000, Exact());

        foreach (var tile in model.Tiles.Where(t => t.Depth == 1))
        {
            var ratio = Math.Max(tile.Width / tile.Height, tile.Height / tile.Width);
            Assert.True(ratio < 2.0, $"{tile.Node.Name} has aspect ratio {ratio:F2}");
        }
    }

    [Fact]
    public void Tiles_are_emitted_in_depth_first_pre_order()
    {
        // The renderer carries cushion surfaces down a per-depth array, which is only
        // valid if a parent is always emitted before its children.
        var model = TreemapLayout.Build(NestedTree(), 900, 700, Exact());
        var seen = new HashSet<FileNode>();

        foreach (var tile in model.Tiles)
        {
            if (tile.Depth > 0)
                Assert.True(seen.Contains(tile.Node.Parent!),
                    $"{tile.Node.Name} was emitted before its parent");
            seen.Add(tile.Node);
        }
    }

    [Fact]
    public void Depth_never_jumps_by_more_than_one()
    {
        var model = TreemapLayout.Build(NestedTree(), 900, 700, Exact());
        var previous = -1;
        foreach (var tile in model.Tiles)
        {
            Assert.True(tile.Depth <= previous + 1,
                $"depth jumped from {previous} to {tile.Depth}");
            previous = tile.Depth;
        }
    }

    [Fact]
    public void Zero_size_tree_produces_an_empty_model()
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root) { Children = [] };
        var model = TreemapLayout.Build(root, 400, 300);
        Assert.Empty(model.Tiles);
    }

    [Fact]
    public void Zero_size_children_are_skipped()
    {
        var root = Tree(("real", 1000), ("empty1", 0), ("empty2", 0));
        var model = TreemapLayout.Build(root, 400, 300, Exact());

        Assert.DoesNotContain(model.Tiles, t => t.Node.Name.StartsWith("empty"));
        var real = model.Tiles.Single(t => t.Node.Name == "real");
        Assert.Equal(400.0 * 300.0, (double)real.Width * real.Height, precision: 2);
    }

    [Fact]
    public void Zero_sized_canvas_is_handled_without_throwing()
    {
        var model = TreemapLayout.Build(NestedTree(), 0, 0);
        Assert.Empty(model.Tiles);
    }

    [Fact]
    public void Max_depth_limits_recursion()
    {
        var options = Exact();
        options.MaxDepth = 1;
        var model = TreemapLayout.Build(NestedTree(), 800, 600, options);

        Assert.All(model.Tiles, t => Assert.True(t.Depth <= 1));
    }

    [Fact]
    public void Renderer_owner_buffer_agrees_with_the_layout()
    {
        var model = TreemapLayout.Build(NestedTree(), 300, 200, Exact());
        var raster = new TreemapRenderer().Render(model, Exact());

        Assert.Equal(300 * 200, raster.Pixels.Length);

        // Every pixel must resolve to a tile that actually contains it.
        for (var y = 0; y < 200; y += 7)
        {
            for (var x = 0; x < 300; x += 7)
            {
                var tile = raster.HitTest(x, y);
                Assert.NotNull(tile);

                // Tile edges fall between pixels, so ownership means the tile overlaps the
                // pixel's unit square — not that it contains the pixel's exact centre.
                var t = tile!.Value;
                var overlapX = Math.Min(t.X + t.Width, x + 1) - Math.Max(t.X, x);
                var overlapY = Math.Min(t.Y + t.Height, y + 1) - Math.Max(t.Y, y);
                Assert.True(overlapX > 0 && overlapY > 0,
                    $"pixel ({x},{y}) is owned by '{t.Node.Name}' at " +
                    $"({t.X:F2},{t.Y:F2},{t.Width:F2},{t.Height:F2}), which does not touch it");
            }
        }
    }

    [Fact]
    public void Renderer_leaves_no_unpainted_pixels()
    {
        var model = TreemapLayout.Build(NestedTree(), 256, 256, Exact());
        var raster = new TreemapRenderer().Render(model, Exact());

        var unpainted = raster.Owners.Count(o => o < 0);
        Assert.Equal(0, unpainted);
    }

    [Fact]
    public void Hit_test_outside_the_canvas_returns_null()
    {
        var model = TreemapLayout.Build(NestedTree(), 100, 100, Exact());
        var raster = new TreemapRenderer().Render(model, Exact());

        Assert.Null(raster.HitTest(-1, 50));
        Assert.Null(raster.HitTest(50, -1));
        Assert.Null(raster.HitTest(100, 50));
        Assert.Null(raster.HitTest(50, 100));
    }

    [Fact]
    public void Cushion_shading_varies_brightness_within_a_tile()
    {
        var root = Tree(("solo", 1000));
        var model = TreemapLayout.Build(root, 200, 200,
            new TreemapOptions { DirectoryFrames = false, MinTileSide = 0.0001 });
        var raster = new TreemapRenderer().Render(model,
            new TreemapOptions { DirectoryFrames = false, MinTileSide = 0.0001 });

        var distinct = raster.Pixels.Distinct().Count();
        // A flat fill would yield exactly one colour; a lit cushion yields a gradient.
        Assert.True(distinct > 20, $"cushion shading produced only {distinct} distinct colours");
    }

    [Fact]
    public void Large_tree_lays_out_within_a_pixel_bounded_budget()
    {
        // Layout cost must follow the canvas, not the file count.
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root);
        var kids = new FileNode[50_000];
        for (var i = 0; i < kids.Length; i++)
        {
            kids[i] = new FileNode($"f{i}.dat") { Parent = root, Size = 1000 + i % 97 };
            root.Size += kids[i].Size;
        }
        root.Children = kids;
        root.SortBySizeDescending();

        // A small canvas cannot resolve 50k tiles, so most must be culled rather than
        // laid out. This is the property that keeps a ten-million-file scan interactive.
        const int width = 200, height = 150;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var model = TreemapLayout.Build(root, width, height);
        sw.Stop();

        Assert.True(model.CulledCount > 0, "nothing was culled on a canvas far too small to hold every tile");
        Assert.True(model.Tiles.Length <= width * height,
            $"emitted {model.Tiles.Length} tiles for only {width * height} pixels");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"layout took {sw.ElapsedMilliseconds} ms");
    }
}
