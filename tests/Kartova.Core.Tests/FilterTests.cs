using Kartova.Core.Filtering;
using Kartova.Core.Model;
using Xunit;

namespace Kartova.Core.Tests;

public class FilterTests
{
    /// <summary>Builds a small tree: root/{docs/{a.pdf,b.txt}, media/{clip.mp4}, readme.md}.</summary>
    private static FileNode SampleTree()
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root);

        var docs = new FileNode("docs", NodeFlags.Directory) { Parent = root };
        var pdf = new FileNode("a.pdf") { Parent = docs, Size = 5_000_000, LastWriteUtcTicks = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc).Ticks };
        var txt = new FileNode("b.txt") { Parent = docs, Size = 1_000, LastWriteUtcTicks = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks };
        docs.Children = [pdf, txt];

        var media = new FileNode("media", NodeFlags.Directory) { Parent = root };
        var mp4 = new FileNode("clip.mp4") { Parent = media, Size = 200_000_000, LastWriteUtcTicks = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc).Ticks };
        media.Children = [mp4];

        var readme = new FileNode("readme.md") { Parent = root, Size = 400 };
        root.Children = [media, docs, readme];

        DirectoryScannerAccess.Aggregate(root);
        root.SortBySizeDescending();
        return root;
    }

    private static string[] FileNames(FileNode root) =>
        root.DescendantsAndSelf().Where(n => !n.IsDirectory).Select(n => n.Name).OrderBy(n => n).ToArray();

    [Fact]
    public void Empty_query_returns_the_original_tree_unchanged()
    {
        var root = SampleTree();
        var filtered = NodeFilter.Apply(root, FilterCriteria.Parse(""));
        Assert.Same(root, filtered);
    }

    [Fact]
    public void Extension_pattern_keeps_only_that_type()
    {
        var root = SampleTree();
        var filtered = NodeFilter.Apply(root, FilterCriteria.Parse("*.mp4"));

        Assert.Equal(["clip.mp4"], FileNames(filtered));
        Assert.Equal(200_000_000, filtered.Size);
    }

    [Fact]
    public void Minimum_size_excludes_smaller_files()
    {
        var root = SampleTree();
        var filtered = NodeFilter.Apply(root, FilterCriteria.Parse(">1mb"));

        Assert.Equal(["a.pdf", "clip.mp4"], FileNames(filtered));
    }

    [Fact]
    public void Maximum_size_excludes_larger_files()
    {
        var root = SampleTree();
        var filtered = NodeFilter.Apply(root, FilterCriteria.Parse("<10kb"));

        Assert.Equal(["b.txt", "readme.md"], FileNames(filtered));
    }

    [Fact]
    public void Name_substring_matches_case_insensitively()
    {
        var root = SampleTree();
        var filtered = NodeFilter.Apply(root, FilterCriteria.Parse("README"));

        Assert.Equal(["readme.md"], FileNames(filtered));
    }

    [Fact]
    public void Terms_combine_as_an_intersection()
    {
        var root = SampleTree();

        // Both conditions must hold: a.pdf is large enough but is not an mp4.
        var filtered = NodeFilter.Apply(root, FilterCriteria.Parse("*.mp4 >1mb"));
        Assert.Equal(["clip.mp4"], FileNames(filtered));
    }

    [Fact]
    public void Date_bounds_are_applied()
    {
        var root = SampleTree();

        var after = NodeFilter.Apply(root, FilterCriteria.Parse("after:2025-01-01"));
        Assert.Equal(["a.pdf", "clip.mp4"], FileNames(after));

        var before = NodeFilter.Apply(root, FilterCriteria.Parse("before:2025-01-01"));
        Assert.Equal(["b.txt"], FileNames(before));
    }

    [Fact]
    public void Directories_with_no_surviving_files_are_pruned()
    {
        var root = SampleTree();
        var filtered = NodeFilter.Apply(root, FilterCriteria.Parse("*.mp4"));

        var names = filtered.DescendantsAndSelf().Select(n => n.Name).ToArray();
        Assert.Contains("media", names);
        Assert.DoesNotContain("docs", names);
    }

    [Fact]
    public void Sizes_are_recomputed_from_what_survived()
    {
        var root = SampleTree();
        var filtered = NodeFilter.Apply(root, FilterCriteria.Parse("<10kb"));

        Assert.Equal(1_400, filtered.Size);
        Assert.Equal(2, filtered.FileCount);
    }

    [Fact]
    public void Filtering_never_mutates_the_original_tree()
    {
        var root = SampleTree();
        var originalSize = root.Size;
        var originalCount = root.FileCount;

        NodeFilter.Apply(root, FilterCriteria.Parse("*.mp4"));

        Assert.Equal(originalSize, root.Size);
        Assert.Equal(originalCount, root.FileCount);
        Assert.Equal(4, root.DescendantsAndSelf().Count(n => !n.IsDirectory));
    }

    [Fact]
    public void No_matches_yields_an_empty_but_usable_tree()
    {
        var root = SampleTree();
        var filtered = NodeFilter.Apply(root, FilterCriteria.Parse("*.nothinghere"));

        Assert.NotNull(filtered);
        Assert.Equal(0, filtered.Size);
        Assert.Empty(filtered.Children!);
    }

    [Theory]
    [InlineData(">500", 500L)]
    [InlineData(">10k", 10240L)]
    [InlineData(">10kb", 10240L)]
    [InlineData(">1.5m", 1572864L)]
    [InlineData(">2G", 2147483648L)]
    public void Size_suffixes_are_parsed(string query, long expected)
    {
        var criteria = FilterCriteria.Parse(query);
        Assert.Equal(expected, criteria.MinSize);
    }

    [Fact]
    public void Unparseable_tokens_fall_through_to_a_name_match()
    {
        var criteria = FilterCriteria.Parse("holiday photos");
        Assert.Equal("holiday photos", criteria.NamePattern);
        Assert.Null(criteria.MinSize);
    }

    [Fact]
    public void Wildcards_in_a_name_anchor_the_whole_name()
    {
        var root = SampleTree();

        // "a*" must match a.pdf but not readme.md, which merely contains an 'a'.
        var filtered = NodeFilter.Apply(root, FilterCriteria.Parse("a*"));
        Assert.Equal(["a.pdf"], FileNames(filtered));
    }
}

/// <summary>Reaches the internal aggregation pass so tests can build trees by hand.</summary>
internal static class DirectoryScannerAccess
{
    public static void Aggregate(FileNode root) => Scanning.DirectoryScanner.Aggregate(root);
}
