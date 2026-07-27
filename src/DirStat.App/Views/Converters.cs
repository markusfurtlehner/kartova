using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DirStat.App.ViewModels;

namespace DirStat.App.Views;

/// <summary>Value converters used by the XAML views.</summary>
public static class Converters
{
    /// <summary>True when the bound screen equals the one named by the parameter.</summary>
    public static readonly IValueConverter ScreenIs = new FuncValueConverter<AppScreen, string?, bool>(
        (screen, parameter) => parameter is not null &&
                               Enum.TryParse<AppScreen>(parameter, ignoreCase: true, out var target) &&
                               screen == target);

    /// <summary>True when a string has content. Drives visibility of optional rows.</summary>
    public static readonly IValueConverter HasText =
        new FuncValueConverter<string?, bool>(text => !string.IsNullOrWhiteSpace(text));

    /// <summary>Inverts a boolean.</summary>
    public static readonly IValueConverter Not =
        new FuncValueConverter<bool, bool>(value => !value);

    /// <summary>
    /// Left margin for a row's colour swatch. Leaf rows have no chevron, so they get the
    /// chevron's width back as padding and stay aligned with their expandable siblings.
    /// </summary>
    public static readonly IValueConverter LeafSpacer =
        new FuncValueConverter<bool, Avalonia.Thickness>(
            hasChildren => hasChildren ? new Avalonia.Thickness(0) : new Avalonia.Thickness(19, 0, 0, 0));

    /// <summary>Pane heading that follows whichever chart is showing.</summary>
    public static readonly IValueConverter ChartTitle =
        new FuncValueConverter<bool, string>(sunburst =>
            Localization.Loc.T(sunburst ? "Results.Sunburst" : "Results.Treemap"));

    private static readonly Geometry CollapsedArrow = Geometry.Parse("M 0,0 L 6,4 L 0,8 Z");
    private static readonly Geometry ExpandedArrow = Geometry.Parse("M 0,0 L 8,0 L 4,6 Z");

    /// <summary>Disclosure triangle, pointing right when collapsed and down when open.</summary>
    public static readonly IValueConverter Chevron =
        new FuncValueConverter<bool, Geometry>(expanded => expanded ? ExpandedArrow : CollapsedArrow);
}

/// <summary>
/// Two-input converter, since Avalonia's built-in <c>FuncValueConverter</c> does not take
/// a converter parameter.
/// </summary>
public sealed class FuncValueConverter<TIn, TParam, TOut>(Func<TIn, TParam?, TOut> convert) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TIn typed) return default(TOut);
        var typedParameter = parameter is TParam p ? p : default;
        return convert(typed, typedParameter);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way only.");
}
