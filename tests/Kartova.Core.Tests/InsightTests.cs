using Kartova.Core.Insights;
using Kartova.Core.Model;
using Kartova.Core.Scanning;
using Xunit;

namespace Kartova.Core.Tests;

public class InsightTests
{
    private static FileNode Build(Action<Builder> configure)
    {
        var root = new FileNode("root", NodeFlags.Directory | NodeFlags.Root);
        configure(new Builder(root));
        DirectoryScanner.Aggregate(root);
        return root;
    }

    private sealed class Builder(FileNode root)
    {
        public Builder File(string path, long size, DateTime? modified = null)
        {
            var node = Ensure(path, out var name);
            var file = new FileNode(name)
            {
                Parent = node,
                Size = size,
                LastWriteUtcTicks = (modified ?? DateTime.UtcNow).Ticks,
            };
            node.Children = (node.Children ?? []).Append(file).ToArray();
            return this;
        }

        public Builder Folder(string path)
        {
            var node = Ensure(path + "/x", out _);
            node.Children ??= [];
            return this;
        }

        private FileNode Ensure(string path, out string leafName)
        {
            var segments = path.Split('/');
            leafName = segments[^1];
            var current = root;

            for (var i = 0; i < segments.Length - 1; i++)
            {
                var existing = current.Children?.FirstOrDefault(c => c.Name == segments[i] && c.IsDirectory);
                if (existing is null)
                {
                    existing = new FileNode(segments[i], NodeFlags.Directory) { Parent = current, Children = [] };
                    current.Children = (current.Children ?? []).Append(existing).ToArray();
                }
                current = existing;
            }

            return current;
        }
    }

    private static InsightGroup? GroupOf(InsightResult result, InsightKind kind) =>
        result.Groups.FirstOrDefault(g => g.Kind == kind);

    // ------------------------------------------------------------------ stale

    [Fact]
    public void A_large_old_file_is_reported_as_stale()
    {
        var root = Build(b => b
            .File("old.bin", 50_000_000, DateTime.UtcNow.AddYears(-3))
            .File("recent.bin", 50_000_000, DateTime.UtcNow.AddDays(-1)));

        var result = InsightAnalyzer.Analyze(root);
        var stale = GroupOf(result, InsightKind.StaleFile);

        Assert.NotNull(stale);
        var item = Assert.Single(stale!.Items);
        Assert.Equal("old.bin", item.Node.Name);
    }

    [Fact]
    public void A_small_old_file_is_not_worth_listing()
    {
        var root = Build(b => b.File("tiny-old.bin", 1000, DateTime.UtcNow.AddYears(-5)));

        var result = InsightAnalyzer.Analyze(root);

        Assert.Null(GroupOf(result, InsightKind.StaleFile));
    }

    [Fact]
    public void The_stale_threshold_is_configurable()
    {
        var root = Build(b => b.File("a.bin", 50_000_000, DateTime.UtcNow.AddDays(-100)));

        var lenient = InsightAnalyzer.Analyze(root, new InsightOptions { StaleAfter = TimeSpan.FromDays(365) });
        var strict = InsightAnalyzer.Analyze(root, new InsightOptions { StaleAfter = TimeSpan.FromDays(30) });

        Assert.Null(GroupOf(lenient, InsightKind.StaleFile));
        Assert.NotNull(GroupOf(strict, InsightKind.StaleFile));
    }

    // ------------------------------------------------------- empty and zero byte

    [Fact]
    public void An_empty_folder_is_reported()
    {
        var root = Build(b => b.Folder("nothing").File("a.bin", 5000));

        var result = InsightAnalyzer.Analyze(root);
        var empty = GroupOf(result, InsightKind.EmptyFolder);

        Assert.NotNull(empty);
        Assert.Contains(empty!.Items, i => i.Node.Name == "nothing");
    }

    [Fact]
    public void A_folder_holding_only_an_empty_folder_is_not_itself_empty()
    {
        // It has a directory underneath it, so removing it is a different decision.
        var root = Build(b => b.Folder("outer/inner"));

        var result = InsightAnalyzer.Analyze(root);
        var empty = GroupOf(result, InsightKind.EmptyFolder)!;

        Assert.Contains(empty.Items, i => i.Node.Name == "inner");
        Assert.DoesNotContain(empty.Items, i => i.Node.Name == "outer");
    }

    [Fact]
    public void Zero_byte_files_are_reported_separately_from_stale_ones()
    {
        var root = Build(b => b.File("empty.bin", 0, DateTime.UtcNow.AddYears(-5)));

        var result = InsightAnalyzer.Analyze(root);

        Assert.NotNull(GroupOf(result, InsightKind.ZeroByteFile));
        Assert.Null(GroupOf(result, InsightKind.StaleFile));
    }

    // ------------------------------------------------------------------- junk

