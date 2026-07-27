using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DirStat.App.Views;

/// <summary>
/// A slim proportional bar used inside grid rows.
/// </summary>
/// <remarks>
/// Drawn rather than composed from panels: these appear once per visible row in two grids,
/// and a custom render is both cheaper and easier to keep pixel-consistent than a nest of
/// borders driven by width bindings.
/// </remarks>
public sealed class FractionBar : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<FractionBar, double>(nameof(Fraction));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<FractionBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<FractionBar, IBrush?>(nameof(Track));

    public static readonly StyledProperty<double> BarHeightProperty =
        AvaloniaProperty.Register<FractionBar, double>(nameof(BarHeight), defaultValue: 6.0);

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public double BarHeight
    {
        get => GetValue(BarHeightProperty);
        set => SetValue(BarHeightProperty, value);
    }

    static FractionBar()
    {
        AffectsRender<FractionBar>(FractionProperty, FillProperty, TrackProperty, BarHeightProperty);
    }

    public override void Render(DrawingContext context)
    {
        var height = Math.Min(BarHeight, Bounds.Height);
        if (height <= 0 || Bounds.Width <= 0) return;

        var top = (Bounds.Height - height) / 2;
        var radius = height / 2;

        if (Track is { } track)
            context.DrawRectangle(track, null, new RoundedRect(new Rect(0, top, Bounds.Width, height), radius));

        var fraction = Math.Clamp(Fraction, 0, 1);
        if (fraction <= 0 || Fill is not { } fill) return;

        // Keep a sliver visible for tiny-but-nonzero shares, so "almost nothing" still
        // reads differently from "nothing at all".
        var width = Math.Max(fraction * Bounds.Width, height);
        context.DrawRectangle(fill, null, new RoundedRect(new Rect(0, top, width, height), radius));
    }
}
