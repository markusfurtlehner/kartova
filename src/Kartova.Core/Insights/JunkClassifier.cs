using Kartova.Core.Model;

namespace Kartova.Core.Insights;

/// <summary>How safe a category is to remove, which decides how loudly the UI warns.</summary>
public enum JunkConfidence
{
    /// <summary>Rebuilt automatically on next use. Removing it costs time, never data.</summary>
    Rebuildable,

    /// <summary>Almost certainly disposable, but check before removing.</summary>
    Likely,

    /// <summary>Often large and often unwanted, but may be deliberate. Read carefully.</summary>
    Review,
}

/// <summary>A recognised category of reclaimable space.</summary>
public sealed class JunkCategory
{
    public required string Id { get; init; }
    public required JunkConfidence Confidence { get; init; }

    /// <summary>Directory names that identify this category, matched case-insensitively.</summary>
    public required IReadOnlyList<string> DirectoryNames { get; init; }

    /// <summary>File extensions that identify this category, including the leading dot.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];
}

/// <summary>One recognised item on disk.</summary>
public sealed class JunkFinding
{
    public required FileNode Node { get; init; }
    public required JunkCategory Category { get; init; }
    public long Size => Node.Size;
}

/// <summary>
/// Recognises directories and files that are caches, build output or other rebuildable data.
/// </summary>
/// <remarks>
/// <para>
/// This classifies and explains; it never decides. Every category carries a confidence level
/// so the interface can say plainly what a thing is and how safe removing it is, and the
/// judgement stays with the person who knows what they are working on.
/// </para>
/// <para>
/// The list is deliberately conservative. Nothing here is a document, a download, or anything
/// a user created by hand — the worst outcome of acting on it should be waiting for a
/// rebuild, not losing work.
/// </para>
/// </remarks>
public static class JunkClassifier
{
    public static IReadOnlyList<JunkCategory> Categories { get; } =
    [
        new()
        {
            Id = "Junk.NodeModules",
            Confidence = JunkConfidence.Rebuildable,
            DirectoryNames = ["node_modules"],
        },
        new()
        {
            Id = "Junk.BuildOutput",
            Confidence = JunkConfidence.Rebuildable,
            DirectoryNames = ["bin", "obj", "target", "build", "dist", "out", ".next", ".nuxt", ".parcel-cache"],
        },
        new()
        {
            Id = "Junk.PackageCache",
            Confidence = JunkConfidence.Rebuildable,
            DirectoryNames = [".nuget", ".m2", ".gradle", ".ivy2", ".cargo", ".rustup", ".pub-cache", ".cocoapods"],
        },
        new()
        {
            Id = "Junk.PythonCache",
            Confidence = JunkConfidence.Rebuildable,
            DirectoryNames = ["__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox"],
            Extensions = [".pyc", ".pyo"],
        },
        new()
        {
            Id = "Junk.ToolCache",
            Confidence = JunkConfidence.Rebuildable,
            DirectoryNames = [".cache", "cache", "caches", ".turbo", ".vite", ".webpack", ".eslintcache"],
        },
        new()
        {
            Id = "Junk.Temp",
            Confidence = JunkConfidence.Likely,
            DirectoryNames = ["temp", "tmp", ".temp"],
            Extensions = [".tmp", ".temp", ".~tmp"],
        },
        new()
        {
            Id = "Junk.Logs",
            Confidence = JunkConfidence.Likely,
            DirectoryNames = ["logs"],
            Extensions = [".log", ".log1", ".log2", ".etl", ".dmp"],
        },
        new()
        {
            Id = "Junk.Backups",
            Confidence = JunkConfidence.Review,
            DirectoryNames = [],
            Extensions = [".bak", ".old", ".orig", ".backup", "~"],
        },
        new()
        {
            Id = "Junk.Installers",
            Confidence = JunkConfidence.Review,
            DirectoryNames = [],
            Extensions = [".msi", ".dmg", ".iso", ".appimage"],
        },
    ];

    private static readonly Dictionary<string, JunkCategory> ByDirectoryName = BuildDirectoryIndex();
    private static readonly Dictionary<string, JunkCategory> ByExtension = BuildExtensionIndex();

    private static Dictionary<string, JunkCategory> BuildDirectoryIndex()
    {
        var map = new Dictionary<string, JunkCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in Categories)
            foreach (var name in category.DirectoryNames)
                map.TryAdd(name, category);
        return map;
    }

    private static Dictionary<string, JunkCategory> BuildExtensionIndex()
    {
        var map = new Dictionary<string, JunkCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in Categories)
            foreach (var extension in category.Extensions)
                map.TryAdd(extension, category);
        return map;
    }

    /// <summary>
    /// Finds recognised junk under a tree.
    /// </summary>
    /// <remarks>
    /// A matched directory is reported whole and not descended into: a <c>node_modules</c>
    /// holds thousands of files that are one decision, not thousands. Files are only matched
    /// outside such a directory, for the same reason.
    /// </remarks>
    public static IReadOnlyList<JunkFinding> Find(FileNode root, long minimumSize = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(root);

        var findings = new List<JunkFinding>();
        var stack = new Stack<FileNode>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.IsSynthetic) continue;

            if (node.IsDirectory)
            {
                if (!node.IsRoot && ByDirectoryName.TryGetValue(node.Name, out var directoryCategory))
                {
                    if (node.Size >= minimumSize)
                        findings.Add(new JunkFinding { Node = node, Category = directoryCategory });

                    // Reported whole; nothing inside it is a separate decision.
                    continue;
                }

                var children = node.Children;
                if (children is null) continue;
                foreach (var child in children) stack.Push(child);
                continue;
            }

            if (node.Size < minimumSize) continue;

            var extension = node.Extension;
            if (extension.Length > 0 && ByExtension.TryGetValue(extension, out var fileCategory))
                findings.Add(new JunkFinding { Node = node, Category = fileCategory });
        }

        findings.Sort((a, b) => b.Size.CompareTo(a.Size));
        return findings;
    }
}
