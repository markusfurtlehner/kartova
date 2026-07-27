using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace DirStat.Core.Scanning;

/// <summary>Identity and allocation facts about a file that the managed API does not expose.</summary>
public readonly record struct FileMetadata(
    uint LinkCount,
    ulong VolumeId,
    ulong FileId,
    long AllocatedSize)
{
    /// <summary>True when the same content is reachable through more than one path.</summary>
    public bool IsHardLinked => LinkCount > 1;
}

/// <summary>
/// Platform metadata queries used by the optional exact-allocation and hard-link features.
/// </summary>
/// <remarks>
/// Unix <c>struct stat</c> layouts vary by kernel, libc and architecture, so this class
/// never trusts a layout on faith. <see cref="IsSupported"/> runs a one-time self-test
/// against a temporary file of known size and refuses the whole native path if the fields
/// do not read back correctly. A wrong guess therefore degrades to "feature unavailable"
/// rather than to silently corrupt sizes.
/// </remarks>
public static class NativeFs
{
    private static readonly Lazy<bool> Supported = new(SelfTest, isThreadSafe: true);

    /// <summary>True when <see cref="TryGetMetadata"/> has been verified to work on this host.</summary>
    public static bool IsSupported => Supported.Value;

    /// <summary>Reads link count, filesystem identity and allocated size for a file.</summary>
    public static bool TryGetMetadata(string path, out FileMetadata metadata)
    {
        metadata = default;
        if (!Supported.Value) return false;
        return TryGetMetadataCore(path, out metadata);
    }

