using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kartova.App.Views;

/// <summary>
/// A circular gauge showing how full a volume is.
/// </summary>
/// <remarks>
/// A ring communicates "nearly full" at a glance, before any number is read, which is the
/// one thing someone opening a disk analyser wants to know immediately.
/// </remarks>
public sealed class CapacityRing : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<CapacityRing, double>(nameof(Fraction));

    public static readonly StyledProperty<IBrush?> RingBrushProperty =
        AvaloniaProperty.Register<CapacityRing, IBrush?>(nameof(RingBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<CapacityRing, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<CapacityRing, double>(nameof(StrokeThickness), defaultValue: 6.0);

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush? RingBrush
    {
        get => GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    static CapacityRing()
    {
        AffectsRender<CapacityRing>(
            FractionProperty, RingBrushProperty, TrackBrushProperty, StrokeThicknessProperty);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var thickness = Math.Min(StrokeThickness, size / 2);
        var radius = (size - thickness) / 2;
        if (radius <= 0) return;

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);

        if (TrackBrush is { } track)
        {
            context.DrawEllipse(null, new Pen(track, thickness) { LineCap = PenLineCap.Round }, centre, radius, radius);
        }

        var fraction = Math.Clamp(Fraction, 0, 1);
        if (fraction <= 0 || RingBrush is not { } ring) return;

        var pen = new Pen(ring, thickness) { LineCap = PenLineCap.Round };

        // A full ring has no arc endpoints to draw, so render it as a plain circle.
        if (fraction >= 0.999)
        {
            context.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }

        // Sweep clockwise from twelve o'clock, the direction a gauge is read.
        const double start = -Math.PI / 2;
        var sweep = fraction * Math.PI * 2;
        var end = start + sweep;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(
                new Point(centre.X + radius * Math.Cos(start), centre.Y + radius * Math.Sin(start)),
                isFilled: false);

            ctx.ArcTo(
                new Point(centre.X + radius * Math.Cos(end), centre.Y + radius * Math.Sin(end)),
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: sweep > Math.PI,
                sweepDirection: SweepDirection.Clockwise);

            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, geometry);
    }
}
