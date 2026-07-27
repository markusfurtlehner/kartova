using Avalonia.Media;
using DirStat.Core.Files;
using DirStat.Core.Model;

namespace DirStat.App.ViewModels;

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
