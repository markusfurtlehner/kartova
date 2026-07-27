using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirStat.App.Localization;
using DirStat.Core.Files;
using DirStat.Core.Model;

namespace DirStat.App.ViewModels;

/// <summary>
/// A collapsible section of the file-type list, gathering related extensions.
/// </summary>
/// <remarks>
/// A real scan produces hundreds of distinct extensions, which as a flat list buries the
/// answer to "what kind of thing is filling this disk". Families answer that in one line each
/// and still open up to the individual types underneath.
/// </remarks>
public sealed partial class ExtensionFamilyViewModel : ObservableObject
{
    public ExtensionFamilyViewModel(FileTypeColors.Family family, IReadOnlyList<ExtensionViewModel> members)
    {
        FamilyKind = family;
        Members = new ObservableCollection<ExtensionViewModel>(members);
        Swatch = new SolidColorBrush(Color.FromUInt32(FileTypeColors.ColorOfFamily(family)));

        TotalSize = members.Sum(m => m.TotalSize);
        FileCount = members.Sum(m => m.FileCount);
        Fraction = members.Sum(m => m.Fraction);
    }

    public FileTypeColors.Family FamilyKind { get; }
    public ObservableCollection<ExtensionViewModel> Members { get; }
    public IBrush Swatch { get; }

    public long TotalSize { get; }
    public int FileCount { get; }
    public double Fraction { get; }

    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public string Title => Loc.T($"Family.{FamilyKind}");
    public string SizeText => SizeFormatter.Format(TotalSize);
    public string CountText => SizeFormatter.FormatCount(FileCount);
    public string PercentText => SizeFormatter.FormatPercent(Fraction);

    public string Tooltip =>
        $"{Title}\n\n{SizeText} across {CountText} files\n{PercentText} of everything scanned";
}

/// <summary>One row in the file-type breakdown.</summary>
public sealed class ExtensionViewModel(ExtensionStat stat)
{
    public ExtensionStat Stat { get; } = stat;

    public string Extension => Stat.Extension;
    public string DisplayName => Stat.DisplayName;
    public long TotalSize => Stat.TotalSize;
    public int FileCount => Stat.FileCount;

    public string SizeText => SizeFormatter.Format(Stat.TotalSize);
    public string CountText => SizeFormatter.FormatCount(Stat.FileCount);
    public string PercentText => SizeFormatter.FormatPercent(Stat.Fraction);

    /// <summary>Share of the scanned total, 0..1. Drives the inline bar width.</summary>
    public double Fraction => Math.Clamp(Stat.Fraction, 0, 1);

    public IBrush Swatch { get; } = new SolidColorBrush(Color.FromUInt32(stat.Color));

    public string Tooltip =>
        $"{DisplayName}\n\n{SizeText} across {CountText} files\n{PercentText} of everything scanned";
}
