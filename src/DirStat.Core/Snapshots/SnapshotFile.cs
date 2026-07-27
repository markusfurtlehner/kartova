using System.Text;
using DirStat.Core.Model;

namespace DirStat.Core.Snapshots;

/// <summary>Metadata about a stored scan, readable without loading the whole tree.</summary>
public sealed class SnapshotInfo
{
    public required string FilePath { get; init; }
    public required string RootPath { get; init; }
    public required DateTime TakenUtc { get; init; }
    public required long TotalBytes { get; init; }
    public required long TotalFiles { get; init; }
    public required long TotalDirectories { get; init; }

    public string DisplayName => $"{RootPath} — {TakenUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
}

/// <summary>A stored scan: its metadata and the tree it captured.</summary>
public sealed class Snapshot
{
    public required SnapshotInfo Info { get; init; }
    public required FileNode Root { get; init; }
}

/// <summary>
/// Reads and writes scan snapshots.
/// </summary>
/// <remarks>
/// <para>
/// A purpose-built binary format rather than the JSON exporter. A full-volume scan is well
/// over a million nodes; JSON would produce a file several times larger and take far longer
/// to parse, and a snapshot is written for the program to read back, not for a person.
/// </para>
/// <para>
/// The header carries the totals, so listing available snapshots costs one short read each
/// rather than loading every tree.
/// </para>
/// </remarks>
public static class SnapshotFile
{
    private static readonly byte[] Magic = "DSSNAP\0"u8.ToArray();
    private const int Version = 1;

    public const string Extension = ".dirstat";

    public static void Save(FileNode root, string path, DateTime? takenUtc = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((takenUtc ?? DateTime.UtcNow).Ticks);
        writer.Write(root.Name);
        writer.Write(root.Size);
        writer.Write((long)root.FileCount);
        writer.Write((long)root.DirCount);

        WriteNode(writer, root);
    }

    /// <summary>Reads only the header. Cheap enough to call for every file in a folder.</summary>
    public static SnapshotInfo? ReadInfo(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            if (!HasMagic(reader)) return null;
            if (reader.ReadInt32() != Version) return null;

            var taken = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            var rootPath = reader.ReadString();
            var totalBytes = reader.ReadInt64();
            var files = reader.ReadInt64();
            var directories = reader.ReadInt64();

            return new SnapshotInfo
            {
                FilePath = path,
                RootPath = rootPath,
                TakenUtc = taken,
                TotalBytes = totalBytes,
                TotalFiles = files,
                TotalDirectories = directories,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    public static Snapshot? Load(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            if (!HasMagic(reader)) return null;
            if (reader.ReadInt32() != Version) return null;

            var info = new SnapshotInfo
            {
                FilePath = path,
                TakenUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
                RootPath = reader.ReadString(),
                TotalBytes = reader.ReadInt64(),
                TotalFiles = reader.ReadInt64(),
                TotalDirectories = reader.ReadInt64(),
            };

            var root = ReadNode(reader, parent: null);
            return new Snapshot { Info = info, Root = root };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or EndOfStreamException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Lists snapshots in a folder, newest first, skipping anything unreadable.</summary>
    public static IReadOnlyList<SnapshotInfo> List(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];

            return Directory.EnumerateFiles(directory, "*" + Extension)
                .Select(ReadInfo)
                .Where(i => i is not null)
                .Select(i => i!)
                .OrderByDescending(i => i.TakenUtc)
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool HasMagic(BinaryReader reader)
    {
        var magic = reader.ReadBytes(Magic.Length);
        return magic.Length == Magic.Length && magic.AsSpan().SequenceEqual(Magic);
    }

    /// <summary>
    /// Writes a node and its subtree, depth first.
    /// </summary>
    /// <remarks>
    /// Iterative rather than recursive: a snapshot of a deep tree must not be limited by the
    /// call stack, and real trees routinely nest further than is comfortable.
    /// </remarks>
    private static void WriteNode(BinaryWriter writer, FileNode root)
    {
        var stack = new Stack<FileNode>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            writer.Write(node.Name);
            writer.Write((ushort)node.Flags);
            writer.Write(node.Size);
            writer.Write(node.SizeOnDisk);
            writer.Write(node.LastWriteUtcTicks);
            writer.Write(node.FileCount);
            writer.Write(node.DirCount);

            var children = node.Children;
            var count = children?.Length ?? 0;
            writer.Write(count);

            // Pushed in reverse so they are read back in their original order.
            for (var i = count - 1; i >= 0; i--) stack.Push(children![i]);
        }
    }

    private static FileNode ReadNode(BinaryReader reader, FileNode? parent)
    {
        // Mirrors the writer's traversal: each frame owns a node and how many children remain.
        var root = ReadOne(reader, parent, out var remaining);
        if (remaining == 0) return root;

        var stack = new Stack<(FileNode Node, FileNode[] Children, int Index)>();
        stack.Push((root, new FileNode[remaining], 0));

        while (stack.Count > 0)
        {
            var (node, children, index) = stack.Pop();

            if (index == children.Length)
            {
                node.Children = children;
                continue;
            }

            var child = ReadOne(reader, node, out var childCount);
            children[index] = child;
            stack.Push((node, children, index + 1));

            if (childCount > 0) stack.Push((child, new FileNode[childCount], 0));
            else child.Children = child.IsDirectory ? [] : null;
        }

        return root;
    }

    private static FileNode ReadOne(BinaryReader reader, FileNode? parent, out int childCount)
    {
        var name = reader.ReadString();
        var flags = (NodeFlags)reader.ReadUInt16();

        var node = new FileNode(name, flags)
        {
            Parent = parent,
            Size = reader.ReadInt64(),
            SizeOnDisk = reader.ReadInt64(),
            LastWriteUtcTicks = reader.ReadInt64(),
            FileCount = reader.ReadInt32(),
            DirCount = reader.ReadInt32(),
        };

        childCount = reader.ReadInt32();
        return node;
    }
}
