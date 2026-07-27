using System.Globalization;
using Kartova.Core.Files;
using Xunit;

namespace Kartova.Core.Tests;

public class FormattingTests : IDisposable
{
    // Formatting is culture-sensitive; pin it so assertions hold on any machine.
    private readonly CultureInfo _original = CultureInfo.CurrentCulture;

    public FormattingTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        SizeFormatter.UseBinaryUnits = true;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _original;
        SizeFormatter.UseBinaryUnits = true;
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(999, "999 B")]
    [InlineData(1024, "1.00 KiB")]
    [InlineData(1536, "1.50 KiB")]
    [InlineData(1048576, "1.00 MiB")]
    [InlineData(1073741824, "1.00 GiB")]
    public void Binary_units_are_formatted_with_adaptive_precision(long bytes, string expected)
    {
        Assert.Equal(expected, SizeFormatter.Format(bytes));
    }

    [Fact]
    public void Precision_shrinks_as_the_number_grows()
    {
        // Two decimals below 10, one below 100, none above — so columns stay aligned.
        Assert.Equal("9.77 KiB", SizeFormatter.Format(10_000));
        Assert.Equal("97.7 KiB", SizeFormatter.Format(100_000));
        Assert.Equal("977 KiB", SizeFormatter.Format(1_000_000));
    }

    [Fact]
    public void Decimal_units_can_be_selected()
    {
        SizeFormatter.UseBinaryUnits = false;
        Assert.Equal("1.00 KB", SizeFormatter.Format(1000));
        Assert.Equal("1.00 MB", SizeFormatter.Format(1_000_000));
    }

    [Fact]
    public void Negative_sizes_keep_their_sign()
    {
        Assert.Equal("-1.00 KiB", SizeFormatter.Format(-1024));
    }

    [Fact]
    public void Percentages_gain_precision_as_they_shrink()
    {
        Assert.Equal("100.0 %", SizeFormatter.FormatPercent(1.0));
        Assert.Equal("50.0 %", SizeFormatter.FormatPercent(0.5));
        Assert.Equal("0.05 %", SizeFormatter.FormatPercent(0.0005));
    }

    [Fact]
    public void Percentages_survive_a_division_by_zero()
    {
        Assert.Equal("0.0 %", SizeFormatter.FormatPercent(double.NaN));
        Assert.Equal("0.0 %", SizeFormatter.FormatPercent(double.PositiveInfinity));
    }

    [Theory]
    [InlineData(0.4, "400 ms")]
    [InlineData(2.5, "2.5 s")]
    [InlineData(75, "1:15")]
    public void Durations_are_formatted_by_magnitude(double seconds, string expected)
    {
        Assert.Equal(expected, SizeFormatter.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Long_durations_include_hours()
    {
        Assert.Equal("1:05:03", SizeFormatter.FormatDuration(new TimeSpan(1, 5, 3)));
    }

    [Fact]
    public void Rate_of_zero_reads_as_unknown()
    {
        Assert.Equal("—", SizeFormatter.FormatRate(0));
    }
}

public class FileTypeColorTests
{
    [Fact]
    public void Known_extensions_are_stable_across_calls()
    {
        Assert.Equal(FileTypeColors.ForExtension(".mp4"), FileTypeColors.ForExtension(".mp4"));
    }

    [Fact]
    public void Extension_lookup_ignores_case()
    {
        Assert.Equal(FileTypeColors.ForExtension(".PNG"), FileTypeColors.ForExtension(".png"));
    }

    [Fact]
    public void Unknown_extensions_get_a_deterministic_colour()
    {
        // Derived from the extension itself, so a colour is the same on every machine
        // and in every session rather than depending on hash seeding.
        var first = FileTypeColors.ForExtension(".zzqq");
        var second = FileTypeColors.ForExtension(".zzqq");
        Assert.Equal(first, second);
        Assert.NotEqual(first, FileTypeColors.ForExtension(".qqzz"));
    }

    [Fact]
    public void Every_colour_is_fully_opaque()
    {
        foreach (var ext in new[] { ".mp4", ".png", ".dll", ".unknownext", "" })
            Assert.Equal(0xFFu, FileTypeColors.ForExtension(ext) >> 24);
    }

    [Fact]
    public void Related_types_stay_within_one_family()
    {
        // Video extensions should read as visibly related in the treemap: same dominant
        // channel, differing only in lightness.
        var mp4 = FileTypeColors.ForExtension(".mp4");
        var mkv = FileTypeColors.ForExtension(".mkv");

        static (int R, int G, int B) Rgb(uint c) => ((int)(c >> 16 & 0xFF), (int)(c >> 8 & 0xFF), (int)(c & 0xFF));

        var (r1, g1, b1) = Rgb(mp4);
        var (r2, g2, b2) = Rgb(mkv);

        Assert.True(r1 > g1 && r1 > b1, "video should be red-dominant");
        Assert.True(r2 > g2 && r2 > b2, "video should be red-dominant");
    }

    [Fact]
    public void Span_and_string_overloads_agree()
    {
        // The span overload exists to avoid allocating on the render hot path; it must
        // resolve to exactly the same colour as the string one, curated or derived.
        foreach (var ext in new[] { ".mp4", ".PNG", ".dll", ".zzqq", ".neverseenbefore", "" })
            Assert.Equal(FileTypeColors.ForExtension(ext), FileTypeColors.ForExtension(ext.AsSpan()));
    }

    [Fact]
    public void Extension_span_matches_the_interned_string()
    {
        var node = new Kartova.Core.Model.FileNode("Report.FINAL.Pdf");
        Assert.Equal(".pdf", node.Extension);
        Assert.True(node.ExtensionSpan.SequenceEqual(".Pdf"));
    }

    [Theory]
    [InlineData("noextension")]
    [InlineData(".gitignore")]   // a leading dot is a hidden file, not an extension
    [InlineData("trailing.")]
    public void Names_without_a_real_extension_report_none(string name)
    {
        var node = new Kartova.Core.Model.FileNode(name);
        Assert.Equal(string.Empty, node.Extension);
        Assert.True(node.ExtensionSpan.IsEmpty);
    }

    [Fact]
    public void Hsl_conversion_produces_expected_primaries()
    {
        Assert.Equal(0xFFFF0000, FileTypeColors.FromHsl(0, 1, 0.5));
        Assert.Equal(0xFF00FF00, FileTypeColors.FromHsl(120, 1, 0.5));
        Assert.Equal(0xFF0000FF, FileTypeColors.FromHsl(240, 1, 0.5));
        Assert.Equal(0xFFFFFFFF, FileTypeColors.FromHsl(0, 0, 1));
    }
}
