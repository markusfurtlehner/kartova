using System.Text.RegularExpressions;
using DirStat.App.Localization;
using Xunit;

namespace DirStat.Core.Tests;

public class LocalizationTests
{
    public static TheoryData<string, IReadOnlyDictionary<string, string>> Translations => new()
    {
        { "German", Strings.German },
        { "French", Strings.French },
        { "Spanish", Strings.Spanish },
    };

    [Theory]
    [MemberData(nameof(Translations))]
    public void Every_language_defines_every_english_key(
        string language, IReadOnlyDictionary<string, string> catalogue)
    {
        var missing = Strings.English.Keys.Where(k => !catalogue.ContainsKey(k)).OrderBy(k => k).ToArray();

        Assert.True(missing.Length == 0,
            $"{language} is missing {missing.Length} keys:\n  {string.Join("\n  ", missing)}");
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void No_language_defines_keys_english_does_not(
        string language, IReadOnlyDictionary<string, string> catalogue)
    {
        // A stray key is usually a typo that leaves the real key untranslated everywhere.
        var extra = catalogue.Keys.Where(k => !Strings.English.ContainsKey(k)).OrderBy(k => k).ToArray();

        Assert.True(extra.Length == 0,
            $"{language} defines {extra.Length} keys English does not:\n  {string.Join("\n  ", extra)}");
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Placeholders_match_the_english_original(
        string language, IReadOnlyDictionary<string, string> catalogue)
    {
        // A translation that drops {1} throws at runtime, or worse, silently loses a number.
        var mismatches = new List<string>();

        foreach (var (key, english) in Strings.English)
        {
            if (!catalogue.TryGetValue(key, out var translated)) continue;

            var expected = Placeholders(english);
            var actual = Placeholders(translated);

            if (!expected.SetEquals(actual))
                mismatches.Add($"{key}: expected {{{string.Join(",", expected.Order())}}}, got {{{string.Join(",", actual.Order())}}}");
        }

        Assert.True(mismatches.Count == 0,
            $"{language} has {mismatches.Count} placeholder mismatches:\n  {string.Join("\n  ", mismatches)}");
    }

    private static HashSet<int> Placeholders(string template) =>
        Regex.Matches(template, @"\{(\d+)\}")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToHashSet();

    [Fact]
    public void No_translation_is_left_empty()
    {
        foreach (var (name, catalogue) in new[]
                 {
                     ("English", Strings.English), ("German", Strings.German),
                     ("French", Strings.French), ("Spanish", Strings.Spanish),
                 })
        {
            var blank = catalogue.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ToArray();
            Assert.True(blank.Length == 0, $"{name} has blank values for: {string.Join(", ", blank)}");
        }
    }

    [Fact]
    public void An_unknown_key_returns_itself_rather_than_blank_or_throwing()
    {
        // A missed string should be visible as an obvious label, not an empty gap.
        Assert.Equal("Nope.Missing.Key", Loc.Current["Nope.Missing.Key"]);
    }

    [Fact]
    public void Switching_language_changes_the_text()
    {
        var original = Loc.Current.Language;
        try
        {
            Loc.Current.Language = AppLanguage.English;
            var english = Loc.Current["Common.Cancel"];

            Loc.Current.Language = AppLanguage.German;
            var german = Loc.Current["Common.Cancel"];

            Assert.Equal("Cancel", english);
            Assert.Equal("Abbrechen", german);
            Assert.Equal("DE", Loc.Current.LanguageCode);
        }
        finally
        {
            Loc.Current.Language = original;
        }
    }

    [Fact]
    public void Format_survives_a_malformed_template()
    {
        // A bad placeholder must not take the UI down; the raw template is the safe answer.
        var result = Loc.Format("Nope.{unclosed", "x");
        Assert.False(string.IsNullOrEmpty(result));
    }
}
