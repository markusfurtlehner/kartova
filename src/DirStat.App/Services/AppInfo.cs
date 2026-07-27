using System.Reflection;
using System.Runtime.InteropServices;

namespace DirStat.App.Services;

/// <summary>
/// Who wrote DirStat, which build this is, and what it is running on.
/// </summary>
/// <remarks>
/// The system facts are here rather than gathered at the point of display because they are
/// what someone pastes into a bug report. Version, runtime and architecture together explain
/// most "it behaves differently on my machine" reports before a single question is asked.
/// </remarks>
public static class AppInfo
{
    public const string Name = "DirStat";
    public const string Author = "Markus Furtlehner";
    public const string Licence = "MIT";

    /// <summary>Three-part version, without the assembly's trailing revision field.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string Copyright { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? $"Copyright (c) 2026 {Author}";

    /// <summary>What the app is built on, for the credits line.</summary>
    public static string Framework { get; } = $".NET {Environment.Version.ToString(2)} · Avalonia UI";

    /// <summary>Operating system and processor architecture, as one line.</summary>
    public static string System { get; } =
        $"{RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";

    /// <summary>
    /// Everything above as plain text, for pasting into an issue.
    /// </summary>
    public static string Details =>
        $"""
         {Name} {Version}
         {Copyright}
         Licence:   {Licence}
         Runtime:   {Framework}
         System:    {System}
         Processors: {Environment.ProcessorCount}
         """;
}
