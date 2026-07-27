using System.Text.RegularExpressions;
using DirStat.Core.Model;

namespace DirStat.Core.Filtering;

/// <summary>Criteria for narrowing a scanned tree, in the spirit of SpaceSniffer's filters.</summary>
public sealed class FilterCriteria
{
    /// <summary>Name pattern. Plain text matches as a substring; <c>*</c> and <c>?</c> act as wildcards.</summary>
    public string? NamePattern { get; set; }

    /// <summary>Only files at least this large.</summary>
    public long? MinSize { get; set; }

    /// <summary>Only files no larger than this.</summary>
    public long? MaxSize { get; set; }

    /// <summary>Only files modified on or after this instant.</summary>
    public DateTime? ModifiedAfterUtc { get; set; }

    /// <summary>Only files modified on or before this instant.</summary>
    public DateTime? ModifiedBeforeUtc { get; set; }

    /// <summary>Only these extensions, each including the leading dot. Empty means any.</summary>
    public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(NamePattern) &&
        MinSize is null && MaxSize is null &&
        ModifiedAfterUtc is null && ModifiedBeforeUtc is null &&
        Extensions.Count == 0;

    /// <summary>Parses a query such as <c>report *.pdf &gt;10mb after:2025-01-01</c>.</summary>
    /// <remarks>
    /// A single text box is far quicker to drive than a panel of controls, so the box accepts
    /// a small query language and everything unrecognised falls through to a name match.
    /// </remarks>
    public static FilterCriteria Parse(string? query)
    {
        var criteria = new FilterCriteria();
        if (string.IsNullOrWhiteSpace(query)) return criteria;

        var names = new List<string>();

        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith(">=", StringComparison.Ordinal) && TryParseSize(token[2..], out var gte))
                criteria.MinSize = gte;
            else if (token.StartsWith("<=", StringComparison.Ordinal) && TryParseSize(token[2..], out var lte))
                criteria.MaxSize = lte;
            else if (token.StartsWith('>') && TryParseSize(token[1..], out var gt))
                criteria.MinSize = gt;
            else if (token.StartsWith('<') && TryParseSize(token[1..], out var lt))
                criteria.MaxSize = lt;
            else if (token.StartsWith("after:", StringComparison.OrdinalIgnoreCase) &&
                     DateTime.TryParse(token[6..], out var after))
                criteria.ModifiedAfterUtc = after.ToUniversalTime();
            else if (token.StartsWith("before:", StringComparison.OrdinalIgnoreCase) &&
                     DateTime.TryParse(token[7..], out var before))
                criteria.ModifiedBeforeUtc = before.ToUniversalTime();
            else if (token.StartsWith("*.", StringComparison.Ordinal) && token.Length > 2 && !token.Contains('?'))
                criteria.Extensions.Add(token[1..]);
            else
                names.Add(token);
        }

        if (names.Count > 0) criteria.NamePattern = string.Join(' ', names);
        return criteria;
    }

    /// <summary>Parses sizes written as <c>500</c>, <c>10k</c>, <c>1.5mb</c>, <c>2G</c>.</summary>
    private static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim().TrimEnd('b', 'B');
        if (text.Length == 0) return false;

        var multiplier = 1L;
        var suffix = char.ToLowerInvariant(text[^1]);
        if (!char.IsDigit(suffix))
        {
            multiplier = suffix switch
            {
                'k' => 1024L,
                'm' => 1024L * 1024,
                'g' => 1024L * 1024 * 1024,
                't' => 1024L * 1024 * 1024 * 1024,
                _ => 0,
            };
            if (multiplier == 0) return false;
            text = text[..^1];
        }

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return false;

        bytes = (long)(value * multiplier);
        return true;
    }
}

/// <summary>Produces a filtered copy of a scanned tree.</summary>
public static class NodeFilter
{
    /// <summary>
    /// Returns a new tree holding only the files that match, plus the directories needed to
    /// reach them, with sizes and counts recomputed from what survived.
    /// </summary>
    /// <remarks>
    /// The original tree is never mutated. A filter is a view, and the user expects clearing
    /// the box to restore the full scan instantly rather than trigger a rescan.
    /// </remarks>
    public static FileNode Apply(FileNode root, FilterCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(criteria);

        if (criteria.IsEmpty) return root;

        var matcher = BuildNameMatcher(criteria.NamePattern);
        var copy = CopyMatching(root, null, criteria, matcher);

        // Nothing matched anywhere: hand back an empty root rather than null, so every
        // consumer can keep treating the result as a tree.
        return copy ?? new FileNode(root.Name, root.Flags) { Children = [] };
    }

    private static FileNode? CopyMatching(
        FileNode node, FileNode? parent, FilterCriteria criteria, Func<string, bool>? matcher)
    {
        if (!node.IsDirectory)
        {
            if (node.IsSynthetic) return null; // free/unknown space is not a file match
            return Matches(node, criteria, matcher)
                ? new FileNode(node.Name, node.Flags)
                {
                    Parent = parent,
                    Size = node.Size,
                    SizeOnDisk = node.SizeOnDisk,
                    LastWriteUtcTicks = node.LastWriteUtcTicks,
                }
                : null;
        }

        var clone = new FileNode(node.Name, node.Flags)
        {
            Parent = parent,
            LastWriteUtcTicks = node.LastWriteUtcTicks,
        };

        List<FileNode>? kept = null;
        var children = node.Children;

        if (children is not null)
        {
            foreach (var child in children)
            {
                var copy = CopyMatching(child, clone, criteria, matcher);
                if (copy is null) continue;

                (kept ??= new List<FileNode>(4)).Add(copy);
                clone.Size += copy.Size;
                clone.SizeOnDisk += copy.SizeOnDisk;

                if (copy.IsDirectory)
                {
                    clone.FileCount += copy.FileCount;
                    clone.DirCount += copy.DirCount + 1;
                }
                else
                {
                    clone.FileCount++;
                }
            }
        }

        // Prune directories that contributed nothing, so the map shows only live branches.
        if (kept is null) return null;

        clone.Children = kept.ToArray();
        return clone;
    }

    private static bool Matches(FileNode node, FilterCriteria criteria, Func<string, bool>? matcher)
    {
        if (matcher is not null && !matcher(node.Name)) return false;
        if (criteria.MinSize is { } min && node.Size < min) return false;
        if (criteria.MaxSize is { } max && node.Size > max) return false;

        if (criteria.Extensions.Count > 0 && !criteria.Extensions.Contains(node.Extension)) return false;

        if (criteria.ModifiedAfterUtc is { } after &&
            (node.LastWriteUtcTicks == 0 || node.LastWriteUtc < after)) return false;

        if (criteria.ModifiedBeforeUtc is { } before &&
            (node.LastWriteUtcTicks == 0 || node.LastWriteUtc > before)) return false;

        return true;
    }

    /// <summary>
    /// Builds a name predicate. Patterns containing <c>*</c> or <c>?</c> become anchored
    /// regexes; anything else is a case-insensitive substring test.
    /// </summary>
    private static Func<string, bool>? BuildNameMatcher(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;

        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return name => name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

        var regex = new Regex(
            "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return name => regex.IsMatch(name);
    }
}
