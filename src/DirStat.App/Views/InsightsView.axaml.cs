using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DirStat.App.Views;

public partial class InsightsView : UserControl
{
    public InsightsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
