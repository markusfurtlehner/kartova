using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DirStat.App.Views;

public partial class ScanProgressView : UserControl
{
    public ScanProgressView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
