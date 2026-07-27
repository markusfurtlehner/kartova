using System.ComponentModel;
using System.Globalization;

namespace DirStat.App.Localization;

public enum AppLanguage
{
    English,
    German,
    French,
    Spanish,
}

/// <summary>
/// The application's string catalogue, switchable at run time.
/// </summary>
/// <remarks>
/// <para>
/// Strings live in compiled dictionaries rather than in <c>.resx</c> satellite assemblies.
/// Satellites work, but they resolve through <see cref="CultureInfo.CurrentUICulture"/>, which
/// means changing language mid-session requires rebuilding every view — and they add a
/// per-language file to a build whose whole point is shipping one. Compiled tables are also
/// trim-safe by construction.
/// </para>
/// <para>
/// Views bind through the indexer, so raising a change for <c>Item[]</c> retranslates the
/// entire window in place the moment the user picks a language.
/// </para>
/// </remarks>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Current { get; } = new();

    private readonly Dictionary<string, TranslatedString> _bound = [];
    private IReadOnlyDictionary<string, string> _strings = Strings.English;
    private AppLanguage _language = AppLanguage.English;

    private Loc() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;

            _language = value;
            _strings = Catalogue(value);

            // Each bound key is its own observable object rather than an indexer read.
            // Indexer bindings do not reliably re-evaluate on an "Item[]" notification, and a
            // language switch that silently leaves the window in the old language is worse
            // than no switcher at all.
            foreach (var entry in _bound.Values) entry.Refresh();

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LanguageCode)));

            LanguageChanged?.Invoke();
        }
    }

    /// <summary>
    /// Returns the shared observable for a key, creating it on first use.
    /// </summary>
    /// <remarks>
    /// One instance per key, shared by every binding to it, so a language change notifies each
    /// key exactly once no matter how many places display it.
    /// </remarks>
    public static TranslatedString Get(string key)
    {
        lock (Current._bound)
        {
            if (Current._bound.TryGetValue(key, out var existing)) return existing;
            var created = new TranslatedString(key);
            Current._bound[key] = created;
            return created;
        }
    }

    /// <summary>Raised after a language switch, for text built in code rather than bound.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Two-letter code shown in the title bar selector.</summary>
    public string LanguageCode => _language switch
    {
        AppLanguage.German => "DE",
        AppLanguage.French => "FR",
        AppLanguage.Spanish => "ES",
        _ => "EN",
    };

    /// <summary>
    /// Looks up a key. An unknown key returns itself rather than throwing or blanking, so a
    /// missed string shows up as an obvious label instead of an empty gap or a crash.
    /// </summary>
    public string this[string key] =>
        _strings.TryGetValue(key, out var value) ? value
        : Strings.English.TryGetValue(key, out var fallback) ? fallback
        : key;

    /// <summary>Looks up a key and fills in positional arguments.</summary>
    public static string Format(string key, params object[] args)
    {
        var template = Current[key];
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // A malformed placeholder must not take the UI down with it.
            return template;
        }
    }

    /// <summary>Shorthand for a plain lookup from code.</summary>
    public static string T(string key) => Current[key];

    /// <summary>One translated string, observable so bindings refresh on a language change.</summary>
    public sealed class TranslatedString(string key) : INotifyPropertyChanged
    {
        public string Key { get; } = key;

        public string Value => Current[Key];

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void Refresh() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));

        public override string ToString() => Value;
    }

    private static IReadOnlyDictionary<string, string> Catalogue(AppLanguage language) => language switch
    {
        AppLanguage.German => Strings.German,
        AppLanguage.French => Strings.French,
        AppLanguage.Spanish => Strings.Spanish,
        _ => Strings.English,
    };

    /// <summary>Picks the closest catalogue to the operating system's language.</summary>
    public static AppLanguage DetectSystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "de" => AppLanguage.German,
            "fr" => AppLanguage.French,
            "es" => AppLanguage.Spanish,
            _ => AppLanguage.English,
        };
}
