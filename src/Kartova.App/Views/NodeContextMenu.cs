using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;

namespace Kartova.App.Views;

/// <summary>
/// The actions available on a scanned node, shared by the directory grid and the treemap.
/// </summary>
/// <remarks>
/// Defined in code rather than as a XAML resource because a <see cref="ContextMenu"/> is a
/// control and cannot be attached to two owners at once. Declaring it as a type lets each
/// pane instantiate its own copy from a single definition, so the two menus cannot drift
/// apart as commands are added.
///
/// Every item targets <c>MainViewModel.SelectedNode</c>, so both panes must select whatever
/// was right-clicked before the menu opens. The treemap does this in its pointer handler;
/// the grid does it in <c>ResultsView.OnTreeRightButton</c>.
/// </remarks>
public sealed class NodeContextMenu : ContextMenu
{
    /// <summary>
    /// Resolve styles as a plain <see cref="ContextMenu"/>.
    /// </summary>
    /// <remarks>
    /// Control themes are keyed by exact type, so without this the subclass matches no theme,
    /// gets no template, and silently never opens.
    /// </remarks>
    protected override Type StyleKeyOverride => typeof(ContextMenu);

    public NodeContextMenu()
    {
        Items.Add(Item("Menu.Open", "OpenSelectedCommand"));
        Items.Add(Item("Menu.Reveal", "RevealSelectedCommand"));
        Items.Add(Item("Menu.Terminal", "OpenTerminalHereCommand"));
        Items.Add(new Separator());

        Items.Add(Item("Menu.CopyPath", "CopyPathCommand", gesture: new KeyGesture(Key.C, KeyModifiers.Control)));
        Items.Add(Item("Menu.ZoomInto", "ZoomIntoCommand", parameterPath: "SelectedNode",
            gesture: new KeyGesture(Key.Enter)));
        Items.Add(Item("Menu.Rescan", "RefreshSelectedCommand", gesture: new KeyGesture(Key.F5)));
        Items.Add(new Separator());

        Items.Add(Item("Menu.Trash", "RequestDeleteToTrashCommand", gesture: new KeyGesture(Key.Delete)));
        Items.Add(Item("Menu.DeletePermanently", "RequestDeletePermanentlyCommand",
            gesture: new KeyGesture(Key.Delete, KeyModifiers.Shift)));
    }

    /// <summary>
    /// Builds one item from a translation key.
    /// </summary>
    /// <remarks>
    /// The header binds through the localizer rather than being set to a fixed string, so a
    /// language change retranslates menus that were built before it. The gesture is a hint
    /// only — the shortcuts themselves are handled by the view, so they work whether or not
    /// the menu is open.
    /// </remarks>
    private static MenuItem Item(
        string headerKey, string commandPath, string? parameterPath = null, KeyGesture? gesture = null)
    {
        var item = new MenuItem();

        item.Bind(HeaderedSelectingItemsControl.HeaderProperty, new Binding
        {
            Path = nameof(Localization.Loc.TranslatedString.Value),
            Source = Localization.Loc.Get(headerKey),
            Mode = BindingMode.OneWay,
        });

        item.Bind(MenuItem.CommandProperty, new Binding(commandPath));
        if (parameterPath is not null)
            item.Bind(MenuItem.CommandParameterProperty, new Binding(parameterPath));
        if (gesture is not null)
            item.InputGesture = gesture;

        return item;
    }
}
