using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Kartova.App.Services;

/// <summary>Outcome of a shell operation, carrying a message the UI can show verbatim.</summary>
public readonly record struct ShellResult(bool Success, string? Message)
{
    public static ShellResult Ok() => new(true, null);
    public static ShellResult Failed(string message) => new(false, message);
}

/// <summary>
/// Opening, revealing and deleting files through each platform's native conventions.
/// </summary>
/// <remarks>
/// Deletion moves to the platform recycle bin by default. On Windows that is the shell's
/// own undo-capable delete; on macOS and Linux it is an explicit move into the user's trash
/// folder rather than a scripted Finder call, which avoids triggering an automation
/// permission prompt just to delete a file.
/// </remarks>
public static class ShellService
{
    /// <summary>Opens a file or folder with the user's default handler.</summary>
    public static ShellResult Open(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return ShellResult.Failed($"No longer exists: {path}");

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
            else if (OperatingSystem.IsMacOS())
            {
                Launch("open", [path]);
            }
            else
            {
                Launch("xdg-open", [path]);
            }

            return ShellResult.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ShellResult.Failed($"Could not open: {e.Message}");
        }
    }

    /// <summary>Opens a web address in the default browser.</summary>
    /// <remarks>
    /// Separate from <see cref="Open"/> because that one requires the target to exist on disk.
    /// Only http and https are honoured: handing an arbitrary scheme to the shell is how a
    /// string turns into a launched program, and nothing here needs more than the web.
    /// </remarks>
    public static ShellResult OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ShellResult.Failed($"Not a web address: {url}");
        }

        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            else if (OperatingSystem.IsMacOS())
                Launch("open", [uri.AbsoluteUri]);
            else
                Launch("xdg-open", [uri.AbsoluteUri]);

            return ShellResult.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ShellResult.Failed($"Could not open: {e.Message}");
        }
    }

    /// <summary>Shows the item in the platform file manager, selected where possible.</summary>
    public static ShellResult Reveal(string path)
    {
        try
        {
            var exists = File.Exists(path) || Directory.Exists(path);
            if (!exists)
            {
                // Fall back to the parent so the user still lands somewhere useful.
                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                    return ShellResult.Failed($"No longer exists: {path}");
                path = parent;
            }

            if (OperatingSystem.IsWindows())
            {
                // /select needs the argument quoted and comma-separated, with no space.
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                })?.Dispose();
            }
            else if (OperatingSystem.IsMacOS())
            {
                Launch("open", ["-R", path]);
            }
            else
            {
                // The freedesktop file-manager interface selects the item; not every desktop
                // implements it, so fall back to simply opening the containing folder.
                if (!TryLaunch("dbus-send",
                    [
                        "--session", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                        "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                        $"array:string:{new Uri(path).AbsoluteUri}", "string:\"\"",
                    ]))
                {
                    var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder)) Launch("xdg-open", [folder]);
                }
            }

            return ShellResult.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ShellResult.Failed($"Could not reveal: {e.Message}");
        }
    }

    /// <summary>Moves a file or directory to the platform trash.</summary>
    public static ShellResult MoveToTrash(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return ShellResult.Failed($"No longer exists: {path}");

            if (OperatingSystem.IsWindows()) return WindowsRecycle(path);
            if (OperatingSystem.IsMacOS()) return MoveIntoTrashFolder(path, MacTrashDirectory());
            return LinuxTrash(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ShellResult.Failed($"Could not move to trash: {e.Message}");
        }
    }

    /// <summary>Deletes a file or directory outright, bypassing the trash.</summary>
    public static ShellResult DeletePermanently(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
            else return ShellResult.Failed($"No longer exists: {path}");

            return ShellResult.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ShellResult.Failed($"Could not delete: {e.Message}");
        }
    }

    /// <summary>Opens a terminal in the given directory.</summary>
    public static ShellResult OpenTerminal(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                directory = Path.GetDirectoryName(directory) ?? directory;
            if (!Directory.Exists(directory)) return ShellResult.Failed("Directory no longer exists.");

            if (OperatingSystem.IsWindows())
            {
                // Windows Terminal when present, otherwise the classic console host.
                if (!TryLaunch("wt.exe", ["-d", directory]))
                {
                    Process.Start(new ProcessStartInfo("cmd.exe")
                    {
                        WorkingDirectory = directory,
                        UseShellExecute = true,
                    })?.Dispose();
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                Launch("open", ["-a", "Terminal", directory]);
            }
            else
            {
                foreach (var terminal in new[] { "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal", "xterm" })
                    if (TryLaunch(terminal, [], directory))
                        return ShellResult.Ok();

                return ShellResult.Failed("No terminal emulator found.");
            }

            return ShellResult.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ShellResult.Failed($"Could not open a terminal: {e.Message}");
        }
    }

    // ------------------------------------------------------------- Windows

    private static ShellResult WindowsRecycle(string path)
    {
        // pFrom is a double-null-terminated list of paths.
        var operation = new Win32.ShFileOpStruct
        {
            wFunc = Win32.FoDelete,
            pFrom = path + '\0' + '\0',
            fFlags = Win32.FofAllowUndo | Win32.FofNoConfirmation |
                     Win32.FofNoErrorUi | Win32.FofSilent,
        };

        var code = Win32.SHFileOperationW(ref operation);
        if (code != 0) return ShellResult.Failed($"The shell refused the delete (code {code}).");
        if (operation.fAnyOperationsAborted) return ShellResult.Failed("The delete was aborted.");
        return ShellResult.Ok();
    }

    private static class Win32
    {
        public const uint FoDelete = 0x0003;
        public const ushort FofSilent = 0x0004;
        public const ushort FofNoConfirmation = 0x0010;
        public const ushort FofAllowUndo = 0x0040;
        public const ushort FofNoErrorUi = 0x0400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
        public struct ShFileOpStruct
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int SHFileOperationW(ref ShFileOpStruct fileOp);
    }

    // --------------------------------------------------------------- macOS

    private static string MacTrashDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");

    // --------------------------------------------------------------- Linux

    /// <summary>
    /// Implements the freedesktop.org trash specification: the item moves into
    /// <c>Trash/files</c> and a matching <c>.trashinfo</c> record is written so the desktop
    /// can restore it later.
    /// </summary>
    private static ShellResult LinuxTrash(string path)
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome))
            dataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        var trash = Path.Combine(dataHome, "Trash");
        var filesDir = Path.Combine(trash, "files");
        var infoDir = Path.Combine(trash, "info");
        Directory.CreateDirectory(filesDir);
        Directory.CreateDirectory(infoDir);

        var name = UniqueTrashName(filesDir, infoDir, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));

        // The info record must exist before the move, so a crash cannot orphan the file.
        var info = new StringBuilder()
            .AppendLine("[Trash Info]")
            .AppendLine($"Path={Uri.EscapeDataString(Path.GetFullPath(path)).Replace("%2F", "/")}")
            .AppendLine($"DeletionDate={DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}")
            .ToString();

        var infoPath = Path.Combine(infoDir, name + ".trashinfo");
        File.WriteAllText(infoPath, info);

        try
        {
            MoveInto(path, Path.Combine(filesDir, name));
            return ShellResult.Ok();
        }
        catch
        {
            try { File.Delete(infoPath); } catch { /* leave no dangling record */ }
            throw;
        }
    }

    private static ShellResult MoveIntoTrashFolder(string path, string trashDirectory)
    {
        Directory.CreateDirectory(trashDirectory);
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var target = Path.Combine(trashDirectory, name);

        // Trash already holds something by that name: disambiguate rather than clobber.
        var attempt = 1;
        while (File.Exists(target) || Directory.Exists(target))
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            target = Path.Combine(trashDirectory, $"{stem} {++attempt}{extension}");
        }

        MoveInto(path, target);
        return ShellResult.Ok();
    }

    private static string UniqueTrashName(string filesDir, string infoDir, string name)
    {
        var candidate = name;
        var attempt = 1;
        while (File.Exists(Path.Combine(filesDir, candidate)) ||
               Directory.Exists(Path.Combine(filesDir, candidate)) ||
               File.Exists(Path.Combine(infoDir, candidate + ".trashinfo")))
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            candidate = $"{stem}.{++attempt}{extension}";
        }
        return candidate;
    }

    /// <summary>
    /// Moves a file or directory, falling back to copy-then-delete when the destination is
    /// on a different filesystem, which a plain rename cannot cross.
    /// </summary>
    private static void MoveInto(string source, string destination)
    {
        try
        {
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination);
        }
        catch (IOException)
        {
            if (Directory.Exists(source))
            {
                CopyDirectory(source, destination);
                Directory.Delete(source, recursive: true);
            }
            else
            {
                File.Copy(source, destination, overwrite: false);
                File.Delete(source);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    // ------------------------------------------------------------ process

    private static void Launch(string fileName, string[] arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Process.Start(info)?.Dispose();
    }

    private static bool TryLaunch(string fileName, string[] arguments, string? workingDirectory = null)
    {
        try
        {
            Launch(fileName, arguments, workingDirectory);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or PlatformNotSupportedException)
        {
            return false; // not installed on this system
        }
    }
}
