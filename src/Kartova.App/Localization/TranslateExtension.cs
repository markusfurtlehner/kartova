using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Kartova.App.Localization;

/// <summary>
/// XAML shorthand for a translated string: <c>Text="{l:T Volumes.Title}"</c>.
/// </summary>
/// <remarks>
/// Returns a binding rather than a plain string, so switching language retranslates the live
/// window instead of only affecting views created afterwards.
/// </remarks>
public sealed class TExtension : MarkupExtension
{
    public TExtension() => Key = string.Empty;

    public TExtension(string key) => Key = key;

    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding
        {
            Path = nameof(Loc.TranslatedString.Value),
            Source = Loc.Get(Key),
            Mode = BindingMode.OneWay,
        };
}
