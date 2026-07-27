using Kartova.Core.Model;

namespace Kartova.Core.Snapshots;

public enum ChangeKind
{
    Unchanged,
    Grew,
    Shrank,
    Added,
    Removed,
}

/// <summary>One node's fate between two scans.</summary>
public sealed class ChangeNode
{
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }
    public required long OldSize { get; init; }
    public required long NewSize { get; init; }
    public required ChangeKind Kind { get; init; }

    public ChangeNode? Parent { get; set; }
    public List<ChangeNode> Children { get; } = [];

    /// <summary>Positive when the node grew, negative when it shrank.</summary>
    public long Delta => NewSize - OldSize;

    /// <summary>Magnitude of the change, which is what ordering cares about.</summary>
    public long Magnitude => Math.Abs(Delta);

    public string GetFullPath()
    {
        if (Parent is null) return Name;

        var segments = new List<string>(8);
        for (var node = this; node is not null && node.Parent is not null; node = node.Parent)
            segments.Add(node.Name);

        var root = this;
        while (root.Parent is not null) root = root.Parent;

        segments.Reverse();
        return Path.Combine(new[] { root.Name }.Concat(segments).ToArray());
    }

    public IEnumerable<ChangeNode> DescendantsAndSelf()
    {
        var stack = new Stack<ChangeNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }
}

/// <summary>The outcome of comparing two scans of the same location.</summary>
public sealed class ComparisonResult
{
    public required ChangeNode Root { get; init; }
    public required long OldTotal { get; init; }
    public required long NewTotal { get; init; }

    /// <summary>Individual changes, largest magnitude first, with unchanged nodes omitted.</summary>
    public required IReadOnlyList<ChangeNode> Changes { get; init; }

    public long Delta => NewTotal - OldTotal;

    public long AddedBytes => Changes.Where(c => c.Kind == ChangeKind.Added).Sum(c => c.NewSize);
    public long RemovedBytes => Changes.Where(c => c.Kind == ChangeKind.Removed).Sum(c => c.OldSize);
}

/// <summary>
/// Compares two scans of the same location and reports what changed.
/// </summary>
/// <remarks>
/// Matching is by name within each directory, which is the only identity a filesystem
/// actually offers between two points in time — inodes are reused and paths are all a user
/// recognises. A renamed folder therefore reads as one removal and one addition, which is
/// honest: the tool cannot know it was the same thing.
/// </remarks>
public static class TreeComparer
{
    /// <summary>Changes smaller than this are not worth listing individually.</summary>
    public const long DefaultThreshold = 1024 * 1024;

    public static ComparisonResult Compare(FileNode oldRoot, FileNode newRoot, long threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(oldRoot);
        ArgumentNullException.ThrowIfNull(newRoot);

        var root = Build(oldRoot, newRoot, newRoot.Name, parent: null);

        var changes = root.DescendantsAndSelf()
            .Where(c => c.Parent is not null)          // the root's change is the headline total
            .Where(c => c.Kind != ChangeKind.Unchanged)
            .Where(c => c.Magnitude >= threshold)
            .Where(IsWorthListing)
            .OrderByDescending(c => c.Magnitude)
            .ToArray();

        return new ComparisonResult
        {
            Root = root,
            OldTotal = oldRoot.Size,
            NewTotal = newRoot.Size,
            Changes = changes,
        };
    }

    /// <summary>
    /// Decides whether a change deserves its own line, rather than being covered by a
    /// neighbour that says the same thing.
    /// </summary>
    /// <remarks>
    /// The useful node differs by direction of change. For something added or removed, the
    /// outermost node is what a person wants to read — "this whole folder is new" beats a
    /// hundred lines naming each file inside it. For something that grew or shrank, the
    /// innermost is: "this database file grew by 4 GB" beats every folder above it repeating
    /// the same number.
    /// </remarks>
    private static bool IsWorthListing(ChangeNode node)
    {
        if (node.Kind is ChangeKind.Added or ChangeKind.Removed)
        {
            // Covered already if an ancestor was added or removed wholesale.
            for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Kind == node.Kind)
                    return false;

            return true;
        }

        // Grew or shrank: drop it when a single child accounts for the entire delta.
        foreach (var child in node.Children)
            if (child.Delta == node.Delta)
                return false;

        return true;
    }

    private static ChangeNode Build(FileNode? oldNode, FileNode? newNode, string name, ChangeNode? parent)
    {
        var oldSize = oldNode?.Size ?? 0;
        var newSize = newNode?.Size ?? 0;

        var kind =
            oldNode is null ? ChangeKind.Added :
            newNode is null ? ChangeKind.Removed :
            newSize > oldSize ? ChangeKind.Grew :
            newSize < oldSize ? ChangeKind.Shrank :
            ChangeKind.Unchanged;

        var node = new ChangeNode
        {
            Name = name,
            IsDirectory = (newNode ?? oldNode)?.IsDirectory ?? false,
            OldSize = oldSize,
            NewSize = newSize,
            Kind = kind,
            Parent = parent,
        };

        // Only directories have children worth pairing up.
        var oldChildren = oldNode?.Children;
        var newChildren = newNode?.Children;
        if (oldChildren is null && newChildren is null) return node;

        var byName = new Dictionary<string, (FileNode? Old, FileNode? New)>(NameComparer);

        if (oldChildren is not null)
            foreach (var child in oldChildren)
                if (!child.IsSynthetic)
                    byName[child.Name] = (child, null);

        if (newChildren is not null)
        {
            foreach (var child in newChildren)
            {
                if (child.IsSynthetic) continue;
                byName[child.Name] = byName.TryGetValue(child.Name, out var pair)
                    ? (pair.Old, child)
                    : (null, child);
            }
        }

        foreach (var (childName, (oldChild, newChild)) in byName)
            node.Children.Add(Build(oldChild, newChild, childName, node));

        return node;
    }

    private static StringComparer NameComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
