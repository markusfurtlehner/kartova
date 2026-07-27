using System.Globalization;
using System.Runtime.InteropServices;
using Kartova.App.Services;
using Kartova.Core.Duplicates;
using Kartova.Core.Files;
using Kartova.Core.Insights;
using Kartova.Core.Model;
using Kartova.Core.Scanning;
using Kartova.Core.Snapshots;

namespace Kartova.App.Cli;

/// <summary>
/// Headless operation: scan, export, find duplicates and inspect snapshots without a window.
/// </summary>
/// <remarks>
/// <para>
/// Everything here drives the same Core engines the interface uses, so the numbers a script
/// gets are the numbers a person would see. Nothing in this path touches Avalonia, which is
/// what lets it run on a build agent or over SSH with no display at all.
/// </para>
/// <para>
/// The executable is a GUI subsystem binary on Windows, so it owns no console when launched
/// from one. <see cref="AttachConsole"/> borrows the parent's before any output, otherwise a
/// command would appear to run and print nothing.
/// </para>
/// </remarks>
public static class CommandLine
{
    public const int ExitSuccess = 0;
    public const int ExitUsage = 64;
    public const int ExitFailure = 1;

    /// <summary>True when the arguments ask for headless work rather than a window.</summary>
    public static bool WantsHeadless(string[] args) =>
        args.Any(a => a is "--help" or "-h" or "--version" or "--scan" or "--duplicates"
                          or "--insights" or "--snapshot" or "--compare" or "--list-snapshots");

