using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DirStat.Core.Model;
using DirStat.Core.Treemap;

namespace DirStat.App.Views;

/// <summary>
/// Draws a scanned tree as an interactive cushion treemap.
/// </summary>
/// <remarks>
/// <para>
/// The map is rasterized once into a bitmap on a background thread and then simply blitted.
/// Hover and selection are drawn as thin overlays on top, so moving the pointer never
/// re-rasterizes anything — which is what keeps the control smooth on a map holding
/// hundreds of thousands of rectangles.
/// </para>
/// <para>
/// Hit-testing reads the renderer's owner buffer, making "which file is under the cursor"
/// a single array lookup rather than a walk of the tree.
/// </para>
/// </remarks>
public sealed class TreemapControl : Control
{
    public static readonly StyledProperty<FileNode?> RootProperty =
        AvaloniaProperty.Register<TreemapControl, FileNode?>(nameof(Root));

    public static readonly StyledProperty<FileNode?> SelectedNodeProperty =
        AvaloniaProperty.Register<TreemapControl, FileNode?>(
            nameof(SelectedNode), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> CushionShadingProperty =
        AvaloniaProperty.Register<TreemapControl, bool>(nameof(CushionShading), defaultValue: true);

    public static readonly StyledProperty<bool> DirectoryFramesProperty =
        AvaloniaProperty.Register<TreemapControl, bool>(nameof(DirectoryFrames), defaultValue: true);

    /// <summary>Draw the same data as concentric rings instead of nested rectangles.</summary>
    public static readonly StyledProperty<bool> SunburstProperty =
        AvaloniaProperty.Register<TreemapControl, bool>(nameof(Sunburst));

    public bool Sunburst
    {
        get => GetValue(SunburstProperty);
        set => SetValue(SunburstProperty, value);
    }

    /// <summary>Raised on double-click, asking the host to zoom into the node.</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> ZoomCommandProperty =
        AvaloniaProperty.Register<TreemapControl, System.Windows.Input.ICommand?>(nameof(ZoomCommand));

    public FileNode? Root
    {
        get => GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public FileNode? SelectedNode
    {
        get => GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public bool CushionShading
    {
        get => GetValue(CushionShadingProperty);
        set => SetValue(CushionShadingProperty, value);
    }

    public bool DirectoryFrames
    {
        get => GetValue(DirectoryFramesProperty);
        set => SetValue(DirectoryFramesProperty, value);
    }

    public System.Windows.Input.ICommand? ZoomCommand
    {
        get => GetValue(ZoomCommandProperty);
        set => SetValue(ZoomCommandProperty, value);
    }

    private WriteableBitmap? _bitmap;
    private TreemapRaster? _raster;
    private CancellationTokenSource? _renderCancellation;
    private FileNode? _hovered;

    /// <summary>Cached tile bounds for the current selection and hover, in bitmap pixels.</summary>
    private Rect? _selectionRect;
    private Rect? _hoverRect;

    private PixelSize _rasterSize;
    private DispatcherTimer? _resizeDebounce;

    private static readonly IBrush EmptyBackground = new SolidColorBrush(Color.FromRgb(0x0D, 0x10, 0x17));
    private static readonly IPen SelectionPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), 2);
    private static readonly IPen SelectionHalo = new Pen(new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C)), 4);
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), 1);

    static TreemapControl()
    {
        AffectsRender<TreemapControl>(SelectedNodeProperty);
        FocusableProperty.OverrideDefaultValue<TreemapControl>(true);
    }

    public TreemapControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RootProperty ||
            change.Property == CushionShadingProperty ||
            change.Property == DirectoryFramesProperty ||
            change.Property == SunburstProperty)
        {
            ScheduleRender(immediate: true);
        }
        else if (change.Property == BoundsProperty)
        {
            // Dragging a window edge fires continuously; re-rasterizing on every frame
            // would peg a core, so coalesce the burst and render once it settles.
            ScheduleRender(immediate: false);
        }
        else if (change.Property == SelectedNodeProperty)
        {
            _selectionRect = FindShapeBounds(SelectedNode);
            InvalidateVisual();
        }
    }

    private void ScheduleRender(bool immediate)
    {
        if (immediate)
        {
            _resizeDebounce?.Stop();
            StartRender();
            return;
        }

        _resizeDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _resizeDebounce.Stop();
        _resizeDebounce.Tick -= OnResizeSettled;
        _resizeDebounce.Tick += OnResizeSettled;
        _resizeDebounce.Start();
    }

    private void OnResizeSettled(object? sender, EventArgs e)
    {
        _resizeDebounce?.Stop();
        StartRender();
    }

    private async void StartRender()
    {
        _renderCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _renderCancellation = cancellation;
        var token = cancellation.Token;

        var root = Root;
        var scaling = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        var width = (int)Math.Round(Bounds.Width * scaling);
        var height = (int)Math.Round(Bounds.Height * scaling);

        if (root is null || root.Size <= 0 || width < 8 || height < 8)
        {
            _raster = null;
            _sunburstModel = null;
            _sunburstOwners = null;
            _bitmap = null;
            _selectionRect = null;
            _hoverRect = null;
            InvalidateVisual();
            return;
        }

        var options = new TreemapOptions
        {
            CushionShading = CushionShading,
            DirectoryFrames = DirectoryFrames,
            // Sub-pixel rectangles cannot be seen or clicked, so they are not worth drawing.
            MinTileSide = 1.0,
        };

        var sunburst = Sunburst;

        try
        {
            if (sunburst)
            {
                var rendered = await Task.Run(() =>
                {
                    var model = SunburstLayout.Build(root, width, height, _sunburstOptions);
                    token.ThrowIfCancellationRequested();

                    var pixels = new uint[width * height];
                    var owners = new int[width * height];
                    SunburstLayout.Render(model, _sunburstOptions, pixels, owners, EmptyBackgroundColor);
                    return (model, pixels, owners);
                }, token);

                if (token.IsCancellationRequested) return;

                _sunburstModel = rendered.model;
                _sunburstOwners = rendered.owners;
                _raster = null;
                _bitmap = CreateBitmap(rendered.pixels, width, height, scaling);
            }
            else
            {
                var raster = await Task.Run(() =>
                {
                    var model = TreemapLayout.Build(root, width, height, options);
                    token.ThrowIfCancellationRequested();
                    return new TreemapRenderer().Render(model, options);
                }, token);

                if (token.IsCancellationRequested) return;

                _raster = raster;
                _sunburstModel = null;
                _sunburstOwners = null;
                _bitmap = CreateBitmap(raster.Pixels, width, height, scaling);
            }

            _rasterSize = new PixelSize(width, height);
            _selectionRect = FindShapeBounds(SelectedNode);
            _hoverRect = FindShapeBounds(_hovered);

            InvalidateVisual();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer render; the previous bitmap stays on screen meanwhile.
        }
    }

    private readonly SunburstOptions _sunburstOptions = new();
    private SunburstModel? _sunburstModel;
    private int[]? _sunburstOwners;

    private const uint EmptyBackgroundColor = 0xFF0D1017;

    /// <summary>The last rendered image, for saving to a file.</summary>
    public Bitmap? CurrentImage => _bitmap;

    /// <summary>Copies rasterized pixels into a GPU-uploadable bitmap.</summary>
    private static WriteableBitmap CreateBitmap(uint[] source, int width, int height, double scaling)
    {
        // Matching the bitmap DPI to the render scaling makes one bitmap pixel land on one
        // physical pixel, so the cushion detail stays crisp on a HiDPI display.
        var dpi = new Vector(96 * scaling, 96 * scaling);
        var bitmap = new WriteableBitmap(new PixelSize(width, height), dpi, PixelFormat.Bgra8888, AlphaFormat.Opaque);

        using var buffer = bitmap.Lock();

        unsafe
        {
            var rowPixels = buffer.RowBytes / 4;
            if (rowPixels == width)
            {
                var destination = new Span<uint>((void*)buffer.Address, width * height);
                source.AsSpan(0, width * height).CopyTo(destination);
            }
            else
            {
                // The surface is padded; copy a row at a time to respect the stride.
                for (var y = 0; y < height; y++)
                {
                    var destination = new Span<uint>((void*)(buffer.Address + y * buffer.RowBytes), width);
                    source.AsSpan(y * width, width).CopyTo(destination);
                }
            }
        }

        return bitmap;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(EmptyBackground, bounds);

        var bitmap = _bitmap;
        if (bitmap is null) return;

        context.DrawImage(bitmap, new Rect(bitmap.Size), bounds);

        // Overlays are drawn in device-independent units, so scale from bitmap pixels.
        var scaleX = _rasterSize.Width > 0 ? bounds.Width / _rasterSize.Width : 1;
        var scaleY = _rasterSize.Height > 0 ? bounds.Height / _rasterSize.Height : 1;

        if (_hoverRect is { } hover && _hovered is not null && !ReferenceEquals(_hovered, SelectedNode))
            context.DrawRectangle(null, HoverPen, Scale(hover, scaleX, scaleY));

        if (_selectionRect is { } selection)
        {
            var rect = Scale(selection, scaleX, scaleY);
            // A dark halo underneath keeps the white outline readable over pale tiles.
            context.DrawRectangle(null, SelectionHalo, rect);
            context.DrawRectangle(null, SelectionPen, rect);
        }
    }

    private static Rect Scale(Rect rect, double scaleX, double scaleY) =>
        new(rect.X * scaleX, rect.Y * scaleY, rect.Width * scaleX, rect.Height * scaleY);

    /// <summary>
    /// Bounding box of a node's drawn shape, or null when it is not on the chart.
    /// </summary>
    /// <remarks>
    /// For a treemap this is the tile itself. For a sunburst it is the box around the ring
    /// segment, which is enough for the highlight outline and avoids drawing an arc overlay
    /// for something the user is only hovering over.
    /// </remarks>
    private Rect? FindShapeBounds(FileNode? node)
    {
        if (node is null) return null;

        if (_raster is { } raster)
        {
            var tiles = raster.Model.Tiles;
            for (var i = 0; i < tiles.Length; i++)
            {
                if (!ReferenceEquals(tiles[i].Node, node)) continue;
                ref readonly var tile = ref tiles[i];
                return new Rect(tile.X, tile.Y, tile.Width, tile.Height);
            }

            return null;
        }

        if (_sunburstModel is not { } model) return null;

        var segments = model.Segments;
        for (var i = 0; i < segments.Length; i++)
        {
            if (!ReferenceEquals(segments[i].Node, node)) continue;
            return SegmentBounds(model, segments[i]);
        }

        return null;
    }

    /// <summary>Axis-aligned box enclosing a ring segment.</summary>
    private static Rect SegmentBounds(SunburstModel model, in SunburstSegment segment)
    {
        // Sample the arc rather than solving for extrema: a handful of points is plenty for a
        // highlight box and keeps the quadrant-crossing cases from needing special handling.
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        const int samples = 24;
        for (var i = 0; i <= samples; i++)
        {
            var angle = segment.StartAngle + (segment.EndAngle - segment.StartAngle) * i / samples;
            var sin = Math.Sin(angle);
            var cos = Math.Cos(angle);

            foreach (var radius in new[] { (double)segment.InnerRadius, segment.OuterRadius })
            {
                var x = model.CentreX + radius * sin;
                var y = model.CentreY - radius * cos;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Node under a pixel, whichever chart is showing.</summary>
    private FileNode? NodeAtPixel(int x, int y)
    {
        if (_raster is { } raster) return raster.NodeAt(x, y);
        if (_sunburstModel is { } model && _sunburstOwners is { } owners)
            return SunburstLayout.HitTest(model, owners, x, y)?.Node;
        return null;
    }

    // ----------------------------------------------------------- interaction

    private (int X, int Y) ToRasterPoint(Point point)
    {
        var scaleX = Bounds.Width > 0 ? _rasterSize.Width / Bounds.Width : 1;
        var scaleY = Bounds.Height > 0 ? _rasterSize.Height / Bounds.Height : 1;
        return ((int)(point.X * scaleX), (int)(point.Y * scaleY));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_raster is null && _sunburstModel is null) return;

        var (x, y) = ToRasterPoint(e.GetPosition(this));
        var node = NodeAtPixel(x, y);

        if (ReferenceEquals(node, _hovered)) return;

        _hovered = node;
        _hoverRect = FindShapeBounds(node);
        ToolTip.SetTip(this, node is null ? null : BuildTooltip(node));
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = null;
        _hoverRect = null;
        ToolTip.SetTip(this, null);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (_raster is null && _sunburstModel is null) return;

        var (x, y) = ToRasterPoint(e.GetPosition(this));
        var node = NodeAtPixel(x, y);
        if (node is null) return;

        SelectedNode = node;

        // Double-click drills in, matching SpaceSniffer.
        if (e.ClickCount == 2 && node.IsDirectory && ZoomCommand?.CanExecute(node) == true)
            ZoomCommand.Execute(node);

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var current = SelectedNode;
        if (current is null) return;

        switch (e.Key)
        {
            // Enter drills into the selection; Backspace steps back out.
            case Key.Enter when current.IsDirectory && ZoomCommand?.CanExecute(current) == true:
                ZoomCommand.Execute(current);
                e.Handled = true;
                break;

            case Key.Up when current.Parent is not null:
                SelectedNode = current.Parent;
                e.Handled = true;
                break;

            case Key.Down when current.Children is { Length: > 0 } children:
                SelectedNode = children[0];
                e.Handled = true;
                break;

            case Key.Left:
            case Key.Right:
                MoveToSibling(current, e.Key == Key.Right ? 1 : -1);
                e.Handled = true;
                break;
        }
    }

    private void MoveToSibling(FileNode node, int direction)
    {
        var siblings = node.Parent?.Children;
        if (siblings is null || siblings.Length == 0) return;

        var index = Array.IndexOf(siblings, node);
        if (index < 0) return;

        var next = index + direction;
        if (next < 0 || next >= siblings.Length) return;

        SelectedNode = siblings[next];
    }

    private static string BuildTooltip(FileNode node)
    {
        var viewModel = new ViewModels.NodeViewModel(node, showSizeOnDisk: false);
        return viewModel.Tooltip;
    }
}
