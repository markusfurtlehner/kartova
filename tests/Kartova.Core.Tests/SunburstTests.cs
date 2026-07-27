using Kartova.Core.Model;
using Kartova.Core.Treemap;
using Xunit;

namespace Kartova.Core.Tests;

public class SunburstTests
{
    private static FileNode NestedTree()
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root);
        var branches = new List<FileNode>();

        for (var i = 0; i < 4; i++)
        {
            var dir = new FileNode($"dir{i}", NodeFlags.Directory) { Parent = root };
            var kids = new List<FileNode>();
            for (var j = 0; j < 5; j++)
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

    [Fact]
    public void Every_child_of_the_root_appears_on_the_first_ring()
    {
        var root = NestedTree();
        var model = SunburstLayout.Build(root, 600, 600);

        var firstRing = model.Segments.Where(s => s.Depth == 1).ToArray();
        Assert.Equal(4, firstRing.Length);
    }

    [Fact]
    public void The_first_ring_sweeps_a_full_turn()
    {
        var root = NestedTree();
        var model = SunburstLayout.Build(root, 600, 600);

        var total = model.Segments.Where(s => s.Depth == 1).Sum(s => (double)s.Sweep);
        Assert.Equal(Math.Tau, total, precision: 3);
    }

    [Fact]
    public void Sweep_is_proportional_to_size()
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root);
        var big = new FileNode("big") { Parent = root, Size = 3000 };
        var small = new FileNode("small") { Parent = root, Size = 1000 };
        root.Children = [big, small];
        root.Size = 4000;

        var model = SunburstLayout.Build(root, 600, 600);

        var bigSegment = model.Segments.Single(s => s.Node.Name == "big");
        var smallSegment = model.Segments.Single(s => s.Node.Name == "small");

        Assert.Equal(3.0, bigSegment.Sweep / smallSegment.Sweep, precision: 3);
    }

    [Fact]
    public void Siblings_on_a_ring_do_not_overlap()
    {
        var model = SunburstLayout.Build(NestedTree(), 600, 600);

        foreach (var ring in model.Segments.GroupBy(s => s.Depth))
        {
            var ordered = ring.OrderBy(s => s.StartAngle).ToArray();
            for (var i = 1; i < ordered.Length; i++)
                Assert.True(ordered[i].StartAngle >= ordered[i - 1].EndAngle - 1e-4,
                    $"segments overlap on ring {ring.Key}");
        }
    }

    [Fact]
    public void Children_stay_inside_their_parents_angular_span()
    {
        var model = SunburstLayout.Build(NestedTree(), 600, 600);
        var byNode = model.Segments.ToDictionary(s => s.Node, s => s);

        foreach (var segment in model.Segments.Where(s => s.Depth > 1))
        {
            var parent = byNode[segment.Node.Parent!];
            Assert.True(segment.StartAngle >= parent.StartAngle - 1e-4);
            Assert.True(segment.EndAngle <= parent.EndAngle + 1e-4);
        }
    }

    [Fact]
    public void Rings_move_outward_with_depth()
    {
        var model = SunburstLayout.Build(NestedTree(), 600, 600);

        var inner = model.Segments.Where(s => s.Depth == 1).Select(s => s.OuterRadius).Distinct().Single();
        var outer = model.Segments.Where(s => s.Depth == 2).Select(s => s.InnerRadius).Distinct().Single();

        Assert.Equal(inner, outer, precision: 3);
    }

    [Fact]
    public void Depth_is_capped_by_the_options()
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root);
        var current = root;
        for (var i = 0; i < 20; i++)
        {
            var child = new FileNode($"d{i}", NodeFlags.Directory) { Parent = current, Size = 1000 };
            current.Children = [child];
            current = child;
        }
        for (var n = root; n is not null; n = n.Children?.FirstOrDefault()) n.Size = 1000;

        var model = SunburstLayout.Build(root, 600, 600, new SunburstOptions { MaxDepth = 5 });

        Assert.All(model.Segments, s => Assert.True(s.Depth <= 5));
    }

    /// <summary>
    /// A tree whose largest child is shallow but whose smaller sibling runs deep.
    /// </summary>
    /// <remarks>
    /// The shape that matters: sizing the rings by following the largest child alone reports
    /// one level here while the layout actually draws seven, so the rings are cut for a disc
    /// six times larger than the canvas and everything past the first is clipped away.
    /// </remarks>
    private static FileNode LopsidedTree()
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root);

        var big = new FileNode("big.bin") { Parent = root, Size = 10_000_000 };

        var deep = new FileNode("deep", NodeFlags.Directory) { Parent = root, Size = 5_000_000 };
        var cursor = deep;
        for (var i = 0; i < 8; i++)
        {
            var child = new FileNode($"level{i}", NodeFlags.Directory) { Parent = cursor, Size = 5_000_000 };
            cursor.Children = [child];
            cursor = child;
        }

        root.Children = [big, deep];
        root.Size = 15_000_000;
        root.SortBySizeDescending();
        return root;
    }

    [Theory]
    [InlineData(400)]
    [InlineData(600)]
    [InlineData(1024)]
    public void No_segment_reaches_outside_the_canvas(int size)
    {
        foreach (var root in new[] { NestedTree(), LopsidedTree() })
        {
            var model = SunburstLayout.Build(root, size, size);
            var limit = Math.Min(model.CentreX, model.CentreY);

            Assert.All(model.Segments, s =>
                Assert.True(s.OuterRadius <= limit,
                    $"segment at depth {s.Depth} reaches {s.OuterRadius:F1}, past the {limit:F1} half-canvas"));
        }
    }

    [Fact]
    public void The_outermost_ring_ends_exactly_at_the_radius()
    {
        // Shallow and deep alike: whatever depth is drawn, the rings are cut to fill the disc.
        foreach (var root in new[] { NestedTree(), LopsidedTree() })
        {
            var model = SunburstLayout.Build(root, 600, 600);
            var outermost = model.Segments.Max(s => s.OuterRadius);

            Assert.Equal(600 / 2.0 * 0.86, outermost, precision: 2);
        }
    }

    [Fact]
    public void A_ring_that_carries_almost_nothing_does_not_claim_a_share_of_the_radius()
    {
        // One wide branch and a hair-thin chain trailing off underneath it. Drawing the chain
        // to full depth would spend most of the radius on rings covering a sliver of the turn.
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root);
        var bulk = new FileNode("bulk.bin") { Parent = root, Size = 100_000_000 };

        var thread = new FileNode("thread", NodeFlags.Directory) { Parent = root, Size = 1_000_000 };
        var cursor = thread;
        for (var i = 0; i < 6; i++)
        {
            var child = new FileNode($"n{i}", NodeFlags.Directory) { Parent = cursor, Size = 1_000_000 };
            cursor.Children = [child];
            cursor = child;
        }

        root.Children = [bulk, thread];
        root.Size = 101_000_000;
        root.SortBySizeDescending();

        var model = SunburstLayout.Build(root, 600, 600);

        // The thread is under 1% of the circle, so it should not set the scale for the disc.
        Assert.True(model.MaxDepth <= 2, $"drew {model.MaxDepth} rings for a 1% branch");

        // And the rings that are drawn still reach the rim exactly.
        Assert.Equal(600 / 2.0 * 0.86, model.Segments.Max(s => s.OuterRadius), precision: 2);
    }

    [Fact]
    public void A_deep_branch_that_carries_real_weight_is_still_drawn()
    {
        // The counterpart: a third of the circle running deep is worth the radius it costs.
        var model = SunburstLayout.Build(LopsidedTree(), 600, 600);

        Assert.True(model.MaxDepth >= 5, $"only drew {model.MaxDepth} rings for a 33% branch");
    }

    [Fact]
    public void Ring_thickness_is_uniform_across_the_disc()
    {
        var model = SunburstLayout.Build(LopsidedTree(), 600, 600);

        Assert.All(model.Segments, s =>
            Assert.Equal(model.RingThickness, s.OuterRadius - s.InnerRadius, precision: 2));
    }

    [Fact]
    public void Slivers_too_thin_to_see_are_dropped()
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root);
        var children = new List<FileNode> { new("huge") { Size = 10_000_000 } };
        for (var i = 0; i < 500; i++) children.Add(new FileNode($"tiny{i}") { Size = 10 });

        foreach (var c in children) { c.Parent = root; root.Size += c.Size; }
        root.Children = children.ToArray();
        root.SortBySizeDescending();

        var model = SunburstLayout.Build(root, 600, 600);

        Assert.Contains(model.Segments, s => s.Node.Name == "huge");
        Assert.True(model.Segments.Length < 100, $"expected slivers to be culled, got {model.Segments.Length}");
    }

    [Fact]
    public void A_zero_sized_tree_produces_nothing()
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root) { Children = [] };
        Assert.Empty(SunburstLayout.Build(root, 400, 400).Segments);
    }

    [Fact]
    public void A_canvas_too_small_to_draw_produces_nothing()
    {
        Assert.Empty(SunburstLayout.Build(NestedTree(), 6, 6).Segments);
    }

    [Fact]
    public void Rendering_paints_the_disc_and_leaves_the_corners_as_background()
    {
        var model = SunburstLayout.Build(NestedTree(), 200, 200);
        var pixels = new uint[200 * 200];
        var owners = new int[200 * 200];
        const uint background = 0xFF101010;

        SunburstLayout.Render(model, new SunburstOptions(), pixels, owners, background);

        // A corner is outside the disc.
        Assert.Equal(background, pixels[0]);
        Assert.Equal(-1, owners[0]);

        // Something was drawn.
        Assert.Contains(owners, o => o >= 0);
    }

    [Fact]
    public void Every_painted_pixel_resolves_to_a_segment_that_contains_it()
    {
        var model = SunburstLayout.Build(NestedTree(), 240, 240);
        var pixels = new uint[240 * 240];
        var owners = new int[240 * 240];

        SunburstLayout.Render(model, new SunburstOptions(), pixels, owners, 0xFF000000);

        var checkedAny = false;
        for (var y = 0; y < 240; y += 5)
        {
            for (var x = 0; x < 240; x += 5)
            {
                var hit = SunburstLayout.HitTest(model, owners, x, y);
                if (hit is null) continue;

                checkedAny = true;
                var segment = hit.Value;

                var dx = x + 0.5 - model.CentreX;
                var dy = y + 0.5 - model.CentreY;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                var angle = Math.Atan2(dx, -dy);
                if (angle < 0) angle += Math.Tau;

                Assert.InRange(distance, segment.InnerRadius - 1.5, segment.OuterRadius + 1.5);
                Assert.InRange(angle, segment.StartAngle - 0.02, segment.EndAngle + 0.02);
            }
        }

        Assert.True(checkedAny, "no painted pixels were sampled");
    }

    [Fact]
    public void Hit_testing_outside_the_canvas_returns_nothing()
    {
        var model = SunburstLayout.Build(NestedTree(), 100, 100);
        var owners = new int[100 * 100];
        Array.Fill(owners, -1);

        Assert.Null(SunburstLayout.HitTest(model, owners, -1, 50));
        Assert.Null(SunburstLayout.HitTest(model, owners, 50, 200));
    }

    [Fact]
    public void Layout_of_a_large_tree_stays_bounded()
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root);
        var kids = new FileNode[50_000];
        for (var i = 0; i < kids.Length; i++)
        {
            kids[i] = new FileNode($"f{i}.dat") { Parent = root, Size = 1000 + i % 97 };
            root.Size += kids[i].Size;
        }
        root.Children = kids;
        root.SortBySizeDescending();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var model = SunburstLayout.Build(root, 600, 600);
        sw.Stop();

        // Most of the 50k slices are far too thin to draw and never reach the segment list.
        Assert.True(model.Segments.Length < 2000, $"got {model.Segments.Length} segments");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"layout took {sw.ElapsedMilliseconds} ms");
    }
}
