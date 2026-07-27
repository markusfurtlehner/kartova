using Kartova.Core.Model;

namespace Kartova.Core.Insights;

/// <summary>The kinds of finding the insights pass produces.</summary>
public enum InsightKind
{
    StaleFile,
    EmptyFolder,
    ZeroByteFile,
    Junk,
}

/// <summary>One thing worth a person's attention.</summary>
public sealed class Insight
{
    public required FileNode Node { get; init; }
    public required InsightKind Kind { get; init; }

    /// <summary>Set only for junk findings, naming what the item is.</summary>
    public JunkCategory? Category { get; init; }

    public long Size => Node.Size;
}

/// <summary>A named group of findings, with its total.</summary>
public sealed class InsightGroup
{
    public required InsightKind Kind { get; init; }
    public required IReadOnlyList<Insight> Items { get; init; }

    /// <summary>Set for junk groups, which are split by category rather than pooled.</summary>
    public JunkCategory? Category { get; init; }

    public long TotalBytes => Items.Sum(i => i.Size);
    public int Count => Items.Count;
}

public sealed class InsightOptions
{
    /// <summary>A file untouched for at least this long counts as stale.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromDays(365 * 2);

    /// <summary>Stale files below this size are not worth listing.</summary>
    public long MinimumStaleSize { get; set; } = 10L * 1024 * 1024;

    /// <summary>Junk below this size is not worth listing.</summary>
    public long MinimumJunkSize { get; set; } = 1024 * 1024;

    public bool FindStale { get; set; } = true;
    public bool FindEmptyFolders { get; set; } = true;
    public bool FindZeroByteFiles { get; set; } = true;
    public bool FindJunk { get; set; } = true;

    /// <summary>Cap per group, so one pathological folder cannot produce a hundred thousand rows.</summary>
    public int MaxPerGroup { get; set; } = 500;
}

public sealed class InsightResult
{
    public required IReadOnlyList<InsightGroup> Groups { get; init; }
    public required DateTime GeneratedUtc { get; init; }

    public long TotalBytes => Groups.Sum(g => g.TotalBytes);

    public static InsightResult Empty { get; } = new()
    {
        Groups = [],
        GeneratedUtc = DateTime.UtcNow,
    };
}

/// <summary>
/// Walks a scanned tree once and reports everything worth a second look.
/// </summary>
/// <remarks>
/// All of this comes from data the scan already collected — sizes, timestamps, names — so the
/// pass costs a single traversal and touches no files. Nothing here deletes anything; the
/// analyser only points.
/// </remarks>
public static class InsightAnalyzer
{
    public static InsightResult Analyze(FileNode root, InsightOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new InsightOptions();

        var staleBefore = DateTime.UtcNow - options.StaleAfter;

        var stale = new List<Insight>();
        var empty = new List<Insight>();
        var zeroByte = new List<Insight>();

        foreach (var node in root.DescendantsAndSelf())
        {
            if (node.IsSynthetic || node.IsRoot) continue;

            if (node.IsDirectory)
            {
                // Empty means nothing underneath at all, not merely no direct children.
                if (options.FindEmptyFolders && node.FileCount == 0 && node.DirCount == 0)
                    empty.Add(new Insight { Node = node, Kind = InsightKind.EmptyFolder });

                continue;
            }

            if (options.FindZeroByteFiles && node.Size == 0)
            {
                zeroByte.Add(new Insight { Node = node, Kind = InsightKind.ZeroByteFile });
                continue;
            }

            if (options.FindStale &&
                node.Size >= options.MinimumStaleSize &&
                node.LastWriteUtcTicks != 0 &&
                node.LastWriteUtc < staleBefore)
            {
                stale.Add(new Insight { Node = node, Kind = InsightKind.StaleFile });
            }
        }

        var groups = new List<InsightGroup>();

        if (stale.Count > 0)
        {
            stale.Sort((a, b) => b.Size.CompareTo(a.Size));
            groups.Add(new InsightGroup { Kind = InsightKind.StaleFile, Items = Cap(stale, options.MaxPerGroup) });
        }

        if (options.FindJunk)
        {
            // Junk is grouped by category, because "what is this?" and "is it safe to remove?"
            // have different answers for a package cache and a stray installer.
            foreach (var byCategory in JunkClassifier.Find(root, options.MinimumJunkSize)
                         .GroupBy(f => f.Category)
                         .OrderByDescending(g => g.Sum(f => f.Size)))
            {
                var items = byCategory
                    .Select(f => new Insight { Node = f.Node, Kind = InsightKind.Junk, Category = f.Category })
                    .ToList();

                groups.Add(new InsightGroup
                {
                    Kind = InsightKind.Junk,
                    Category = byCategory.Key,
                    Items = Cap(items, options.MaxPerGroup),
                });
            }
        }

        if (zeroByte.Count > 0)
        {
            groups.Add(new InsightGroup
            {
                Kind = InsightKind.ZeroByteFile,
                Items = Cap(zeroByte, options.MaxPerGroup),
            });
        }

        if (empty.Count > 0)
        {
            groups.Add(new InsightGroup
            {
                Kind = InsightKind.EmptyFolder,
                Items = Cap(empty, options.MaxPerGroup),
            });
        }

        return new InsightResult { Groups = groups, GeneratedUtc = DateTime.UtcNow };
    }

    private static IReadOnlyList<Insight> Cap(List<Insight> items, int limit) =>
        items.Count <= limit ? items : items.Take(limit).ToArray();
}
