using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DirStat.App.ViewModels;

namespace DirStat.App.Views;

public partial class ResultsView : UserControl
{
    public ResultsView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Model => DataContext as MainViewModel;

    private DirectoryTreeViewModel? Tree => Model?.Tree;

    // ------------------------------------------------------------ tree grid

    /// <summary>Toggles a branch without disturbing the current row selection.</summary>
    private void OnChevronClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: NodeViewModel row }) Tree?.Toggle(row);
        e.Handled = true;
    }

    /// <summary>Double-clicking a folder row expands it; a file row opens it.</summary>
    private void OnTreeRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Tree is not { SelectedRow: { } row } tree) return;

        if (row.HasChildren) tree.Toggle(row);
        else Model?.OpenSelectedCommand.Execute(null);

        e.Handled = true;
    }

    private void OnSortByName(object? sender, RoutedEventArgs e) => Tree?.SetSort(TreeSortKey.Name);

    private void OnSortBySize(object? sender, RoutedEventArgs e) => Tree?.SetSort(TreeSortKey.Size);

    private void OnSortByItems(object? sender, RoutedEventArgs e) => Tree?.SetSort(TreeSortKey.Items);

    // ------------------------------------------------------------ shortcuts

    /// <summary>
    /// Application shortcuts. Handled on the tunnel so they fire wherever focus sits, except
    /// inside a text box, where ordinary typing must win.
    /// </summary>
    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model) return;

        var typing = e.Source is TextBox;

        switch (e.Key)
        {
            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                this.FindControl<TextBox>("FilterBox")?.Focus();
                e.Handled = true;
                break;

            case Key.Escape when typing:
                model.FilterText = string.Empty;
                e.Handled = true;
                break;

            case Key.F5 when !typing:
                model.RefreshSelectedCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Back when !typing:
                model.ZoomOutCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Delete when !typing:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    model.RequestDeletePermanentlyCommand.Execute(null);
                else
                    model.RequestDeleteToTrashCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
