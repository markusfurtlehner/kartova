namespace DirStat.Core.Model;

/// <summary>
/// An immutable snapshot of scan progress. Published on a timer rather than per file,
/// so the UI thread sees a steady low-rate stream regardless of scan throughput.
/// </summary>
public readonly record struct ScanProgress(
    long FilesSeen,
    long DirectoriesSeen,
    long BytesSeen,
    long DeniedDirectories,
    string CurrentPath,
    TimeSpan Elapsed,
    bool IsComplete)
{
    /// <summary>Files per second averaged over the whole scan.</summary>
    public double FilesPerSecond =>
        Elapsed.TotalSeconds <= 0.001 ? 0 : FilesSeen / Elapsed.TotalSeconds;

    /// <summary>Bytes per second averaged over the whole scan.</summary>
    public double BytesPerSecond =>
        Elapsed.TotalSeconds <= 0.001 ? 0 : BytesSeen / Elapsed.TotalSeconds;
}
