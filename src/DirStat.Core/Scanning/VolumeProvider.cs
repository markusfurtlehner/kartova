namespace DirStat.Core.Scanning;

/// <summary>A mounted volume the user can choose to scan.</summary>
public sealed class VolumeInfo
{
    public required string RootPath { get; init; }
    public required string Label { get; init; }
    public required string FileSystem { get; init; }
    public required string DriveType { get; init; }
    public required long TotalBytes { get; init; }
    public required long FreeBytes { get; init; }
    public required bool IsReady { get; init; }

    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    /// <summary>Used share of the volume, 0..1.</summary>
    public double UsedFraction => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes;

    /// <summary>Label if the volume has one, otherwise the mount point.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? RootPath : Label;
}

/// <summary>Enumerates mounted volumes across platforms.</summary>
public static class VolumeProvider
{
    /// <summary>
    /// Returns every volume worth offering to the user, largest first.
    /// </summary>
    /// <remarks>
    /// Pseudo-filesystems (tmpfs, devfs, procfs, snap loopbacks) are filtered out. They are
    /// either synthetic or read-only images, so showing them as scan targets is just noise.
    /// </remarks>
    public static IReadOnlyList<VolumeInfo> Enumerate()
    {
        var volumes = new List<VolumeInfo>();

        foreach (var drive in SafeGetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (IsPseudoFilesystem(drive)) continue;

                var total = drive.TotalSize;
                if (total <= 0) continue;

                // Virtual and placeholder drives report absurd capacities — commonly the
                // signed 64-bit maximum. No real volume approaches an exbibyte, so treat
                // anything at that scale as synthetic rather than showing "8.00 EiB free".
                if (total >= 1L << 60) continue;

                volumes.Add(new VolumeInfo
                {
                    RootPath = drive.RootDirectory.FullName,
                    Label = SafeLabel(drive),
                    FileSystem = SafeFormat(drive),
                    DriveType = drive.DriveType.ToString(),
                    TotalBytes = total,
                    FreeBytes = drive.AvailableFreeSpace,
                    IsReady = true,
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A volume that vanished or refuses inspection is simply not offered.
            }
        }

        // De-duplicate: Linux commonly reports the same device at several mount points.
        var seen = new HashSet<string>(PathComparerOrdinal);
        var unique = new List<VolumeInfo>(volumes.Count);
        foreach (var v in volumes.OrderByDescending(v => v.TotalBytes))
            if (seen.Add(v.RootPath))
                unique.Add(v);

        return unique;
    }

    /// <summary>Describes the volume containing <paramref name="path"/>, or null if unavailable.</summary>
    public static VolumeInfo? TryDescribe(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return null;

            return new VolumeInfo
            {
                RootPath = drive.RootDirectory.FullName,
                Label = SafeLabel(drive),
                FileSystem = SafeFormat(drive),
                DriveType = drive.DriveType.ToString(),
                TotalBytes = drive.TotalSize,
                FreeBytes = drive.AvailableFreeSpace,
                IsReady = true,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static StringComparer PathComparerOrdinal =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static DriveInfo[] SafeGetDrives()
    {
        try { return DriveInfo.GetDrives(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string SafeLabel(DriveInfo drive)
    {
        try { return drive.VolumeLabel ?? string.Empty; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    private static string SafeFormat(DriveInfo drive)
    {
        try { return drive.DriveFormat ?? string.Empty; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    private static readonly string[] PseudoFilesystems =
    [
        "tmpfs", "devtmpfs", "devfs", "proc", "procfs", "sysfs", "cgroup", "cgroup2",
        "squashfs", "overlay", "ramfs", "autofs", "debugfs", "tracefs", "securityfs",
        "pstore", "bpf", "configfs", "fusectl", "mqueue", "hugetlbfs", "binfmt_misc",
        "efivarfs", "nsfs", "fuse.gvfsd-fuse", "fuse.portal",
    ];

    private static bool IsPseudoFilesystem(DriveInfo drive)
    {
        var format = SafeFormat(drive);
        if (PseudoFilesystems.Contains(format, StringComparer.OrdinalIgnoreCase)) return true;

        var path = drive.RootDirectory.FullName;

        if (OperatingSystem.IsLinux())
        {
            // Snap packages mount one read-only loopback image per revision.
            if (path.StartsWith("/snap/", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/var/snap/", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/sys", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/proc", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/dev", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/run", StringComparison.Ordinal)) return true;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (path.StartsWith("/System/Volumes/VM", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/System/Volumes/Preboot", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/System/Volumes/Update", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/private/var/vm", StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
