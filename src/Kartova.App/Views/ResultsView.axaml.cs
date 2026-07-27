using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Kartova.App.ViewModels;

namespace Kartova.App.Views;

public partial class ResultsView : UserControl
{
    public ResultsView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);

        // Tunnel, because ListBoxItem handles the bubbling event and would swallow this.
        AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);

        // Saving the chart needs the bitmap the control actually rendered, so the view hands
        // the view model a way to reach it rather than re-rendering a second time.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel model)
                model.GetChartImage = () => this.FindControl<TreemapControl>("Treemap")?.CurrentImage;
        };
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

    /// <summary>
    /// Selects the row under a right-click before the context menu opens.
    /// </summary>
    /// <remarks>
    /// A list does not select on the secondary button by default, so without this the menu
    /// would act on whatever happened to be selected already — which is how a right-click
    /// on one folder ends up deleting a different one.
    /// </remarks>
    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (Tree is not { } tree) return;

        // Walk up from whatever was hit to the row that owns it.
        for (var control = e.Source as Control; control is not null; control = control.Parent as Control)
        {
            if (control is ListBox) break;               // clicked empty space below the rows
            if (control.DataContext is not NodeViewModel row) continue;

            tree.SelectedRow = row;
            break;
        }
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

            // Ctrl+C inside the filter box must still copy the text being edited.
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) && !typing:
                model.CopyPathCommand.Execute(null);
                e.Handled = true;
                break;

            // Enter drills into the selection from anywhere, matching the menu's hint.
            case Key.Enter when !typing:
                model.ZoomIntoCommand.Execute(model.SelectedNode);
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
