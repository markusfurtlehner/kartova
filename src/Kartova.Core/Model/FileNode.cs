using System.Runtime.CompilerServices;
using System.Text;

namespace Kartova.Core.Model;

/// <summary>
/// One file or directory in a scanned tree.
/// </summary>
/// <remarks>
/// This type is deliberately lean. A scan of a system volume routinely produces
/// more than a million instances, so the node stores only its own path segment and
/// reconstructs full paths by walking <see cref="Parent"/>. Storing full paths
/// instead would cost hundreds of megabytes on a large scan.
/// </remarks>
public sealed class FileNode
{
    /// <summary>
    /// Path segment only — except on a root node, where it holds the full root path.
    /// </summary>
    public string Name;

    public FileNode? Parent;

    /// <summary>Child nodes, or <c>null</c> for files and empty directories.</summary>
    public FileNode[]? Children;

    /// <summary>Logical size in bytes. For a directory this is the subtree total.</summary>
    public long Size;

    /// <summary>Allocated size in bytes, rounded to the volume cluster size.</summary>
    public long SizeOnDisk;

    public long LastWriteUtcTicks;

    /// <summary>Files in this subtree. Zero for a file node.</summary>
    public int FileCount;

    /// <summary>Directories in this subtree, excluding this node.</summary>
    public int DirCount;

    public NodeFlags Flags;

    public FileNode(string name, NodeFlags flags = NodeFlags.None)
    {
        Name = name;
        Flags = flags;
    }

    public bool IsDirectory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Flags & NodeFlags.Directory) != 0;
    }

    public bool IsRoot
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Flags & NodeFlags.Root) != 0;
    }

    public bool IsSynthetic
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Flags & (NodeFlags.FreeSpace | NodeFlags.Unknown)) != 0;
    }

    public bool HasFlag(NodeFlags flag)
    {
        return (Flags & flag) != 0;
    }

    public DateTime LastWriteUtc =>
        LastWriteUtcTicks == 0 ? DateTime.MinValue : new DateTime(LastWriteUtcTicks, DateTimeKind.Utc);

    /// <summary>Depth below the scan root; a root itself is depth 0.</summary>
    public int Depth
    {
        get
        {
            var d = 0;
            for (var n = Parent; n is not null; n = n.Parent) d++;
            return d;
        }
    }

    public FileNode Root
    {
        get
        {
            var n = this;
            while (n.Parent is not null) n = n.Parent;
            return n;
        }
    }

    /// <summary>
    /// Extension including the leading dot, or an empty span. Directories and dotfiles such
    /// as <c>.gitignore</c> report none.
    /// </summary>
    /// <remarks>
    /// Returns a span into <see cref="Name"/> rather than a new string. This is read once per
    /// tile on every treemap render and once per file on every filter pass, so materialising
    /// a string here would churn megabytes of garbage for no benefit.
    /// </remarks>
    public ReadOnlySpan<char> ExtensionSpan
    {
        get
        {
            if (IsDirectory || IsSynthetic) return default;
            var dot = Name.LastIndexOf('.');
            // A leading dot means a hidden file, not an extension.
            if (dot <= 0 || dot == Name.Length - 1) return default;
            return Name.AsSpan(dot);
        }
    }

    /// <summary>
    /// Extension as an interned, lowercased string. Prefer <see cref="ExtensionSpan"/> on hot
    /// paths; use this only where a string is genuinely needed, such as a dictionary key.
    /// </summary>
    public string Extension => ExtensionPool.Intern(ExtensionSpan);

    /// <summary>Rebuilds the absolute path by walking up to the root.</summary>
    public string GetFullPath()
    {
        if (IsRoot) return Name;
        if (IsSynthetic) return Parent?.GetFullPath() ?? Name;

        // Collect segments up to the root, then emit them in order.
        var stack = new List<string>(8);
        var node = this;
        while (node is not null && !node.IsRoot)
        {
            stack.Add(node.Name);
            node = node.Parent;
        }

        var sb = new StringBuilder(64);
        sb.Append(node?.Name ?? string.Empty);

        for (var i = stack.Count - 1; i >= 0; i--)
        {
            if (sb.Length > 0 && sb[^1] != Path.DirectorySeparatorChar && sb[^1] != Path.AltDirectorySeparatorChar)
                sb.Append(Path.DirectorySeparatorChar);
            sb.Append(stack[i]);
        }

        return sb.ToString();
    }

    /// <summary>Sorts children by descending size, recursively. Required before treemap layout.</summary>
    public void SortBySizeDescending()
    {
        // Iterative to survive pathologically deep trees.
        var stack = new Stack<FileNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            var kids = node.Children;
            if (kids is null) continue;
            Array.Sort(kids, static (a, b) => b.Size.CompareTo(a.Size));
            foreach (var k in kids)
                if (k.Children is not null) stack.Push(k);
        }
    }

    /// <summary>Enumerates this node and every descendant, depth first.</summary>
    public IEnumerable<FileNode> DescendantsAndSelf()
    {
        var stack = new Stack<FileNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            var kids = node.Children;
            if (kids is null) continue;
            for (var i = kids.Length - 1; i >= 0; i--) stack.Push(kids[i]);
        }
    }

    /// <summary>This node's share of its parent, in the range 0..1.</summary>
    public double FractionOfParent
    {
        get
        {
            var parentSize = Parent?.Size ?? 0;
            return parentSize <= 0 ? 0 : (double)Size / parentSize;
        }
    }

    public override string ToString() => $"{Name} ({Size:N0} B)";
}

/// <summary>
/// Interns extension strings. A large scan contains millions of files sharing a few
/// thousand distinct extensions, so pooling turns millions of allocations into thousands.
/// </summary>
internal static class ExtensionPool
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Pool =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Alternate lookup keyed by span, so an already-pooled extension is found without
    /// allocating a string first. Only a genuinely new extension allocates.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>
        .AlternateLookup<ReadOnlySpan<char>> SpanLookup = Pool.GetAlternateLookup<ReadOnlySpan<char>>();

    public static string Intern(ReadOnlySpan<char> ext)
    {
        if (ext.IsEmpty) return string.Empty;
        if (SpanLookup.TryGetValue(ext, out var existing)) return existing;

        var lowered = ext.ToString().ToLowerInvariant();
        return Pool.GetOrAdd(lowered, lowered);
    }
}
