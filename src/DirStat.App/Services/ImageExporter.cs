using Avalonia.Media.Imaging;

namespace DirStat.App.Services;

/// <summary>Writes the rendered chart to a PNG.</summary>
/// <remarks>
/// The chart is already a pixel buffer by the time it reaches the screen, so saving it is a
/// matter of handing that buffer to the encoder — there is no second rendering path to keep
/// in step with the first, and what lands in the file is exactly what was on screen.
/// </remarks>
public static class ImageExporter
{
    public static void SavePng(Bitmap bitmap, string path)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        bitmap.Save(stream);
    }
}