    private static bool TryGetMetadataCore(string path, out FileMetadata metadata)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return TryGetWindows(path, out metadata);
            if (OperatingSystem.IsLinux()) return TryGetLinux(path, out metadata);
            if (OperatingSystem.IsMacOS()) return TryGetMacOs(path, out metadata);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                                       or MarshalDirectiveException or BadImageFormatException)
        {
            // The platform does not offer what we assumed. Callers fall back cleanly.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Ordinary per-file failure: locked, vanished, or not ours to read.
        }

        metadata = default;
        return false;
    }

    /// <summary>
    /// Verifies the native path end to end against a file whose size we control.
    /// Runs once per process.
    /// </summary>
    private static bool SelfTest()
    {
        string? probe = null;
        try
        {
            const int probeSize = 12_345;
            probe = Path.Combine(Path.GetTempPath(), $"dirstat-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, new byte[probeSize]);

            if (!TryGetMetadataCore(probe, out var m)) return false;

            // A freshly created file has exactly one link and a non-zero identity.
            if (m.LinkCount != 1) return false;
            if (m.FileId == 0) return false;

            // Allocation must be plausible: at least the logical size, and not absurd.
            // Some filesystems pre-allocate generously, so only the lower bound is firm.
            if (m.AllocatedSize < probeSize) return false;
            if (m.AllocatedSize > probeSize * 64L + 1_048_576L) return false;

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (probe is not null)
            {
                try { File.Delete(probe); } catch { /* best effort */ }
            }
        }
    }

    // ---------------------------------------------------------------- Windows

    private static bool TryGetWindows(string path, out FileMetadata metadata)
    {
        metadata = default;

        using var handle = Win32.CreateFileW(
            Win32.Prefix(path),
            0, // query metadata only
            Win32.FileShareRead | Win32.FileShareWrite | Win32.FileShareDelete,
            IntPtr.Zero,
            Win32.OpenExisting,
            Win32.FlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid) return false;
        if (!Win32.GetFileInformationByHandle(handle, out var info)) return false;

        var allocated = Win32.GetCompressedFileSizeW(Win32.Prefix(path), out var high);
        long allocatedSize;
        if (allocated == uint.MaxValue && Marshal.GetLastWin32Error() != 0)
        {
            // Fall back to the logical size when the compressed-size query is unavailable.
            allocatedSize = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        }
        else
        {
            allocatedSize = ((long)high << 32) | allocated;
        }

        metadata = new FileMetadata(
            info.NumberOfLinks,
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            allocatedSize);
        return true;
    }

    // ------------------------------------------------------------------ Linux

    private static bool TryGetLinux(string path, out FileMetadata metadata)
    {
        metadata = default;

        // statx has an architecture-independent, stable struct layout — unlike struct stat,
        // whose field offsets differ between x86-64 and aarch64. Available since Linux 4.11.
        Span<byte> buffer = stackalloc byte[256];
        buffer.Clear();

        int rc;
        unsafe
        {
            fixed (byte* p = buffer)
            {
                rc = Libc.statx(Libc.AtFdCwd, path, Libc.AtStatxSyncAsStat, Libc.StatxBasicStats, p);
            }
        }

        if (rc != 0) return false;

        var nlink = BitConverter.ToUInt32(buffer[16..]);
        var ino = BitConverter.ToUInt64(buffer[32..]);
        var blocks = BitConverter.ToUInt64(buffer[48..]);
        var devMajor = BitConverter.ToUInt32(buffer[128..]);
        var devMinor = BitConverter.ToUInt32(buffer[132..]);

        metadata = new FileMetadata(
            nlink,
            ((ulong)devMajor << 32) | devMinor,
            ino,
            (long)blocks * 512); // stx_blocks is always in 512-byte units
        return true;
    }

    // ------------------------------------------------------------------ macOS

    private static bool TryGetMacOs(string path, out FileMetadata metadata)
    {
        metadata = default;

        // Darwin's struct stat is a stable published ABI and identical on x86-64 and arm64.
        Span<byte> buffer = stackalloc byte[256];
        buffer.Clear();

        int rc;
        unsafe
        {
            fixed (byte* p = buffer)
            {
                rc = Libc.stat_darwin(path, p);
            }
        }

        if (rc != 0) return false;

        var dev = BitConverter.ToInt32(buffer[0..]);
        var nlink = BitConverter.ToUInt16(buffer[6..]);
        var ino = BitConverter.ToUInt64(buffer[8..]);
        var blocks = BitConverter.ToInt64(buffer[104..]);

        metadata = new FileMetadata(
            nlink,
            unchecked((ulong)(uint)dev),
            ino,
            blocks * 512);
        return true;
    }

    /// <summary>Allocation unit (cluster) size for the volume containing <paramref name="path"/>.</summary>
    public static long GetClusterSize(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(root) &&
                    Win32.GetDiskFreeSpaceW(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
                {
                    var size = (long)sectorsPerCluster * bytesPerSector;
                    if (size > 0) return size;
                }
            }
            else
            {
                // f_frsize is the meaningful allocation unit on Unix.
                var buf = new byte[512];
                if (Libc.statvfs(path, buf) == 0)
                {
                    var frsize = OperatingSystem.IsMacOS()
                        ? BitConverter.ToUInt32(buf, 0)              // Darwin: f_bsize first
                        : (uint)BitConverter.ToUInt64(buf, 8);       // Linux: f_bsize, then f_frsize
                    if (frsize is > 0 and <= 1 << 20) return frsize;
                }
            }
        }
        catch
        {
            // Fall through to the default below.
        }

        return 4096; // near-universal default on NTFS, APFS and ext4
    }

    // -------------------------------------------------------------- P/Invoke

    private static class Win32
    {
        public const uint FileShareRead = 0x1, FileShareWrite = 0x2, FileShareDelete = 0x4;
        public const uint OpenExisting = 3;
        public const uint FlagBackupSemantics = 0x02000000;

        /// <summary>Applies the extended-length prefix so paths beyond MAX_PATH still open.</summary>
        public static string Prefix(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
            return path.Length >= 240 ? @"\\?\" + path : path;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDiskFreeSpaceW(
            string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector,
            out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);
    }

    private static class Libc
    {
        public const int AtFdCwd = -100;
        public const int AtStatxSyncAsStat = 0x0000;
        public const uint StatxBasicStats = 0x000007ff;

        [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern unsafe int statx(int dirfd, string pathname, int flags, uint mask, byte* buf);

        // arm64 and modern x86-64 Darwin both export the 64-bit-inode variant as plain "stat".
        [DllImport("libc", EntryPoint = "stat", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern unsafe int stat_darwin(string pathname, byte* buf);

        [DllImport("libc", EntryPoint = "statvfs", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern int statvfs(string path, byte[] buf);
    }
}
