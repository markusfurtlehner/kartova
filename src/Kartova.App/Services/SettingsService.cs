using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kartova.App.Services;

public enum AppTheme
{
    Dark,
    Light,
    System,
}

/// <summary>User preferences, persisted between sessions.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>
    /// Interface language. Null means follow the operating system, which is what a first run
    /// should do rather than assuming English.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>Report sizes in KiB/MiB/GiB rather than KB/MB/GB.</summary>
    public bool UseBinaryUnits { get; set; } = true;

    /// <summary>Show sizes as allocated on disk rather than logical length.</summary>
    public bool ShowSizeOnDisk { get; set; }

    public bool ShowFreeSpace { get; set; } = true;
    public bool SkipHidden { get; set; }
    public bool DetectHardLinks { get; set; }
    public bool ExactAllocation { get; set; }

    public bool CushionShading { get; set; } = true;
    public bool DirectoryFrames { get; set; } = true;

    /// <summary>Directory names skipped wherever they occur, such as <c>node_modules</c>.</summary>
    public List<string> ExcludedDirectoryNames { get; set; } = [];

    /// <summary>Most recently scanned paths, newest first.</summary>
    public List<string> RecentPaths { get; set; } = [];

    public double WindowWidth { get; set; } = 1480;
    public double WindowHeight { get; set; } = 940;
    public bool WindowMaximized { get; set; }

    /// <summary>Records a scan target, keeping the list short and duplicate-free.</summary>
    public void RememberPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RecentPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentPaths.Insert(0, path);
        if (RecentPaths.Count > 12) RecentPaths.RemoveRange(12, RecentPaths.Count - 12);
    }
}

/// <summary>
/// Source-generated JSON context.
/// </summary>
/// <remarks>
/// Reflection-based serialization does not survive trimming, and the app ships trimmed.
/// Generating the contract at compile time keeps settings working in the published binary.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Loads and saves <see cref="AppSettings"/> from the platform config directory.</summary>
public static class SettingsService
{
    private static string ConfigDirectory
    {
        get
        {
            // Maps to %APPDATA% on Windows, ~/Library/Application Support on macOS,
            // and $XDG_CONFIG_HOME (or ~/.config) on Linux.
            var root = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create);

            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

            return Path.Combine(root, "Kartova");
        }
    }

    private static string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return new AppSettings();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt or unreadable settings must never stop the app from starting.
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

            // Write to a sibling then swap, so a crash mid-write cannot truncate the real file.
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience; failing to persist them is not worth interrupting for.
        }
    }
}