    public static int Run(string[] args)
    {
        AttachConsole();

        try
        {
            return Dispatch(args);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"kartova: {e.Message}");
            return ExitFailure;
        }
    }

    private static int Dispatch(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h")) { PrintUsage(); return ExitSuccess; }

        if (args.Contains("--version"))
        {
            Console.WriteLine($"Kartova {typeof(CommandLine).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}");
            return ExitSuccess;
        }

        if (TryGetValue(args, "--list-snapshots", out var snapshotDirectory))
            return ListSnapshots(snapshotDirectory ?? SnapshotStore.DefaultDirectory);

        if (TryGetValue(args, "--compare", out var comparePair))
            return Compare(comparePair, args);

        if (!TryGetValue(args, "--scan", out var target) || string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("kartova: --scan <path> is required. Try --help.");
            return ExitUsage;
        }

        if (!Directory.Exists(target))
        {
            Console.Error.WriteLine($"kartova: not a directory: {target}");
            return ExitFailure;
        }

        var quiet = args.Contains("--quiet");
        var result = Scan(target, args, quiet);

        if (TryGetValue(args, "--export", out var exportPath) && !string.IsNullOrEmpty(exportPath))
        {
            var json = exportPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (json) ExportService.ExportJson(result.Root, exportPath);
            else ExportService.ExportCsv(result.Root, exportPath);
            if (!quiet) Console.WriteLine($"exported {(json ? "JSON" : "CSV")} to {exportPath}");
        }

        if (TryGetValue(args, "--snapshot", out var snapshotPath))
        {
            var path = string.IsNullOrEmpty(snapshotPath)
                ? SnapshotStore.SuggestPath(target)
                : snapshotPath;

            SnapshotStore.EnsureDirectory(Path.GetDirectoryName(path));
            SnapshotFile.Save(result.Root, path);
            if (!quiet) Console.WriteLine($"snapshot saved to {path}");
        }

        if (args.Contains("--duplicates")) ReportDuplicates(result.Root, args);
        if (args.Contains("--insights")) ReportInsights(result.Root);

        return ExitSuccess;
    }

    private static ScanResult Scan(string target, string[] args, bool quiet)
    {
        var options = new ScanOptions { IncludeFreeSpace = false };

        if (TryGetValue(args, "--exclude", out var excluded) && !string.IsNullOrEmpty(excluded))
            foreach (var name in excluded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                options.ExcludedDirectoryNames.Add(name);

        var result = new DirectoryScanner().Scan([target], options);

        if (!quiet)
        {
            Console.WriteLine($"{target}");
            Console.WriteLine($"  size        {SizeFormatter.Format(result.TotalBytes)}");
            Console.WriteLine($"  files       {result.TotalFiles:N0}");
            Console.WriteLine($"  folders     {result.TotalDirectories:N0}");
            Console.WriteLine($"  duration    {SizeFormatter.FormatDuration(result.Duration)}");
            if (result.DeniedPaths.Count > 0)
                Console.WriteLine($"  unreadable  {result.DeniedPaths.Count:N0} folders");
            Console.WriteLine();
        }

        return result;
    }

    private static void ReportDuplicates(FileNode root, string[] args)
    {
        var minimum = TryGetValue(args, "--min-size", out var raw) && long.TryParse(raw, out var parsed)
            ? parsed
            : 4096;

        var result = new DuplicateFinder().Find(root, new DuplicateOptions { MinimumFileSize = minimum });

        Console.WriteLine("duplicates");
        Console.WriteLine($"  recoverable {SizeFormatter.Format(result.WastedInFiles + result.WastedInFolders)}");
        Console.WriteLine($"  file groups {result.FileGroups.Count:N0}");
        Console.WriteLine($"  folder sets {result.FolderGroups.Count:N0}");
        Console.WriteLine();

        foreach (var group in result.FolderGroups.Take(20))
        {
            Console.WriteLine($"  [folder] {SizeFormatter.Format(group.WastedBytes),12}  {group.CopyCount} copies  {group.DisplayName}");
            foreach (var item in group.Items) Console.WriteLine($"             {item.GetFullPath()}");
        }

        foreach (var group in result.FileGroups.Take(40))
        {
            Console.WriteLine($"  [file]   {SizeFormatter.Format(group.WastedBytes),12}  {group.CopyCount} copies  {group.DisplayName}");
            foreach (var item in group.Items) Console.WriteLine($"             {item.GetFullPath()}");
        }
    }

    private static void ReportInsights(FileNode root)
    {
        var result = InsightAnalyzer.Analyze(root);

        Console.WriteLine("insights");
        foreach (var group in result.Groups)
        {
            var label = group.Category?.Id ?? group.Kind.ToString();
            Console.WriteLine($"  {label,-24} {SizeFormatter.Format(group.TotalBytes),12}  {group.Count:N0} items");
        }
        Console.WriteLine();
    }

    private static int ListSnapshots(string directory)
    {
        var snapshots = SnapshotFile.List(directory);
        if (snapshots.Count == 0)
        {
            Console.WriteLine($"no snapshots in {directory}");
            return ExitSuccess;
        }

        foreach (var snapshot in snapshots)
        {
            Console.WriteLine(
                $"{snapshot.TakenUtc.ToLocalTime():yyyy-MM-dd HH:mm}  " +
                $"{SizeFormatter.Format(snapshot.TotalBytes),12}  " +
                $"{snapshot.TotalFiles,10:N0} files  {snapshot.RootPath}");
        }

        return ExitSuccess;
    }

    private static int Compare(string? pair, string[] args)
    {
        // --compare old.kartova,new.kartova  or  --compare old.kartova --scan <path>
        var parts = (pair ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            Console.Error.WriteLine("kartova: --compare needs a snapshot, and either a second snapshot or --scan.");
            return ExitUsage;
        }

        var before = SnapshotFile.Load(parts[0]);
        if (before is null)
        {
            Console.Error.WriteLine($"kartova: could not read snapshot {parts[0]}");
            return ExitFailure;
        }

        FileNode after;
        if (parts.Length > 1)
        {
            var second = SnapshotFile.Load(parts[1]);
            if (second is null)
            {
                Console.Error.WriteLine($"kartova: could not read snapshot {parts[1]}");
                return ExitFailure;
            }
            after = second.Root;
        }
        else if (TryGetValue(args, "--scan", out var target) && !string.IsNullOrEmpty(target))
        {
            after = new DirectoryScanner().Scan([target], new ScanOptions { IncludeFreeSpace = false }).Root;
        }
        else
        {
            Console.Error.WriteLine("kartova: --compare needs a second snapshot or --scan <path>.");
            return ExitUsage;
        }

        var comparison = TreeComparer.Compare(before.Root, after);

        Console.WriteLine("comparison");
        Console.WriteLine($"  before      {SizeFormatter.Format(comparison.OldTotal)}");
        Console.WriteLine($"  after       {SizeFormatter.Format(comparison.NewTotal)}");
        Console.WriteLine($"  change      {(comparison.Delta >= 0 ? "+" : "-")}{SizeFormatter.Format(Math.Abs(comparison.Delta))}");
        Console.WriteLine();

        foreach (var change in comparison.Changes.Take(40))
        {
            var sign = change.Delta >= 0 ? "+" : "-";
            Console.WriteLine(
                $"  {change.Kind,-8} {sign}{SizeFormatter.Format(Math.Abs(change.Delta)),12}  {change.GetFullPath()}");
        }

        return ExitSuccess;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Reads <c>--flag value</c>, or <c>--flag</c> alone with a null value.</summary>
    private static bool TryGetValue(string[] args, string flag, out string? value)
    {
        value = null;
        var index = Array.IndexOf(args, flag);
        if (index < 0) return false;

        if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            value = args[index + 1];

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Kartova — disk usage analyser

            USAGE
              kartova                                  open the window
              kartova <path>                           open the window and scan a path
              kartova --scan <path> [options]          scan without a window

            OPTIONS
              --scan <path>            directory to scan
              --export <file>          write results; .json for JSON, anything else CSV
              --snapshot [file]        store the scan for later comparison
              --duplicates             also report duplicate files and folders
              --insights               also report stale files, empty folders and junk
              --min-size <bytes>       smallest file the duplicate search considers
              --exclude a,b,c          directory names to skip
              --quiet                  print only what was asked for

              --list-snapshots [dir]   list stored snapshots
              --compare a[,b]          compare two snapshots, or one against --scan

              -h, --help               this text
              --version                version and exit

            EXAMPLES
              kartova --scan /home/me --duplicates
              kartova --scan C:\\ --snapshot --quiet
              kartova --compare monday.kartova --scan C:\\
            """);
    }

    /// <summary>
    /// Borrows the parent process's console on Windows.
    /// </summary>
    /// <remarks>
    /// The app is built as a GUI binary so launching it normally does not flash a console
    /// window. The cost is that it owns no console when started from one, and every write
    /// would go nowhere; attaching to the parent restores ordinary command-line behaviour.
    /// </remarks>
    private static void AttachConsole()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            const int attachParentProcess = -1;
            if (!NativeConsole.AttachConsole(attachParentProcess)) return;

            // Rebind the streams: the ones captured at start-up point at the void.
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EntryPointNotFoundException)
        {
            // No console to attach to; output simply goes nowhere, which is the status quo.
        }
    }

    private static class NativeConsole
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachConsole(int processId);
    }
}

/// <summary>Where snapshots live by default, shared by the CLI and the interface.</summary>
public static class SnapshotStore
{
    public static string DefaultDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);

            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

            return Path.Combine(root, "Kartova", "snapshots");
        }
    }

    /// <summary>A filename derived from what was scanned and when.</summary>
    public static string SuggestPath(string scannedPath)
    {
        var name = scannedPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(':', '-');

        var leaf = Path.GetFileName(name);
        if (string.IsNullOrEmpty(leaf)) leaf = name.Trim(Path.DirectorySeparatorChar, '-');
        if (string.IsNullOrEmpty(leaf)) leaf = "scan";

        foreach (var invalid in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(invalid, '-');

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(DefaultDirectory, $"{leaf}-{stamp}{SnapshotFile.Extension}");
    }

    public static void EnsureDirectory(string? directory)
    {
        var target = string.IsNullOrEmpty(directory) ? DefaultDirectory : directory;
        try { Directory.CreateDirectory(target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
