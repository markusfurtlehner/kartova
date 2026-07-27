using System.Text;
using DirStat.Core.Duplicates;
using DirStat.Core.Model;
using DirStat.Core.Scanning;
using Xunit;

namespace DirStat.Core.Tests;

public class DuplicateTests
{
    /// <summary>Writes a file of exactly <paramref name="size"/> bytes with deterministic content.</summary>
    private static void WriteFile(TempTree tree, string relative, int size, byte seed)
    {
        var full = Path.Combine(tree.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var data = new byte[size];
        for (var i = 0; i < size; i++) data[i] = (byte)((i * 31 + seed) & 0xFF);
        File.WriteAllBytes(full, data);
    }

    private static DuplicateResult Run(TempTree tree, DuplicateOptions? options = null)
    {
        var scan = new DirectoryScanner().Scan([tree.Root], new ScanOptions { IncludeFreeSpace = false });
        return new DuplicateFinder().Find(scan.Root, options ?? new DuplicateOptions { MinimumFileSize = 1 });
    }

    private static string[] NamesIn(DuplicateGroup group) =>
        group.Items.Select(i => i.Name).OrderBy(n => n).ToArray();

    // -------------------------------------------------------------------- files

    [Fact]
    public void Identical_files_are_reported_as_one_group()
    {
        using var tree = new TempTree();
        WriteFile(tree, "a.bin", 50_000, seed: 1);
        WriteFile(tree, "copies/b.bin", 50_000, seed: 1);

        var result = Run(tree);

        var group = Assert.Single(result.FileGroups);
        Assert.Equal(2, group.CopyCount);
        Assert.Equal(50_000, group.ItemSize);
        Assert.Equal(50_000, group.WastedBytes);
        Assert.Equal(["a.bin", "b.bin"], NamesIn(group));
    }

    [Fact]
    public void Same_size_but_different_content_is_not_a_duplicate()
    {
        using var tree = new TempTree();
        WriteFile(tree, "a.bin", 50_000, seed: 1);
        WriteFile(tree, "b.bin", 50_000, seed: 2);

        var result = Run(tree);

        Assert.Empty(result.FileGroups);
    }

    [Fact]
    public void Files_differing_only_after_the_screening_window_are_still_caught()
    {
        // Identical for the first 16 KB, differing only near the end. The prefix screen
        // cannot separate these, so this exercises the full-hash stage.
        using var tree = new TempTree();
        var size = FileHasher.ScreenLength * 4;

        var a = new byte[size];
        for (var i = 0; i < size; i++) a[i] = (byte)(i & 0xFF);
        var b = (byte[])a.Clone();
        b[^1] ^= 0xFF;

        File.WriteAllBytes(Path.Combine(tree.Root, "a.bin"), a);
        File.WriteAllBytes(Path.Combine(tree.Root, "b.bin"), b);
        File.WriteAllBytes(Path.Combine(tree.Root, "c.bin"), a);

        var result = Run(tree);

        var group = Assert.Single(result.FileGroups);
        Assert.Equal(["a.bin", "c.bin"], NamesIn(group));
    }

    [Fact]
    public void Three_copies_report_two_copies_worth_of_waste()
    {
        using var tree = new TempTree();
        WriteFile(tree, "one/x.bin", 30_000, seed: 7);
        WriteFile(tree, "two/x.bin", 30_000, seed: 7);
        WriteFile(tree, "three/x.bin", 30_000, seed: 7);

        var result = Run(tree, new DuplicateOptions { MinimumFileSize = 1, FindDuplicateFolders = false });

        var group = Assert.Single(result.FileGroups);
        Assert.Equal(3, group.CopyCount);
        Assert.Equal(60_000, group.WastedBytes);
    }

    [Fact]
    public void Files_below_the_minimum_size_are_ignored()
    {
        using var tree = new TempTree();
        WriteFile(tree, "tiny1.bin", 100, seed: 3);
        WriteFile(tree, "tiny2.bin", 100, seed: 3);

        var result = Run(tree, new DuplicateOptions { MinimumFileSize = 4096 });

        Assert.Empty(result.FileGroups);
    }

    [Fact]
    public void Ignored_extensions_are_skipped()
    {
        using var tree = new TempTree();
        WriteFile(tree, "a.log", 20_000, seed: 4);
        WriteFile(tree, "b.log", 20_000, seed: 4);

        var options = new DuplicateOptions { MinimumFileSize = 1 };
        options.IgnoredExtensions.Add(".log");

        var result = Run(tree, options);

        Assert.Empty(result.FileGroups);
    }

    [Fact]
    public void Unique_sizes_are_never_read()
    {
        // The whole design rests on this: a file whose length is unique cannot have a twin,
        // so it must never cost a read.
        using var tree = new TempTree();
        for (var i = 0; i < 20; i++) WriteFile(tree, $"f{i}.bin", 10_000 + i * 100, seed: (byte)i);

        var result = Run(tree);

        Assert.Empty(result.FileGroups);
        Assert.Equal(0, result.BytesHashed);
        Assert.Equal(0, result.FilesHashed);
    }

    [Fact]
    public void Byte_for_byte_verification_agrees_with_hashing()
    {
        using var tree = new TempTree();
        WriteFile(tree, "a.bin", 40_000, seed: 9);
        WriteFile(tree, "b.bin", 40_000, seed: 9);
        WriteFile(tree, "c.bin", 40_000, seed: 10);

        var verified = Run(tree, new DuplicateOptions { MinimumFileSize = 1, VerifyByteForByte = true });

        var group = Assert.Single(verified.FileGroups);
        Assert.Equal(["a.bin", "b.bin"], NamesIn(group));
    }

    [Fact]
    public void Groups_are_ordered_by_recoverable_space()
    {
        using var tree = new TempTree();
        WriteFile(tree, "small1.bin", 10_000, seed: 1);
        WriteFile(tree, "small2.bin", 10_000, seed: 1);
        WriteFile(tree, "big1.bin", 90_000, seed: 2);
        WriteFile(tree, "big2.bin", 90_000, seed: 2);

        var result = Run(tree, new DuplicateOptions { MinimumFileSize = 1, FindDuplicateFolders = false });

        Assert.Equal(2, result.FileGroups.Count);
        Assert.True(result.FileGroups[0].WastedBytes > result.FileGroups[1].WastedBytes);
        Assert.Equal(100_000, result.WastedInFiles);
    }

    [Fact]
    public void An_empty_tree_yields_no_groups()
    {
        using var tree = new TempTree();
        var result = Run(tree);

        Assert.Empty(result.FileGroups);
        Assert.Empty(result.FolderGroups);
        Assert.False(result.WasCancelled);
    }

    [Fact]
    public void Cancellation_returns_promptly_and_says_so()
    {
        using var tree = new TempTree();
        for (var i = 0; i < 30; i++)
        {
            WriteFile(tree, $"a{i}.bin", 200_000, seed: 1);
            WriteFile(tree, $"b{i}.bin", 200_000, seed: 1);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var scan = new DirectoryScanner().Scan([tree.Root], new ScanOptions { IncludeFreeSpace = false });
        var result = new DuplicateFinder().Find(
            scan.Root, new DuplicateOptions { MinimumFileSize = 1 }, progress: null, cancellationToken: cts.Token);

        Assert.True(result.WasCancelled);
    }

    // ------------------------------------------------------------------ folders

    [Fact]
    public void Identical_folders_are_reported_as_a_folder_group()
    {
        using var tree = new TempTree();
        WriteFile(tree, "alpha/one.bin", 20_000, seed: 1);
        WriteFile(tree, "alpha/two.bin", 30_000, seed: 2);
        WriteFile(tree, "beta/one.bin", 20_000, seed: 1);
        WriteFile(tree, "beta/two.bin", 30_000, seed: 2);

        var result = Run(tree);

        var group = Assert.Single(result.FolderGroups);
        Assert.Equal(["alpha", "beta"], NamesIn(group));
        Assert.Equal(50_000, group.ItemSize);
        Assert.Equal(50_000, group.WastedBytes);
    }

    [Fact]
    public void Folders_with_the_same_bytes_under_different_names_do_not_match()
    {
        // Same content, different filenames: the folders are not interchangeable, so the
        // signature has to fold in names as well as content.
        using var tree = new TempTree();
        WriteFile(tree, "alpha/one.bin", 20_000, seed: 1);
        WriteFile(tree, "beta/renamed.bin", 20_000, seed: 1);

        var result = Run(tree);

        Assert.Empty(result.FolderGroups);
        // The files themselves are still duplicates.
        Assert.Single(result.FileGroups);
    }

    [Fact]
    public void Folders_differing_by_one_file_do_not_match()
    {
        using var tree = new TempTree();
        WriteFile(tree, "alpha/one.bin", 20_000, seed: 1);
        WriteFile(tree, "alpha/two.bin", 20_000, seed: 2);
        WriteFile(tree, "beta/one.bin", 20_000, seed: 1);
        WriteFile(tree, "beta/two.bin", 20_000, seed: 3);

        var result = Run(tree);

        Assert.Empty(result.FolderGroups);
    }

    [Fact]
    public void Nested_duplicate_folders_report_only_the_outermost()
    {
        // Two matching trees also have matching subdirectories. Reporting every nested pair
        // would bury the only finding that matters: the outer folder worth deleting.
        using var tree = new TempTree();
        WriteFile(tree, "alpha/inner/deep/a.bin", 20_000, seed: 1);
        WriteFile(tree, "alpha/inner/b.bin", 25_000, seed: 2);
        WriteFile(tree, "beta/inner/deep/a.bin", 20_000, seed: 1);
        WriteFile(tree, "beta/inner/b.bin", 25_000, seed: 2);

        var result = Run(tree);

        var group = Assert.Single(result.FolderGroups);
        Assert.Equal(["alpha", "beta"], NamesIn(group));
    }

    [Fact]
    public void Files_inside_reported_duplicate_folders_are_not_listed_separately()
    {
        // They would be removed with the folder, so listing them again is double counting.
        using var tree = new TempTree();
        WriteFile(tree, "alpha/one.bin", 20_000, seed: 1);
        WriteFile(tree, "beta/one.bin", 20_000, seed: 1);

        var result = Run(tree);

        Assert.Single(result.FolderGroups);
        Assert.Empty(result.FileGroups);
    }

    [Fact]
    public void A_duplicate_file_outside_a_duplicate_folder_is_still_reported()
    {
        using var tree = new TempTree();
        WriteFile(tree, "alpha/one.bin", 20_000, seed: 1);
        WriteFile(tree, "beta/one.bin", 20_000, seed: 1);

        // Named differently, so "loose" is not itself a duplicate of alpha and beta —
        // otherwise all three folders match and no file sits outside one.
        WriteFile(tree, "loose/renamed.bin", 20_000, seed: 1);

        var result = Run(tree);

        // alpha and beta match as folders; the loose third copy still deserves a mention.
        var folders = Assert.Single(result.FolderGroups);
        Assert.Equal(["alpha", "beta"], NamesIn(folders));

        var files = Assert.Single(result.FileGroups);
        Assert.Equal(3, files.CopyCount);
    }

    [Fact]
    public void Every_folder_holding_the_same_content_joins_one_group()
    {
        using var tree = new TempTree();
        WriteFile(tree, "alpha/one.bin", 20_000, seed: 1);
        WriteFile(tree, "beta/one.bin", 20_000, seed: 1);
        WriteFile(tree, "gamma/one.bin", 20_000, seed: 1);

        var result = Run(tree);

        var group = Assert.Single(result.FolderGroups);
        Assert.Equal(["alpha", "beta", "gamma"], NamesIn(group));
        Assert.Equal(40_000, group.WastedBytes);

        // All copies live inside those folders, so listing them again would double count.
        Assert.Empty(result.FileGroups);
    }

    [Fact]
    public void Folder_detection_can_be_switched_off()
    {
        using var tree = new TempTree();
        WriteFile(tree, "alpha/one.bin", 20_000, seed: 1);
        WriteFile(tree, "beta/one.bin", 20_000, seed: 1);

        var result = Run(tree, new DuplicateOptions { MinimumFileSize = 1, FindDuplicateFolders = false });

        Assert.Empty(result.FolderGroups);
        Assert.Single(result.FileGroups);
    }

    [Fact]
    public void Group_members_lead_with_the_best_candidate_to_keep()
    {
        using var tree = new TempTree();
        WriteFile(tree, "deep/nested/copy.bin", 20_000, seed: 1);
        WriteFile(tree, "original.bin", 20_000, seed: 1);

        var original = Path.Combine(tree.Root, "original.bin");
        File.SetLastWriteTimeUtc(original, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = Run(tree, new DuplicateOptions { MinimumFileSize = 1, FindDuplicateFolders = false });

        var group = Assert.Single(result.FileGroups);
        Assert.Equal("original.bin", group.Items[0].Name);
    }

    // ------------------------------------------------------------------ hashing

    [Fact]
    public void Identical_content_hashes_alike_and_different_content_does_not()
    {
        using var tree = new TempTree();
        WriteFile(tree, "a.bin", 5000, seed: 1);
        WriteFile(tree, "b.bin", 5000, seed: 1);
        WriteFile(tree, "c.bin", 5000, seed: 2);

        var a = FileHasher.HashFile(Path.Combine(tree.Root, "a.bin"));
        var b = FileHasher.HashFile(Path.Combine(tree.Root, "b.bin"));
        var c = FileHasher.HashFile(Path.Combine(tree.Root, "c.bin"));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.False(a.IsZero);
    }

    [Fact]
    public void Byte_comparison_agrees_with_the_hash()
    {
        using var tree = new TempTree();
        WriteFile(tree, "a.bin", 5000, seed: 1);
        WriteFile(tree, "b.bin", 5000, seed: 1);
        WriteFile(tree, "c.bin", 5000, seed: 2);

        Assert.True(FileHasher.ContentsEqual(
            Path.Combine(tree.Root, "a.bin"), Path.Combine(tree.Root, "b.bin")));
        Assert.False(FileHasher.ContentsEqual(
            Path.Combine(tree.Root, "a.bin"), Path.Combine(tree.Root, "c.bin")));
    }

    [Fact]
    public void Directory_signatures_depend_on_names_and_order_independence()
    {
        var one = FileHasher.CombineDirectory([("a", new ContentHash(1, 2)), ("b", new ContentHash(3, 4))]);
        var same = FileHasher.CombineDirectory([("a", new ContentHash(1, 2)), ("b", new ContentHash(3, 4))]);
        var renamed = FileHasher.CombineDirectory([("a", new ContentHash(1, 2)), ("z", new ContentHash(3, 4))]);
        var reordered = FileHasher.CombineDirectory([("b", new ContentHash(3, 4)), ("a", new ContentHash(1, 2))]);

        Assert.Equal(one, same);
        Assert.NotEqual(one, renamed);
        // Callers sort before combining, so a different order is genuinely a different input.
        Assert.NotEqual(one, reordered);
    }

    [Fact]
    public void Hash_of_an_empty_file_is_stable_and_not_an_error()
    {
        using var tree = new TempTree();
        File.WriteAllBytes(Path.Combine(tree.Root, "empty.bin"), []);

        var first = FileHasher.HashFile(Path.Combine(tree.Root, "empty.bin"));
        var second = FileHasher.HashFile(Path.Combine(tree.Root, "empty.bin"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Text_files_with_the_same_bytes_match_regardless_of_name()
    {
        using var tree = new TempTree();
        var content = Encoding.UTF8.GetBytes(new string('x', 20_000));
        File.WriteAllBytes(Path.Combine(tree.Root, "notes.txt"), content);
        File.WriteAllBytes(Path.Combine(tree.Root, "notes-copy.txt"), content);

        var result = Run(tree, new DuplicateOptions { MinimumFileSize = 1, FindDuplicateFolders = false });

        var group = Assert.Single(result.FileGroups);
        Assert.Equal(2, group.CopyCount);
    }
}
