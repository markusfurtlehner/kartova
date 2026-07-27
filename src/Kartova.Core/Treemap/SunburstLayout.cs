using Kartova.Core.Model;

namespace Kartova.Core.Treemap;

/// <summary>One ring segment: an angular slice at a given depth.</summary>
public readonly struct SunburstSegment
{
    public readonly FileNode Node;

    /// <summary>Angles in radians, measured clockwise from twelve o'clock.</summary>
    public readonly float StartAngle;
    public readonly float EndAngle;

    public readonly float InnerRadius;
    public readonly float OuterRadius;
    public readonly short Depth;

    public SunburstSegment(
        FileNode node, double startAngle, double endAngle,
        double innerRadius, double outerRadius, int depth)
    {
        Node = node;
        StartAngle = (float)startAngle;
        EndAngle = (float)endAngle;
        InnerRadius = (float)innerRadius;
        OuterRadius = (float)outerRadius;
        Depth = (short)depth;
    }

    public float Sweep => EndAngle - StartAngle;
}

public sealed class SunburstModel
{
    public required FileNode Root { get; init; }
    public required SunburstSegment[] Segments { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double CentreX { get; init; }
    public required double CentreY { get; init; }
    public required double RingThickness { get; init; }

    /// <summary>Empty radius at the centre. Needed to turn a distance back into a ring index.</summary>
    public required double HoleRadius { get; init; }

    public required int MaxDepth { get; init; }

    public static SunburstModel Empty(FileNode root) => new()
    {
        Root = root, Segments = [], Width = 0, Height = 0,
        CentreX = 0, CentreY = 0, RingThickness = 0, HoleRadius = 0, MaxDepth = 0,
    };
}

public sealed class SunburstOptions
{
    /// <summary>How many rings to draw outward from the centre.</summary>
    public int MaxDepth { get; set; } = 7;

    /// <summary>
    /// Segments narrower than this many radians are dropped. Hairline slices are not merely
    /// unreadable, they turn a deep tree into visual static.
    /// </summary>
    public double MinimumSweep { get; set; } = 0.008;

    /// <summary>Fraction of the radius left empty at the centre, where the root label sits.</summary>
    public double HoleFraction { get; set; } = 0.16;

    /// <summary>
    /// A ring is drawn only if its segments cover at least this share of a full turn.
    /// </summary>
    /// <remarks>
    /// One deep chain of narrow slices should not set the scale for the whole picture. On a
    /// Windows directory the seventh ring covers well under a percent of the circle while
    /// claiming a seventh of the radius, which shrinks the rings holding the other 99% for no
    /// gain. Stopping where the rings stop carrying anything spends the radius on what can
    /// actually be read.
    /// </remarks>
    public double MinimumRingCoverage { get; set; } = 0.05;

