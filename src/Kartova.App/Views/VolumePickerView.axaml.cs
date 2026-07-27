using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kartova.App.Views;

public partial class VolumePickerView : UserControl
{
    public VolumePickerView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
