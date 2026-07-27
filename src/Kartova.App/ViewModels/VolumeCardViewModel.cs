using Avalonia.Media;
using Kartova.Core.Files;
using Kartova.Core.Scanning;

namespace Kartova.App.ViewModels;

/// <summary>A selectable volume on the opening screen.</summary>
public sealed class VolumeCardViewModel(VolumeInfo volume)
{
    public VolumeInfo Volume { get; } = volume;

    public string RootPath => Volume.RootPath;
    public string Title => Volume.DisplayName;

    /// <summary>Mount point, shown beneath the label when the two differ.</summary>
    public string Subtitle =>
        string.Equals(Volume.DisplayName, Volume.RootPath, StringComparison.Ordinal)
            ? Volume.FileSystem
            : $"{Volume.RootPath}  ·  {Volume.FileSystem}";

    public string UsedText => SizeFormatter.Format(Volume.UsedBytes);
    public string FreeText => SizeFormatter.Format(Volume.FreeBytes);
    public string TotalText => SizeFormatter.Format(Volume.TotalBytes);
    public string PercentText => SizeFormatter.FormatPercent(Volume.UsedFraction);

    public double UsedFraction => Volume.UsedFraction;

    public string DriveKind => Volume.DriveType switch
    {
        "Fixed" => "Internal",
        "Removable" => "Removable",
        "Network" => "Network",
        "CDRom" => "Optical",
        "Ram" => "RAM disk",
        _ => Volume.DriveType,
    };

    /// <summary>
    /// Ring colour graded by how full the volume is. A nearly full disk is the reason
    /// someone opens this app, so it should be visible before any text is read.
    /// </summary>
    public IBrush RingBrush => new SolidColorBrush(Color.FromUInt32(
        Volume.UsedFraction switch
        {
            >= 0.92 => 0xFFFF6B8A,
            >= 0.80 => 0xFFFFB24C,
            _ => 0xFF4C8DFF,
        }));
}
