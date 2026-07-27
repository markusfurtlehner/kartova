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

    /// <summary>
    /// Chooses between a custom caption and the system one.
    /// </summary>
    /// <remarks>
    /// Windows and macOS honour an extended client area, so the app draws its own title bar
    /// and the shell looks the same on both. Linux window managers are not obliged to, and
    /// several — WSLg among them — decorate the window regardless, which would leave two
    /// stacked title bars. There, the system caption is kept and the app's own row degrades
    /// to a header strip without window controls.
    /// </remarks>
    private void ApplyPlatformChrome()
    {
        if (OperatingSystem.IsLinux())
        {
            SystemDecorations = SystemDecorations.Full;
            if (this.FindControl<StackPanel>("WindowControls") is { } controls)
                controls.IsVisible = false;
            return;
        }

        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
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

            if (settings.WindowMaximized) WindowState = WindowState.Maximized;
        }

        CenterOnPrimaryScreen();
    }

    /// <summary>
    /// Places the window in the middle of the primary monitor.
    /// </summary>
    /// <remarks>
    /// <see cref="WindowStartupLocation.CenterScreen"/> centres on the screen the platform
    /// nominates, and on X11 that is the whole virtual desktop rather than one monitor. With
    /// several monitors the window lands at the centre of their combined bounding box, which
    /// can be a different monitor entirely — or, where a monitor sits above or left of the
    /// primary and the layout has a negative origin, somewhere the user never looks. The
    /// symptom is unpleasant, because the app appears to launch and then hang: there is a
    /// taskbar entry and no visible window. Centring explicitly on the primary is what people
    /// expect on every platform, so it is done here rather than left to the toolkit.
    /// </remarks>
    private void CenterOnPrimaryScreen()
    {
        var screens = Screens;
        if (screens is null || screens.ScreenCount == 0) return;

        var size = PixelSize.FromSize(ClientSize, RenderScaling);
        if (size.Width <= 0 || size.Height <= 0) return;

        var target = screens.Primary ?? screens.All[0];
        var area = target.WorkingArea;

        Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - size.Width) / 2),
            area.Y + Math.Max(0, (area.Height - size.Height) / 2));
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
