using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Kartova.App.ViewModels;

namespace Kartova.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ApplyPlatformChrome();

        // The window owns the platform services the view model needs but should not know about.
        DataContextChanged += (_, _) => WireUpViewModel();
        Opened += OnOpened;
        Closing += OnClosing;

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Width the macOS traffic lights occupy, plus breathing room.</summary>
    private const double TrafficLightInset = 78;

    /// <summary>
    /// Chooses between a custom caption and the system one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows gets the app's own caption: its managed chrome cannot be drawn over user
    /// content, so an extended client area means drawing the buttons ourselves.
    /// </para>
    /// <para>
    /// macOS gets the real thing. Traffic lights belong on the left, they are muscle memory,
    /// and a Windows-style row of buttons on the right is the single clearest sign that an
    /// application was ported rather than written for the platform. Asking for system chrome
    /// also hands the window's corner rounding back to macOS, so it follows whatever the OS
    /// does rather than whatever we guessed - which is the only way it stays right across
    /// versions and user settings.
    /// </para>
    /// <para>
    /// Linux window managers are not obliged to honour an extended client area, and several -
    /// WSLg among them - decorate regardless, which would leave two stacked title bars. There
    /// the system caption is kept and the app's own row degrades to a header strip.
    /// </para>
    /// </remarks>
    private void ApplyPlatformChrome()
    {
        if (OperatingSystem.IsLinux())
        {
            SystemDecorations = SystemDecorations.Full;
            HideOwnWindowControls();
            return;
        }

        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;

        if (OperatingSystem.IsMacOS())
        {
            // PreferSystemChrome keeps the native traffic lights; the thick-title-bar hint
            // drops them to sit centred against a taller caption row rather than riding high.
            ExtendClientAreaChromeHints =
                Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome |
                Avalonia.Platform.ExtendClientAreaChromeHints.OSXThickTitleBar;

            HideOwnWindowControls();

            // Shift the title and path clear of the traffic lights they would otherwise
            // sit underneath.
            if (this.FindControl<StackPanel>("TitleBarLeft") is { } left)
                left.Margin = new Thickness(TrafficLightInset, 0, 0, 0);

            return;
        }

        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
    }

    private void HideOwnWindowControls()
    {
        if (this.FindControl<StackPanel>("WindowControls") is { } controls)
            controls.IsVisible = false;
    }

    private void WireUpViewModel()
    {
        if (DataContext is not MainViewModel viewModel) return;

        viewModel.PickFoldersAsync = PickFoldersAsync;
        viewModel.PickSaveFileAsync = PickSaveFileAsync;
        viewModel.CopyToClipboardAsync = CopyToClipboardAsync;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            var settings = viewModel.Settings;
            if (settings.WindowWidth > 400 && settings.WindowHeight > 300)
            {
                Width = settings.WindowWidth;
                Height = settings.WindowHeight;
            }

            if (settings.WindowMaximized)
            {
                WindowState = WindowState.Maximized;
                return; // the window manager owns its size and place from here
            }
        }

        FitToPrimaryScreen();
    }

    /// <summary>
    /// Sizes the window to fit the screen it opens on, then centres it there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Size and position are settled together on purpose. The default size suits a desktop
    /// monitor and a saved size came from whatever display the app last ran on; neither is a
    /// promise about this one. A 1280x800 laptop or virtual machine is smaller than the
    /// default in both directions, and a window larger than the screen puts its own title bar
    /// out of reach with no obvious way to drag it back.
    /// </para>
    /// <para>
    /// Centring cannot read <c>ClientSize</c> to do this, because that still reports the size
    /// from before the clamp - the layout pass has not run yet - which placed a shrunk window
    /// hard against the left edge.
    /// </para>
    /// <para>
    /// <see cref="WindowStartupLocation.CenterScreen"/> is not used because it centres on the
    /// screen the platform nominates, and on X11 that is the whole virtual desktop. With
    /// several monitors the window lands in the centre of their combined bounding box, which
    /// can be a different monitor entirely, or - where a monitor sits above or left of the
    /// primary and the layout has a negative origin - somewhere the user never looks. That
    /// reads as the app hanging: a taskbar entry and no visible window.
    /// </para>
    /// </remarks>
    private void FitToPrimaryScreen()
    {
        var screens = Screens;
        if (screens is null || screens.ScreenCount == 0) return;

        var target = screens.Primary ?? screens.All[0];
        var area = target.WorkingArea;
        var scaling = target.Scaling <= 0 ? 1.0 : target.Scaling;

        // WorkingArea leaves out the menu bar, dock and taskbar, and is in physical pixels
        // while Width and Height are device-independent.
        var availableWidth = area.Width / scaling;
        var availableHeight = area.Height / scaling;
        if (availableWidth <= 0 || availableHeight <= 0) return;

        // A margin so the window reads as placed rather than wedged against the edges.
        const double margin = 0.96;

        // The minimum still wins, so fold it in here rather than let it surprise the centring.
        var width = Math.Max(Math.Min(Width, availableWidth * margin), MinWidth);
        var height = Math.Max(Math.Min(Height, availableHeight * margin), MinHeight);

        Width = width;
        Height = height;

        Position = new PixelPoint(
            area.X + (int)Math.Max(0, (area.Width - width * scaling) / 2),
            area.Y + (int)Math.Max(0, (area.Height - height * scaling) / 2));
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        viewModel.PersistWindowState(Width, Height, WindowState == WindowState.Maximized);
        viewModel.PersistSettings();
    }

    // ------------------------------------------------------- platform services

    private async Task<IReadOnlyList<string>> PickFoldersAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folders to scan",
            AllowMultiple = true,
        });

        return folders
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToArray();
    }

    private async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        var extension = Path.GetExtension(suggestedName).TrimStart('.');

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export scan",
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices =
            [
                extension == "csv"
                    ? new FilePickerFileType("CSV") { Patterns = ["*.csv"] }
                    : new FilePickerFileType("JSON") { Patterns = ["*.json"] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    private async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is not null) await Clipboard.SetTextAsync(text);
    }

    // ---------------------------------------------------------- about dialog

    /// <summary>Dismisses the About dialog when the dimmed backdrop is clicked.</summary>
    private void OnAboutScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.IsAboutOpen = false;
    }

    /// <summary>
    /// Escape closes the About dialog.
    /// </summary>
    /// <remarks>
    /// Registered as a tunnelling handler so it runs before the screens underneath see the key.
    /// The dialog covers them, so a keystroke aimed at it should never reach the view behind.
    /// </remarks>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is not MainViewModel { IsAboutOpen: true } viewModel) return;

        viewModel.IsAboutOpen = false;
        e.Handled = true;
    }

    // --------------------------------------------------------- window chrome

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only a primary-button drag should move the window.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximized();

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
