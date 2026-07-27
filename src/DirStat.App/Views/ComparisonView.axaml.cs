using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DirStat.App.Views;

public partial class ComparisonView : UserControl
{
    public ComparisonView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