    /// <summary>Lighting for the shaded fill, matching the treemap's upper-left source.</summary>
    public double Ambient { get; set; } = 0.42;
}

/// <summary>
/// Lays a scanned tree out as concentric rings.
/// </summary>
/// <remarks>
/// <para>
/// The same data as the treemap, read differently: depth becomes distance from the centre and
/// size becomes angle, so the shape of a hierarchy is visible at a glance in a way a treemap's
/// nested rectangles are not. It is the better picture for showing someone what a disk looks
/// like; the treemap remains the better tool for finding one large file among many.
/// </para>
/// <para>
/// Cost is bounded the same way: rings stop at a fixed depth and segments too narrow to see
/// are dropped along with everything inside them, so a ten-million-file tree lays out as
/// quickly as a small one.
/// </para>
/// </remarks>
public static class SunburstLayout
{
    public static SunburstModel Build(FileNode root, int width, int height, SunburstOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new SunburstOptions();

        if (width <= 0 || height <= 0 || root.Size <= 0) return SunburstModel.Empty(root);

        var centreX = width / 2.0;
        var centreY = height / 2.0;

        // A proportional margin rather than a fixed one. Filling the pane edge to edge reads
        // as clipped whatever the actual geometry, and the outermost ring is where the deepest,
        // most fragmented segments land — exactly what needs room to be legible.
        var radius = Math.Min(width, height) / 2.0 * 0.86;
        if (radius <= 8) return SunburstModel.Empty(root);

        var hole = radius * options.HoleFraction;

        // Angles first, radii second. A sweep depends only on a node's share of its parent, so
        // the whole angular layout can be built without knowing how thick a ring will be. That
        // ordering is what makes the fit exact: rather than predicting how deep the tree will
        // draw and dividing the radius by the guess, this measures the depth actually emitted
        // and divides by that. A shallow tree fills the circle and a deep one still ends at the
        // rim, because the outermost ring is by construction the one the radius was cut for.
        var slices = new List<Slice>(1024);
        var deepest = 0;

        AddChildren(root, 0, Math.Tau, 1, options, slices, ref deepest);

        // One number decides both how many rings are drawn and how thick they are. Deriving the
        // two independently is what let the disc overflow: rings cut for a shallow tree were
        // used to lay out a deep one, and the outermost ring landed several times past the rim.
        var drawnDepth = LegibleDepth(slices, deepest, options.MinimumRingCoverage);
        var ringThickness = (radius - hole) / drawnDepth;

        var segments = new List<SunburstSegment>(slices.Count);
        foreach (var slice in slices)
        {
            if (slice.Depth > drawnDepth) continue;

            var inner = hole + (slice.Depth - 1) * ringThickness;
            segments.Add(new SunburstSegment(
                slice.Node, slice.StartAngle, slice.EndAngle, inner, inner + ringThickness, slice.Depth));
        }

        return new SunburstModel
        {
            Root = root,
            Segments = segments.ToArray(),
            Width = width,
            Height = height,
            CentreX = centreX,
            CentreY = centreY,
            RingThickness = ringThickness,
            HoleRadius = hole,
            MaxDepth = drawnDepth,
        };
    }

    /// <summary>
    /// The deepest ring still worth spending radius on: the last one whose segments cover at
    /// least <paramref name="minimumCoverage"/> of a full turn.
    /// </summary>
    /// <remarks>
    /// Coverage never increases with depth, since a child's sweep is a share of its parent's
    /// and culling only removes, so the first ring below the threshold ends the disc and no
    /// populated ring is ever stranded outside it.
    /// </remarks>
    private static int LegibleDepth(List<Slice> slices, int deepest, double minimumCoverage)
    {
        if (deepest <= 1) return 1;

        var covered = new double[deepest + 1];
        foreach (var slice in slices) covered[slice.Depth] += slice.EndAngle - slice.StartAngle;

        var depth = 1;
        for (var d = 1; d <= deepest; d++)
        {
            if (covered[d] / Math.Tau < minimumCoverage) break;
            depth = d;
        }

        return depth;
    }

    /// <summary>A segment with its angles settled but its radius not yet known.</summary>
    private readonly record struct Slice(FileNode Node, double StartAngle, double EndAngle, int Depth);

    /// <summary>Distributes a node's children across the angular span it occupies.</summary>
    private static void AddChildren(
        FileNode node, double startAngle, double endAngle, int depth,
        SunburstOptions options, List<Slice> slices, ref int deepest)
    {
        if (depth > options.MaxDepth) return;

        var children = node.Children;
        if (children is null || children.Length == 0) return;
        if (node.Size <= 0) return;

        var available = endAngle - startAngle;
        var cursor = startAngle;

        foreach (var child in children)
        {
            if (child.Size <= 0) continue; // sorted descending, but synthetic nodes can be zero

            var sweep = available * child.Size / node.Size;
            if (sweep < options.MinimumSweep)
            {
                // Too thin to see or click, and so is everything inside it.
                cursor += sweep;
                continue;
            }

            // Counted here rather than on entry, so a ring whose children were all culled does
            // not claim a share of the radius it never draws in.
            if (depth > deepest) deepest = depth;

            slices.Add(new Slice(child, cursor, cursor + sweep, depth));

            AddChildren(child, cursor, cursor + sweep, depth + 1, options, slices, ref deepest);

            cursor += sweep;
        }
    }

