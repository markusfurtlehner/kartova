using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DirStat.App.ViewModels;

namespace DirStat.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // The window owns the platform services the view model needs but should not know about.
        DataContextChanged += (_, _) => WireUpViewModel();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void WireUpViewModel()
    {
        if (DataContext is not MainViewModel viewModel) return;

        viewModel.PickFoldersAsync = PickFoldersAsync;
        viewModel.PickSaveFileAsync = PickSaveFileAsync;
        viewModel.CopyToClipboardAsync = CopyToClipboardAsync;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var settings = viewModel.Settings;
        if (settings.WindowWidth > 400 && settings.WindowHeight > 300)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }

        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
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
