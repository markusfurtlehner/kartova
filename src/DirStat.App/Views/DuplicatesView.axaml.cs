using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DirStat.App.Views;

public partial class DuplicatesView : UserControl
{
    public DuplicatesView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
