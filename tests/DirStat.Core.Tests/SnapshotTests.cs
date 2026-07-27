using DirStat.Core.Model;
using DirStat.Core.Scanning;
using DirStat.Core.Snapshots;
using Xunit;

namespace DirStat.Core.Tests;

public class SnapshotTests
{
    private static FileNode Tree(params (string Path, long Size)[] files)
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root);

        foreach (var (path, size) in files)
        {
            var segments = path.Split('/');
            var current = root;

            for (var i = 0; i < segments.Length - 1; i++)
            {
                var name = segments[i];
                var existing = current.Children?.FirstOrDefault(c => c.Name == name);
                if (existing is null)
                {
                    existing = new FileNode(name, NodeFlags.Directory) { Parent = current };
                    current.Children = (current.Children ?? []).Append(existing).ToArray();
                }
                current = existing;
            }

            var file = new FileNode(segments[^1]) { Parent = current, Size = size };
            current.Children = (current.Children ?? []).Append(file).ToArray();
        }

        DirectoryScanner.Aggregate(root);
        return root;
    }

    // ------------------------------------------------------------------ format

    [Fact]
    public void A_saved_tree_reloads_identically()
    {
        var root = Tree(("a.bin", 1000), ("docs/b.bin", 2000), ("docs/deep/c.bin", 3000));
        var path = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}{SnapshotFile.Extension}");

        try
        {
            SnapshotFile.Save(root, path);
            var loaded = SnapshotFile.Load(path);

            Assert.NotNull(loaded);
            Assert.Equal(root.Size, loaded!.Root.Size);
            Assert.Equal(root.FileCount, loaded.Root.FileCount);
            Assert.Equal(root.DirCount, loaded.Root.DirCount);

            var original = root.DescendantsAndSelf().Select(n => (n.Name, n.Size)).OrderBy(x => x.Name).ToArray();
            var restored = loaded.Root.DescendantsAndSelf().Select(n => (n.Name, n.Size)).OrderBy(x => x.Name).ToArray();
            Assert.Equal(original, restored);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parent_links_survive_the_round_trip()
    {
        var root = Tree(("docs/deep/c.bin", 3000));
        var path = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}{SnapshotFile.Extension}");

        try
        {
            SnapshotFile.Save(root, path);
            var loaded = SnapshotFile.Load(path)!;

            var leaf = loaded.Root.DescendantsAndSelf().Single(n => n.Name == "c.bin");
            Assert.Equal("deep", leaf.Parent!.Name);
            Assert.Equal("docs", leaf.Parent.Parent!.Name);
            Assert.Same(loaded.Root, leaf.Parent.Parent.Parent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_header_can_be_read_without_loading_the_tree()
    {
        var root = Tree(("a.bin", 5000), ("b.bin", 7000));
        var path = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}{SnapshotFile.Extension}");
        var taken = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        try
        {
            SnapshotFile.Save(root, path, taken);
            var info = SnapshotFile.ReadInfo(path);

            Assert.NotNull(info);
            Assert.Equal(12_000, info!.TotalBytes);
            Assert.Equal(2, info.TotalFiles);
            Assert.Equal(taken, info.TakenUtc);
            Assert.Equal("root", info.RootPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_that_is_not_a_snapshot_is_rejected_rather_than_misread()
    {
        var path = Path.Combine(Path.GetTempPath(), $"junk-{Guid.NewGuid():N}{SnapshotFile.Extension}");
        File.WriteAllText(path, "this is not a snapshot");

        try
        {
            Assert.Null(SnapshotFile.ReadInfo(path));
            Assert.Null(SnapshotFile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_deep_tree_round_trips_without_overflowing_the_stack()
    {
        var deep = string.Join('/', Enumerable.Repeat("d", 200)) + "/leaf.bin";
        var root = Tree((deep, 42));
        var path = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}{SnapshotFile.Extension}");

        try
        {
            SnapshotFile.Save(root, path);
            var loaded = SnapshotFile.Load(path)!;

            Assert.Equal(42, loaded.Root.Size);
            Assert.Equal(200, loaded.Root.DirCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Listing_a_folder_returns_snapshots_newest_first()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"snaps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            SnapshotFile.Save(Tree(("a.bin", 1)), Path.Combine(directory, "old" + SnapshotFile.Extension),
                new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            SnapshotFile.Save(Tree(("a.bin", 2)), Path.Combine(directory, "new" + SnapshotFile.Extension),
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var list = SnapshotFile.List(directory);

            Assert.Equal(2, list.Count);
            Assert.True(list[0].TakenUtc > list[1].TakenUtc);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // -------------------------------------------------------------- comparison

    [Fact]
    public void An_unchanged_tree_reports_no_changes()
    {
        var before = Tree(("a.bin", 5_000_000), ("b.bin", 3_000_000));
        var after = Tree(("a.bin", 5_000_000), ("b.bin", 3_000_000));

        var result = TreeComparer.Compare(before, after);

        Assert.Equal(0, result.Delta);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void A_grown_file_is_reported_with_its_delta()
    {
        var before = Tree(("a.bin", 5_000_000));
        var after = Tree(("a.bin", 9_000_000));

        var result = TreeComparer.Compare(before, after);

        Assert.Equal(4_000_000, result.Delta);
        var change = Assert.Single(result.Changes);
        Assert.Equal("a.bin", change.Name);
        Assert.Equal(ChangeKind.Grew, change.Kind);
        Assert.Equal(4_000_000, change.Delta);
    }

    [Fact]
    public void A_new_file_is_reported_as_added()
    {
        var before = Tree(("a.bin", 5_000_000));
        var after = Tree(("a.bin", 5_000_000), ("new.bin", 8_000_000));

        var result = TreeComparer.Compare(before, after);

        var change = Assert.Single(result.Changes);
        Assert.Equal("new.bin", change.Name);
        Assert.Equal(ChangeKind.Added, change.Kind);
        Assert.Equal(8_000_000, result.AddedBytes);
    }

    [Fact]
    public void A_deleted_file_is_reported_as_removed()
    {
        var before = Tree(("a.bin", 5_000_000), ("gone.bin", 7_000_000));
        var after = Tree(("a.bin", 5_000_000));

        var result = TreeComparer.Compare(before, after);

        var change = Assert.Single(result.Changes);
        Assert.Equal("gone.bin", change.Name);
        Assert.Equal(ChangeKind.Removed, change.Kind);
        Assert.Equal(-7_000_000, change.Delta);
        Assert.Equal(7_000_000, result.RemovedBytes);
    }

    [Fact]
    public void Changes_below_the_threshold_are_not_listed()
    {
        var before = Tree(("a.bin", 5_000_000));
        var after = Tree(("a.bin", 5_000_100));

        var result = TreeComparer.Compare(before, after, threshold: 1024 * 1024);

        Assert.Empty(result.Changes);
        // The total still reflects it; only the itemised list is filtered.
        Assert.Equal(100, result.Delta);
    }

    [Fact]
    public void A_folder_is_not_listed_when_one_child_explains_the_whole_change()
    {
        // Otherwise one new file appears once for itself and once for every folder above it,
        // each reporting the same number.
        var before = Tree(("media/clip.mp4", 1_000_000));
        var after = Tree(("media/clip.mp4", 9_000_000));

        var result = TreeComparer.Compare(before, after);

        var change = Assert.Single(result.Changes);
        Assert.Equal("clip.mp4", change.Name);
    }

    [Fact]
    public void A_folder_is_listed_when_several_children_share_the_change()
    {
        var before = Tree(("media/a.mp4", 1_000_000), ("media/b.mp4", 1_000_000));
        var after = Tree(("media/a.mp4", 5_000_000), ("media/b.mp4", 5_000_000));

        var result = TreeComparer.Compare(before, after);

        // No single child accounts for the folder's 8 MB, so the folder earns its own line.
        Assert.Contains(result.Changes, c => c.Name == "media" && c.Delta == 8_000_000);
    }

    [Fact]
    public void Changes_are_ordered_by_magnitude_regardless_of_direction()
    {
        var before = Tree(("small.bin", 1_000_000), ("huge.bin", 50_000_000));
        var after = Tree(("small.bin", 4_000_000));

        var result = TreeComparer.Compare(before, after);

        // The 50 MB removal outranks the 3 MB growth even though it is negative.
        Assert.Equal("huge.bin", result.Changes[0].Name);
        Assert.Equal(ChangeKind.Removed, result.Changes[0].Kind);
    }

    [Fact]
    public void A_rename_reads_as_one_removal_and_one_addition()
    {
        // Honest rather than clever: nothing in the filesystem says these are the same thing.
        var before = Tree(("old-name.bin", 6_000_000));
        var after = Tree(("new-name.bin", 6_000_000));

        var result = TreeComparer.Compare(before, after);

        Assert.Equal(2, result.Changes.Count);
        Assert.Contains(result.Changes, c => c.Name == "old-name.bin" && c.Kind == ChangeKind.Removed);
        Assert.Contains(result.Changes, c => c.Name == "new-name.bin" && c.Kind == ChangeKind.Added);
        Assert.Equal(0, result.Delta);
    }

    [Fact]
    public void A_whole_new_folder_is_reported_once_at_its_top()
    {
        var before = Tree(("a.bin", 1_000_000));
        var after = Tree(("a.bin", 1_000_000), ("newdir/x.bin", 4_000_000));

        var result = TreeComparer.Compare(before, after);

        // "newdir is new" is what a person wants to read, not a line per file inside it.
        var change = Assert.Single(result.Changes);
        Assert.Equal("newdir", change.Name);
        Assert.Equal(ChangeKind.Added, change.Kind);
        Assert.Equal(4_000_000, change.Delta);
    }

    [Fact]
    public void Comparison_survives_a_snapshot_round_trip()
    {
        var before = Tree(("a.bin", 5_000_000));
        var after = Tree(("a.bin", 5_000_000), ("b.bin", 9_000_000));
        var path = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}{SnapshotFile.Extension}");

        try
        {
            SnapshotFile.Save(before, path);
            var loaded = SnapshotFile.Load(path)!;

            var result = TreeComparer.Compare(loaded.Root, after);

            Assert.Equal(9_000_000, result.Delta);
            Assert.Single(result.Changes);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
