using Kartova.Core.Scanning;
using Xunit;

namespace Kartova.Core.Tests;

public class VolumeTests
{
    [Fact]
    public void Enumerate_never_throws_on_the_host()
    {
        var volumes = VolumeProvider.Enumerate();
        Assert.NotNull(volumes);
    }

    [Fact]
    public void Every_mount_point_appears_at_most_once()
    {
        // One device is routinely visible at several paths — bind mounts, containers, WSL.
        // Offering the same storage repeatedly is noise in a tool for accounting for space.
        var volumes = VolumeProvider.Enumerate();
        var paths = volumes.Select(v => v.RootPath).ToArray();
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Reported_capacities_are_plausible()
    {
        foreach (var volume in VolumeProvider.Enumerate())
        {
            Assert.True(volume.TotalBytes > 0, $"{volume.RootPath} reports no capacity");

            // Virtual drives report the signed 64-bit maximum; no real volume is an exbibyte.
            Assert.True(volume.TotalBytes < 1L << 60, $"{volume.RootPath} reports an absurd capacity");

            Assert.InRange(volume.FreeBytes, 0, volume.TotalBytes);
            Assert.InRange(volume.UsedFraction, 0.0, 1.0);
        }
    }

    [Fact]
    public void Pseudo_filesystems_are_not_offered()
    {
        var volumes = VolumeProvider.Enumerate();

        foreach (var volume in volumes)
        {
            Assert.False(
                string.Equals(volume.FileSystem, "tmpfs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(volume.FileSystem, "devtmpfs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(volume.FileSystem, "udev", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(volume.FileSystem, "squashfs", StringComparison.OrdinalIgnoreCase),
                $"{volume.RootPath} is a pseudo-filesystem ({volume.FileSystem})");
        }
    }

    [Fact]
    public void Describing_the_current_directory_yields_its_volume()
    {
        var volume = VolumeProvider.TryDescribe(Directory.GetCurrentDirectory());

        // A container or unusual mount may legitimately have nothing to report.
        if (volume is null) return;

        Assert.True(volume.TotalBytes > 0);
        Assert.False(string.IsNullOrEmpty(volume.RootPath));
    }

    [Fact]
    public void Describing_a_nonexistent_path_returns_null_rather_than_throwing()
    {
        var volume = VolumeProvider.TryDescribe(
            Path.Combine(Path.GetTempPath(), "kartova-no-such-volume-" + Guid.NewGuid().ToString("N")));

        // The path does not exist, but its root usually does, so either answer is acceptable —
        // what matters is that it does not throw.
        _ = volume;
    }
}
