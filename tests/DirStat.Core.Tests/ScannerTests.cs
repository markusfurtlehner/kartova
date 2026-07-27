using DirStat.Core.Model;
using DirStat.Core.Scanning;
using Xunit;

namespace DirStat.Core.Tests;

/// <summary>Creates a disposable directory tree on disk for a single test.</summary>
public sealed class TempTree : IDisposable
{
    public string Root { get; }

    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "dirstat-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Dir(string relative)
    {
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public string File(string relative, int bytes)
    {
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a test left a handle open; the temp dir will be reaped */ }
        catch (UnauthorizedAccessException) { }
    }
}

public class ScannerTests
{
    private static FileNode Find(FileNode root, string name) =>
        root.DescendantsAndSelf().First(n => n.Name == name);

    [Fact]
    public void Scan_totals_sizes_across_the_whole_tree()
    {
        using var tree = new TempTree();
        tree.File("a.bin", 1000);
        tree.File("b.bin", 2000);
        tree.File("sub/c.bin", 3000);
        tree.File("sub/deep/d.bin", 4000);

        var result = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false });

        Assert.Equal(10_000, result.Root.Size);
        Assert.Equal(4, result.TotalFiles);
        Assert.Equal(4, result.Root.FileCount);
    }

    [Fact]
    public void Scan_counts_directories_excluding_the_root_itself()
    {
        using var tree = new TempTree();
        tree.Dir("one");
        tree.Dir("two/three");
        tree.File("one/f.bin", 10);

        var result = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false });

        // one, two, two/three
        Assert.Equal(3, result.Root.DirCount);
    }

    [Fact]
    public void Directory_size_is_the_sum_of_its_subtree()
    {
        using var tree = new TempTree();
        tree.File("sub/c.bin", 3000);
        tree.File("sub/deep/d.bin", 4000);
        tree.File("outside.bin", 500);

        var result = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false });

        var sub = Find(result.Root, "sub");
        Assert.Equal(7000, sub.Size);
        Assert.Equal(2, sub.FileCount);
        Assert.Equal(1, sub.DirCount);
    }

    [Fact]
    public void Empty_directory_scans_to_an_empty_tree()
    {
        using var tree = new TempTree();

        var result = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false });

        Assert.Equal(0, result.Root.Size);
        Assert.Equal(0, result.TotalFiles);
        Assert.Empty(result.Root.Children!);
    }

    [Fact]
    public void Excluded_directory_names_are_skipped_everywhere()
    {
        using var tree = new TempTree();
        tree.File("keep.bin", 100);
        tree.File("node_modules/huge.bin", 999_999);
        tree.File("nested/node_modules/huge.bin", 999_999);

        var options = new ScanOptions { IncludeFreeSpace = false };
        options.ExcludedDirectoryNames.Add("node_modules");

        var result = new DirectoryScanner().Scan([tree.Root], options);

        Assert.Equal(100, result.Root.Size);
        Assert.DoesNotContain(result.Root.DescendantsAndSelf(), n => n.Name == "node_modules");
    }

    [Fact]
    public void Excluded_absolute_paths_are_skipped()
    {
        using var tree = new TempTree();
        tree.File("keep.bin", 100);
        var skipped = tree.Dir("skipme");
        tree.File("skipme/huge.bin", 50_000);

        var options = new ScanOptions { IncludeFreeSpace = false };
        options.ExcludedPaths.Add(skipped);

        var result = new DirectoryScanner().Scan([tree.Root], options);

        Assert.Equal(100, result.Root.Size);
    }

    [Fact]
    public void Hidden_files_are_included_by_default()
    {
        using var tree = new TempTree();
        var hidden = tree.File("secret.bin", 4096);
        if (OperatingSystem.IsWindows())
            System.IO.File.SetAttributes(hidden, FileAttributes.Hidden);

        var result = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false });

        Assert.Equal(4096, result.Root.Size);
    }

    [Fact]
    public void Cancellation_still_returns_a_usable_partial_tree()
    {
        using var tree = new TempTree();
        for (var i = 0; i < 40; i++) tree.File($"d{i}/f.bin", 128);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancelled before any work begins

        var result = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false },
            progress: null, cancellationToken: cts.Token);

        Assert.True(result.WasCancelled);
        Assert.NotNull(result.Root);
        Assert.True(result.Root.Size >= 0);
    }

    [Fact]
    public void Multiple_roots_are_combined_under_one_tree()
    {
        using var a = new TempTree();
        using var b = new TempTree();
        a.File("x.bin", 1000);
        b.File("y.bin", 2000);

        var result = new DirectoryScanner().Scan(
            [a.Root, b.Root], new ScanOptions { IncludeFreeSpace = false });

        Assert.Equal(2, result.Roots.Count);
        Assert.Equal(3000, result.Root.Size);
        Assert.Equal(2, result.TotalFiles);
    }

    [Fact]
    public void Extension_stats_group_files_by_type()
    {
        using var tree = new TempTree();
        tree.File("a.txt", 1000);
        tree.File("b.txt", 2000);
        tree.File("c.jpg", 500);
        tree.File("noext", 100);

        var result = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false });

        var txt = result.Extensions.Single(e => e.Extension == ".txt");
        Assert.Equal(3000, txt.TotalSize);
        Assert.Equal(2, txt.FileCount);

        // Ordered by descending size, so .txt leads.
        Assert.Equal(".txt", result.Extensions[0].Extension);

        var none = result.Extensions.Single(e => e.Extension.Length == 0);
        Assert.Equal(100, none.TotalSize);
    }

    [Fact]
    public void Progress_is_reported_and_ends_complete()
    {
        using var tree = new TempTree();
        for (var i = 0; i < 20; i++) tree.File($"f{i}.bin", 256);

        var reports = new List<ScanProgress>();
        var progress = new Progress<ScanProgress>(p =>
        {
            lock (reports) reports.Add(p);
        });

        var result = new DirectoryScanner().Scan(
            [tree.Root],
            new ScanOptions { IncludeFreeSpace = false, ProgressInterval = TimeSpan.FromMilliseconds(1) },
            progress);

        Assert.Equal(20, result.TotalFiles);

        // Progress<T> marshals asynchronously; give the callbacks a moment to land.
        SpinWait.SpinUntil(() => { lock (reports) return reports.Any(r => r.IsComplete); },
            TimeSpan.FromSeconds(5));

        lock (reports) Assert.Contains(reports, r => r.IsComplete);
    }

    [Fact]
    public void Children_are_sorted_by_descending_size()
    {
        using var tree = new TempTree();
        tree.File("small.bin", 10);
        tree.File("large.bin", 10_000);
        tree.File("medium.bin", 500);

        var result = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false });

        var sizes = result.Root.Children!.Select(c => c.Size).ToArray();
        Assert.Equal(sizes.OrderByDescending(s => s), sizes);
    }

    [Fact]
    public void Full_path_round_trips_for_a_nested_file()
    {
        using var tree = new TempTree();
        var expected = tree.File("a/b/c.bin", 1);

        var result = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false });

        var node = Find(result.Root, "c.bin");
        Assert.Equal(Path.GetFullPath(expected), node.GetFullPath());
    }

    [Fact]
    public void Size_on_disk_rounds_up_to_the_allocation_unit()
    {
        using var tree = new TempTree();
        tree.File("tiny.bin", 1);

        var result = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false, ComputeSizeOnDisk = true });

        var node = Find(result.Root, "tiny.bin");
        Assert.Equal(1, node.Size);
        // A one-byte file always occupies at least one cluster.
        Assert.True(node.SizeOnDisk >= 512, $"expected a full cluster, got {node.SizeOnDisk}");
        Assert.True(node.SizeOnDisk <= 65_536);
    }

    [Fact]
    public void Symlinked_directory_is_recorded_but_not_descended()
    {
        using var tree = new TempTree();
        var target = tree.Dir("target");
        tree.File("target/inside.bin", 5000);

        var linkPath = Path.Combine(tree.Root, "link");
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Windows without Developer Mode cannot create links; nothing to assert.
        }

        var result = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false });

        // Counted once through the real path, not twice through the link.
        Assert.Equal(5000, result.Root.Size);

        var link = result.Root.DescendantsAndSelf().FirstOrDefault(n => n.Name == "link");
        Assert.NotNull(link);
        Assert.True(link!.HasFlag(NodeFlags.ReparsePoint));
    }

    [Fact]
    public void Deeply_nested_tree_does_not_overflow_the_stack()
    {
        using var tree = new TempTree();
        var relative = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("d", 60));
        tree.File(Path.Combine(relative, "leaf.bin"), 42);

        var result = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false });

        Assert.Equal(42, result.Root.Size);
        Assert.Equal(60, result.Root.DirCount);
    }

    [Fact]
    public void Concurrent_scan_produces_the_same_total_as_a_single_worker()
    {
        using var tree = new TempTree();
        for (var i = 0; i < 12; i++)
            for (var j = 0; j < 12; j++)
                tree.File($"d{i}/s{j}/f.bin", 64);

        var serial = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false, MaxDegreeOfParallelism = 1 });
        var parallel = new DirectoryScanner().Scan(
            [tree.Root], new ScanOptions { IncludeFreeSpace = false, MaxDegreeOfParallelism = 16 });

        Assert.Equal(serial.Root.Size, parallel.Root.Size);
        Assert.Equal(serial.TotalFiles, parallel.TotalFiles);
        Assert.Equal(serial.Root.DirCount, parallel.Root.DirCount);
    }
}
