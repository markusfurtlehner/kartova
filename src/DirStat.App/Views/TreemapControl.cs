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
            change.Property == DirectoryFramesProperty)
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
            _selectionRect = FindTileRect(SelectedNode);
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

        try
        {
            var raster = await Task.Run(() =>
            {
                var model = TreemapLayout.Build(root, width, height, options);
                token.ThrowIfCancellationRequested();
                return new TreemapRenderer().Render(model, options);
            }, token);

            if (token.IsCancellationRequested) return;

            _bitmap = CreateBitmap(raster, width, height, scaling);
            _raster = raster;
            _rasterSize = new PixelSize(width, height);
            _selectionRect = FindTileRect(SelectedNode);
            _hoverRect = FindTileRect(_hovered);

            InvalidateVisual();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer render; the previous bitmap stays on screen meanwhile.
        }
    }

    /// <summary>Copies the rasterized pixels into a GPU-uploadable bitmap.</summary>
    private static WriteableBitmap CreateBitmap(TreemapRaster raster, int width, int height, double scaling)
    {
        // Matching the bitmap DPI to the render scaling makes one bitmap pixel land on one
        // physical pixel, so the cushion detail stays crisp on a HiDPI display.
        var dpi = new Vector(96 * scaling, 96 * scaling);
        var bitmap = new WriteableBitmap(new PixelSize(width, height), dpi, PixelFormat.Bgra8888, AlphaFormat.Opaque);

        using var buffer = bitmap.Lock();
        var source = raster.Pixels;

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

    /// <summary>Finds the drawn rectangle for a node, or null when it is not on the map.</summary>
    private Rect? FindTileRect(FileNode? node)
    {
        var raster = _raster;
        if (node is null || raster is null) return null;

        var tiles = raster.Model.Tiles;
        for (var i = 0; i < tiles.Length; i++)
        {
            if (!ReferenceEquals(tiles[i].Node, node)) continue;
            ref readonly var tile = ref tiles[i];
            return new Rect(tile.X, tile.Y, tile.Width, tile.Height);
        }

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

        var raster = _raster;
        if (raster is null) return;

        var (x, y) = ToRasterPoint(e.GetPosition(this));
        var node = raster.NodeAt(x, y);

        if (ReferenceEquals(node, _hovered)) return;

        _hovered = node;
        _hoverRect = FindTileRect(node);
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

        var raster = _raster;
        if (raster is null) return;

        var (x, y) = ToRasterPoint(e.GetPosition(this));
        var node = raster.NodeAt(x, y);
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
