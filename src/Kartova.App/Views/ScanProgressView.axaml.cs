using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kartova.App.Views;

public partial class ScanProgressView : UserControl
{
    public ScanProgressView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
