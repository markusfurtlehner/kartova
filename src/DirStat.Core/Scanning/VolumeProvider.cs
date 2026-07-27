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

        return Deduplicate(volumes);
    }

    /// <summary>
    /// Collapses mount points that refer to the same underlying device.
    /// </summary>
    /// <remarks>
    /// One device is routinely visible at several paths — bind mounts, containers, and WSL,
    /// which surfaces the same disk as <c>/</c>, <c>/mnt/wslg/distro</c> and a Docker bind
    /// path all at once. Offering each as a separate "drive" is pure noise in a tool whose
    /// whole job is accounting for space, so the shortest mount point wins and the rest are
    /// dropped. Where the device cannot be determined the mount point stands in, which at
    /// worst leaves the previous behaviour.
    /// </remarks>
    private static IReadOnlyList<VolumeInfo> Deduplicate(List<VolumeInfo> volumes)
    {
        var devices = ReadMountDevices();
        var seenPaths = new HashSet<string>(PathComparerOrdinal);
        var seenDevices = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<VolumeInfo>(volumes.Count);

        // Largest first, then shortest path, so the canonical mount is the one kept.
        foreach (var volume in volumes
                     .OrderByDescending(v => v.TotalBytes)
                     .ThenBy(v => v.RootPath.Length))
        {
            if (!seenPaths.Add(volume.RootPath)) continue;

            if (devices.TryGetValue(volume.RootPath, out var device) && !seenDevices.Add(device))
                continue;

            unique.Add(volume);
        }

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

    /// <summary>Maps mount point to backing device, read from <c>/proc/mounts</c>.</summary>
    private static Dictionary<string, string> ReadMountDevices()
    {
        var map = new Dictionary<string, string>(PathComparerOrdinal);
        if (!OperatingSystem.IsLinux()) return map;

        try
        {
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                // device mountpoint fstype options dump pass
                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var device = parts[0];
                // Anonymous sources (tmpfs, none, overlay) identify nothing; skip them so
                // unrelated mounts are not collapsed into one another.
                if (!device.StartsWith('/')) continue;

                // Octal escapes are used for spaces and tabs in mount paths.
                var mountPoint = parts[1].Replace("\\040", " ").Replace("\\011", "\t");
                map[mountPoint] = device;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without the table we simply fall back to de-duplicating by path alone.
        }

        return map;
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
        "tmpfs", "devtmpfs", "devfs", "udev", "proc", "procfs", "sysfs", "cgroup", "cgroup2",
        "squashfs", "overlay", "ramfs", "autofs", "debugfs", "tracefs", "securityfs",
        "pstore", "bpf", "configfs", "fusectl", "mqueue", "hugetlbfs", "binfmt_misc",
        "efivarfs", "nsfs", "fuse.gvfsd-fuse", "fuse.portal",
        // Read-only images: a mounted ISO is not somewhere anyone frees space.
        "iso9660", "isofs", "udf",
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

            // WSL plumbing. These are the distro's own machinery — driver shares, the WSLg
            // session, Docker Desktop's bind mounts — not places a user stores anything.
            // Windows drives stay visible, because /mnt/c and friends genuinely are volumes.
            if (path.StartsWith("/mnt/wsl", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/usr/lib/wsl", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/Docker/", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/tmp/.X11-unix", StringComparison.Ordinal)) return true;
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