    /// <summary>
    /// Rasterizes a sunburst into the same pixel-plus-owner buffers the treemap uses, so both
    /// views share one hit-testing path and one bitmap pipeline.
    /// </summary>
    public static void Render(
        SunburstModel model, SunburstOptions options, uint[] pixels, int[] owners, uint background)
    {
        var width = model.Width;
        var height = model.Height;
        if (width <= 0 || height <= 0) return;

        Array.Fill(pixels, background);
        Array.Fill(owners, -1);

        var segments = model.Segments;
        if (segments.Length == 0) return;

        var maxRadius = 0.0;
        foreach (var s in segments) maxRadius = Math.Max(maxRadius, s.OuterRadius);

        // Bound the work to the disc rather than the whole canvas.
        var left = Math.Max(0, (int)(model.CentreX - maxRadius) - 1);
        var right = Math.Min(width, (int)(model.CentreX + maxRadius) + 2);
        var top = Math.Max(0, (int)(model.CentreY - maxRadius) - 1);
        var bottom = Math.Min(height, (int)(model.CentreY + maxRadius) + 2);

        // Segments bucketed by ring and sorted by angle, so a pixel resolves to one segment by
        // arithmetic and a binary search rather than by scanning the whole map.
        var rings = new int[model.MaxDepth + 2][];
        for (var d = 0; d < rings.Length; d++)
        {
            rings[d] = Enumerable.Range(0, segments.Length)
                .Where(i => segments[i].Depth == d)
                .OrderBy(i => segments[i].StartAngle)
                .ToArray();
        }

        var ambient = options.Ambient;
        var diffuse = 1.0 - ambient;
        var thickness = Math.Max(model.RingThickness, 0.0001);

        Parallel.For(top, bottom, y =>
        {
            var row = y * width;
            var dy = y + 0.5 - model.CentreY;

            for (var x = left; x < right; x++)
            {
                var dx = x + 0.5 - model.CentreX;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance > maxRadius || distance < model.HoleRadius) continue;

                // The ring index follows straight from the distance.
                var depth = (int)((distance - model.HoleRadius) / thickness) + 1;
                if (depth < 1 || depth >= rings.Length) continue;

                var ring = rings[depth];
                if (ring.Length == 0) continue;

                // Clockwise from twelve o'clock, matching the layout's convention.
                var angle = Math.Atan2(dx, -dy);
                if (angle < 0) angle += Math.Tau;

                var found = FindByAngle(segments, ring, angle);
                if (found < 0) continue;

                ref readonly var hit = ref segments[found];
                var colour = TreemapRenderer.ColorFor(hit.Node);

                // Shade across the ring so adjacent rings stay distinguishable, and add a
                // hairline at the outer edge so segment boundaries read cleanly.
                var across = (distance - hit.InnerRadius) / Math.Max(1.0, hit.OuterRadius - hit.InnerRadius);
                var intensity = ambient + diffuse * (1.0 - 0.55 * across);

                var r = (uint)Math.Min(255, (int)(((colour >> 16) & 0xFF) * intensity));
                var g = (uint)Math.Min(255, (int)(((colour >> 8) & 0xFF) * intensity));
                var b = (uint)Math.Min(255, (int)((colour & 0xFF) * intensity));

                pixels[row + x] = 0xFF000000u | (r << 16) | (g << 8) | b;
                owners[row + x] = found;
            }
        });
    }

    /// <summary>
    /// Binary search for the segment covering an angle within one ring.
    /// </summary>
    /// <remarks>
    /// Segments in a ring are disjoint and sorted, but they do not tile it completely — a
    /// slice culled for being too thin leaves a gap — so the candidate found by the search
    /// still has to be checked for containment.
    /// </remarks>
    private static int FindByAngle(SunburstSegment[] segments, int[] ring, double angle)
    {
        var low = 0;
        var high = ring.Length - 1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            ref readonly var candidate = ref segments[ring[mid]];

            if (angle < candidate.StartAngle) high = mid - 1;
            else if (angle >= candidate.EndAngle) low = mid + 1;
            else return ring[mid];
        }

        return -1;
    }

    /// <summary>Segment at a pixel, or null outside the disc.</summary>
    public static SunburstSegment? HitTest(SunburstModel model, int[] owners, int x, int y)
    {
        if (x < 0 || y < 0 || x >= model.Width || y >= model.Height) return null;

        var owner = owners[y * model.Width + x];
        if (owner < 0 || owner >= model.Segments.Length) return null;

        return model.Segments[owner];
    }

    /// <summary>Colour of a node, shared with the treemap so both views agree.</summary>
    public static uint ColorFor(FileNode node) => TreemapRenderer.ColorFor(node);
}