    [Fact]
    public void Node_modules_is_recognised_and_reported_whole()
    {
        var root = Build(b => b
            .File("project/node_modules/pkg/index.js", 5_000_000)
            .File("project/node_modules/other/lib.js", 3_000_000)
            .File("project/src/app.js", 2_000_000));

        var findings = JunkClassifier.Find(root);

        // One decision, not one per file inside it.
        var finding = Assert.Single(findings);
        Assert.Equal("node_modules", finding.Node.Name);
        Assert.Equal(8_000_000, finding.Size);
        Assert.Equal(JunkConfidence.Rebuildable, finding.Category.Confidence);
    }

    [Fact]
    public void Files_inside_a_recognised_folder_are_not_listed_again()
    {
        var root = Build(b => b.File("project/node_modules/huge.log", 9_000_000));

        var findings = JunkClassifier.Find(root);

        Assert.Single(findings);
        Assert.Equal("node_modules", findings[0].Node.Name);
    }

    [Fact]
    public void Build_output_and_python_caches_are_recognised()
    {
        var root = Build(b => b
            .File("app/obj/a.o", 4_000_000)
            .File("app/__pycache__/m.pyc", 2_000_000));

        var findings = JunkClassifier.Find(root);

        Assert.Contains(findings, f => f.Node.Name == "obj");
        Assert.Contains(findings, f => f.Node.Name == "__pycache__");
        Assert.All(findings, f => Assert.Equal(JunkConfidence.Rebuildable, f.Category.Confidence));
    }

    [Fact]
    public void Loose_log_and_backup_files_are_recognised_by_extension()
    {
        var root = Build(b => b
            .File("server.log", 5_000_000)
            .File("notes.bak", 3_000_000));

        var findings = JunkClassifier.Find(root);

        Assert.Contains(findings, f => f.Node.Name == "server.log");
        Assert.Contains(findings, f => f.Node.Name == "notes.bak");
    }

    [Fact]
    public void Backups_and_installers_are_flagged_for_review_rather_than_as_rebuildable()
    {
        // These can be deliberate, so they must not be presented as safe to delete.
        var root = Build(b => b.File("notes.bak", 3_000_000).File("setup.msi", 8_000_000));

        var findings = JunkClassifier.Find(root);

        Assert.All(findings, f => Assert.Equal(JunkConfidence.Review, f.Category.Confidence));
    }

    [Fact]
    public void Ordinary_documents_are_never_classified_as_junk()
    {
        var root = Build(b => b
            .File("Documents/taxes.pdf", 9_000_000)
            .File("Photos/holiday.jpg", 8_000_000)
            .File("Music/song.mp3", 7_000_000)
            .File("Code/main.cs", 5_000_000));

        var findings = JunkClassifier.Find(root);

        Assert.Empty(findings);
    }

    [Fact]
    public void Small_junk_is_below_the_reporting_threshold()
    {
        var root = Build(b => b.File("project/node_modules/tiny.js", 100));

        Assert.Empty(JunkClassifier.Find(root, minimumSize: 1024 * 1024));
    }

    [Fact]
    public void Junk_is_grouped_by_category_and_ordered_by_size()
    {
        var root = Build(b => b
            .File("a/node_modules/x.js", 20_000_000)
            .File("b/obj/y.o", 5_000_000)
            .File("c.log", 1_500_000));

        var result = InsightAnalyzer.Analyze(root);
        var junk = result.Groups.Where(g => g.Kind == InsightKind.Junk).ToArray();

        Assert.True(junk.Length >= 2);
        Assert.All(junk, g => Assert.NotNull(g.Category));
        // Largest category leads.
        Assert.True(junk[0].TotalBytes >= junk[^1].TotalBytes);
    }

    [Fact]
    public void Groups_are_capped_so_one_folder_cannot_flood_the_list()
    {
        var root = Build(b =>
        {
            for (var i = 0; i < 50; i++) b.File($"f{i}.log", 2_000_000);
        });

        var result = InsightAnalyzer.Analyze(root, new InsightOptions { MaxPerGroup = 10 });
        var junk = result.Groups.First(g => g.Kind == InsightKind.Junk);

        Assert.Equal(10, junk.Count);
    }

    [Fact]
    public void Each_analysis_can_be_switched_off_independently()
    {
        var root = Build(b => b
            .File("old.bin", 50_000_000, DateTime.UtcNow.AddYears(-5))
            .File("a.log", 5_000_000)
            .File("zero.bin", 0)
            .Folder("empty"));

        var result = InsightAnalyzer.Analyze(root, new InsightOptions
        {
            FindStale = false,
            FindJunk = false,
            FindZeroByteFiles = false,
            FindEmptyFolders = false,
        });

        Assert.Empty(result.Groups);
    }

    [Fact]
    public void An_empty_tree_produces_no_groups()
    {
        var root = Build(_ => { });
        Assert.Empty(InsightAnalyzer.Analyze(root).Groups);
    }
}
