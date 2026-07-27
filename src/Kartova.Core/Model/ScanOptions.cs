namespace Kartova.Core.Model;

/// <summary>Tunables for a scan. Defaults are the safe, fast choices.</summary>
public sealed class ScanOptions
{
    /// <summary>
    /// Worker count. Defaults to the processor count, clamped to a sensible band —
    /// disk enumeration stops scaling well past about 16 threads even on fast NVMe.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 16);

    /// <summary>
    /// Descend into symlinks, junctions and mount points. Off by default: following
    /// them risks infinite cycles and double-counts content reachable by two paths.
    /// </summary>
    public bool FollowReparsePoints { get; set; }

    /// <summary>
    /// Detect hard links and count shared content only once. Off by default because it
    /// requires a per-file metadata query, which roughly halves scan throughput.
    /// </summary>
    public bool DetectHardLinks { get; set; }

    /// <summary>Compute cluster-rounded allocation size alongside logical size. Costs nothing.</summary>
    public bool ComputeSizeOnDisk { get; set; } = true;

    /// <summary>
    /// Query each file's true allocation instead of rounding to the cluster size. Correct for
    /// sparse and compressed files, but costs one metadata call per file. Off by default.
    /// </summary>
    public bool ExactAllocation { get; set; }

    /// <summary>Add synthetic free-space and unknown-space nodes when scanning a volume root.</summary>
    public bool IncludeFreeSpace { get; set; } = true;

    public bool SkipHidden { get; set; }
    public bool SkipSystem { get; set; }

    /// <summary>Absolute paths that are never entered. Seeded with platform pseudo-filesystems.</summary>
    public HashSet<string> ExcludedPaths { get; } =
        new(PlatformDefaults.DefaultExclusions, PathComparer.Instance);

    /// <summary>Directory names excluded wherever they appear, for example <c>node_modules</c>.</summary>
    public HashSet<string> ExcludedDirectoryNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often progress is published. 50 ms keeps the UI live without flooding it.</summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    public ScanOptions Clone()
    {
        var copy = new ScanOptions
        {
            MaxDegreeOfParallelism = MaxDegreeOfParallelism,
            FollowReparsePoints = FollowReparsePoints,
            DetectHardLinks = DetectHardLinks,
            ComputeSizeOnDisk = ComputeSizeOnDisk,
            ExactAllocation = ExactAllocation,
            IncludeFreeSpace = IncludeFreeSpace,
            SkipHidden = SkipHidden,
            SkipSystem = SkipSystem,
            ProgressInterval = ProgressInterval,
        };
        copy.ExcludedPaths.Clear();
        foreach (var p in ExcludedPaths) copy.ExcludedPaths.Add(p);
        foreach (var d in ExcludedDirectoryNames) copy.ExcludedDirectoryNames.Add(d);
        return copy;
    }
}

/// <summary>Path equality that matches the host filesystem's case rules.</summary>
public sealed class PathComparer : IEqualityComparer<string>
{
    public static readonly PathComparer Instance = new();

    private static readonly StringComparer Inner =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public bool Equals(string? x, string? y) => Inner.Equals(Normalize(x), Normalize(y));

    public int GetHashCode(string obj) => Inner.GetHashCode(Normalize(obj) ?? string.Empty);

    private static string? Normalize(string? p)
    {
        if (string.IsNullOrEmpty(p)) return p;
        // Trailing separators are noise, but a bare root such as "/" or "C:\" must keep its own.
        return p.Length > 1 ? p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : p;
    }
}

/// <summary>Paths that are pointless or actively harmful to walk on each platform.</summary>
public static class PlatformDefaults
{
    public static IEnumerable<string> DefaultExclusions
    {
        get
        {
            if (OperatingSystem.IsLinux())
            {
                // Kernel pseudo-filesystems: infinite, synthetic, or blocking.
                yield return "/proc";
                yield return "/sys";
                yield return "/dev";
                yield return "/run";
                yield return "/tmp/.X11-unix";
            }
            else if (OperatingSystem.IsMacOS())
            {
                yield return "/System/Volumes/Data/private/var/vm";
                yield return "/dev";
                yield return "/.vol";
                yield return "/Volumes/.timemachine";
            }
            else if (OperatingSystem.IsWindows())
            {
                // Reachable only by SYSTEM; enumerating them just generates denials.
                yield return @"C:\System Volume Information";
                yield return @"C:\$Recycle.Bin";
                yield return @"C:\Windows\CSC";
            }
        }
    }
}
