namespace Kartova.Core.Model;

/// <summary>
/// Per-node bit flags. Packed into a <see cref="ushort"/> because a full-volume
/// scan can hold well over a million <see cref="FileNode"/> instances and every
/// byte of the node is multiplied by that count.
/// </summary>
[Flags]
public enum NodeFlags : ushort
{
    None = 0,

    /// <summary>Node is a directory and owns a <see cref="FileNode.Children"/> array.</summary>
    Directory = 1 << 0,

    /// <summary>Symlink, junction or mount point. Never descended into.</summary>
    ReparsePoint = 1 << 1,

    Hidden = 1 << 2,
    System = 1 << 3,
    ReadOnly = 1 << 4,

    /// <summary>The directory could not be opened; its contents are missing from the tree.</summary>
    AccessDenied = 1 << 5,

    /// <summary>
    /// A second or later hard link to content already counted elsewhere in the scan.
    /// Contributes zero bytes so the total stays truthful.
    /// </summary>
    HardLinkDuplicate = 1 << 6,

    /// <summary>Synthetic node representing unallocated space on the volume.</summary>
    FreeSpace = 1 << 7,

    /// <summary>
    /// Synthetic node covering the gap between what the volume reports as used and
    /// what the scan could actually see (denied directories, snapshots, metadata).
    /// </summary>
    Unknown = 1 << 8,

    /// <summary>A scan root. Its <see cref="FileNode.Name"/> holds a full path.</summary>
    Root = 1 << 9,

    Compressed = 1 << 10,
    Sparse = 1 << 11,
    Encrypted = 1 << 12,

    /// <summary>Node lies on a different filesystem than its parent.</summary>
    MountPoint = 1 << 13,
}
