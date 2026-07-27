using System.Collections.Concurrent;

namespace DirStat.Core.Files;

/// <summary>
/// Stable colours for file types, shared by the treemap and the extension list.
/// </summary>
/// <remarks>
/// Common types get hand-picked colours grouped by family, so a treemap reads at a glance:
/// video is warm red, images violet, code teal, archives amber. Anything unrecognised falls
/// back to a hue derived from the extension itself, which keeps colours stable across runs
/// and machines while spreading neighbouring hues apart via the golden-ratio increment.
/// Saturation and lightness stay inside a fixed band so no file type can render as mud or neon.
/// </remarks>
public static class FileTypeColors
{
    private static readonly ConcurrentDictionary<string, uint> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Family anchors. Every curated extension resolves to a shade of one of these.
    private const uint Video = 0xFFFF6B6B;
    private const uint Image = 0xFFB07CFF;
    private const uint Audio = 0xFFFF7AC8;
    private const uint Archive = 0xFFFFB24C;
    private const uint Document = 0xFF4C8DFF;
    private const uint Code = 0xFF37D6B0;
    private const uint Executable = 0xFF5AC8FA;
    private const uint Database = 0xFF8B7CFF;
    private const uint Font = 0xFFFF8FA3;
    private const uint DiskImage = 0xFFC49A6C;
    private const uint Systemish = 0xFF8A93A6;
    private const uint Web = 0xFF7BE38B;

    private static readonly Dictionary<string, uint> Curated = BuildCurated();

    /// <summary>Packed 0xAARRGGBB colour for an extension, including the leading dot.</summary>
    public static uint ForExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return Systemish;
        return Cache.GetOrAdd(extension, static ext =>
            Curated.TryGetValue(ext, out var known) ? known : Derive(ext));
    }

    /// <summary>Colour for a synthetic free-space node.</summary>
    public const uint FreeSpace = 0xFF2A3142;

    /// <summary>Colour for a synthetic unknown-space node.</summary>
    public const uint UnknownSpace = 0xFF4A5164;

    /// <summary>Colour for a directory when drawn as a solid block.</summary>
    public const uint Directory = 0xFF3C4459;

    private static Dictionary<string, uint> BuildCurated()
    {
        var map = new Dictionary<string, uint>(256, StringComparer.OrdinalIgnoreCase);

        void Add(uint baseColor, params string[] extensions)
        {
            // Spread each family across a small lightness ramp so neighbouring types in the
            // same family stay distinguishable without leaving the family's hue.
            for (var i = 0; i < extensions.Length; i++)
            {
                var t = extensions.Length == 1 ? 0.0 : (i / (double)(extensions.Length - 1) - 0.5) * 0.34;
                map[extensions[i]] = Shade(baseColor, t);
            }
        }

        Add(Video, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg",
                   ".mpeg", ".m2ts", ".ts", ".vob", ".3gp", ".ogv", ".rmvb", ".divx");

        Add(Image, ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp", ".svg",
                   ".ico", ".heic", ".heif", ".raw", ".cr2", ".nef", ".arw", ".dng", ".psd",
                   ".xcf", ".ai", ".eps", ".avif");

        Add(Audio, ".mp3", ".flac", ".wav", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".aiff",
                   ".alac", ".ape", ".mid", ".midi", ".amr");

        Add(Archive, ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".zst", ".lz4",
                     ".cab", ".arj", ".lzh", ".tgz", ".tbz", ".pkg", ".deb", ".rpm", ".apk",
                     ".jar", ".war", ".nupkg", ".whl", ".crx", ".xpi");

        Add(Document, ".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt", ".md", ".tex", ".epub",
                      ".mobi", ".azw3", ".djvu", ".pages", ".xls", ".xlsx", ".ods", ".csv",
                      ".tsv", ".ppt", ".pptx", ".odp", ".key", ".numbers", ".one");

        Add(Code, ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".cs", ".java", ".kt", ".swift",
                  ".py", ".rb", ".go", ".rs", ".php", ".pl", ".lua", ".r", ".jl", ".scala",
                  ".clj", ".ex", ".exs", ".erl", ".hs", ".ml", ".fs", ".fsx", ".vb", ".asm",
                  ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".bat", ".cmd", ".make",
                  ".cmake", ".gradle", ".sql", ".proto", ".graphql", ".ipynb");

        Add(Web, ".html", ".htm", ".css", ".scss", ".sass", ".less", ".js", ".mjs", ".cjs",
                 ".jsx", ".ts", ".tsx", ".vue", ".svelte", ".json", ".jsonc", ".xml", ".yaml",
                 ".yml", ".toml", ".ini", ".cfg", ".conf", ".env", ".lock", ".wasm");

        Add(Executable, ".exe", ".dll", ".so", ".dylib", ".app", ".msi", ".msix", ".appx",
                        ".com", ".bin", ".o", ".obj", ".lib", ".a", ".pdb", ".elf", ".ko",
                        ".sys", ".drv", ".ocx", ".pyd", ".node");

        Add(Database, ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb", ".mdf", ".ldf", ".frm",
                      ".ibd", ".dbf", ".realm", ".parquet", ".avro", ".orc", ".arrow");

        Add(Font, ".ttf", ".otf", ".woff", ".woff2", ".eot", ".fon", ".pfb", ".ttc");

        Add(DiskImage, ".iso", ".img", ".dmg", ".vhd", ".vhdx", ".vmdk", ".vdi", ".qcow2",
                       ".wim", ".esd", ".nrg", ".mds", ".cue", ".bin_cd");

        Add(Systemish, ".log", ".tmp", ".temp", ".bak", ".old", ".cache", ".swp", ".swo",
                       ".dmp", ".etl", ".evtx", ".crash", ".DS_Store", ".thumbs", ".lnk",
                       ".url", ".desktop", ".pid", ".sock");

        return map;
    }

    /// <summary>
    /// Derives a stable, pleasant colour from an unrecognised extension.
    /// </summary>
    private static uint Derive(string extension)
    {
        // FNV-1a: cheap, stable across processes and platforms, unlike string.GetHashCode.
        uint hash = 2166136261;
        foreach (var ch in extension)
        {
            hash ^= char.ToLowerInvariant(ch);
            hash *= 16777619;
        }

        // Golden-ratio conjugate scatters consecutive hashes to distant hues.
        var hue = (hash % 3600) / 3600.0 * 360.0;
        hue = (hue + 137.50776) % 360.0;

        // Vary saturation and lightness slightly so same-hue collisions still differ.
        var saturation = 0.52 + ((hash >> 8) % 100) / 100.0 * 0.22;
        var lightness = 0.56 + ((hash >> 16) % 100) / 100.0 * 0.14;

        return FromHsl(hue, saturation, lightness);
    }

    /// <summary>Lightens (t &gt; 0) or darkens (t &lt; 0) a packed colour, preserving hue.</summary>
    private static uint Shade(uint color, double t)
    {
        var a = (color >> 24) & 0xFF;
        var r = (color >> 16) & 0xFF;
        var g = (color >> 8) & 0xFF;
        var b = color & 0xFF;

        double Adjust(uint channel) => t >= 0
            ? channel + (255 - channel) * t
            : channel * (1 + t);

        return (a << 24)
             | ((uint)Math.Clamp(Adjust(r), 0, 255) << 16)
             | ((uint)Math.Clamp(Adjust(g), 0, 255) << 8)
             | (uint)Math.Clamp(Adjust(b), 0, 255);
    }

    /// <summary>Converts HSL to a packed opaque 0xAARRGGBB colour.</summary>
    public static uint FromHsl(double hueDegrees, double saturation, double lightness)
    {
        var h = (hueDegrees % 360 + 360) % 360 / 360.0;
        var s = Math.Clamp(saturation, 0, 1);
        var l = Math.Clamp(lightness, 0, 1);

        if (s <= 0)
        {
            var grey = (uint)Math.Round(l * 255);
            return 0xFF000000u | (grey << 16) | (grey << 8) | grey;
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        var r = HueToChannel(p, q, h + 1.0 / 3.0);
        var g = HueToChannel(p, q, h);
        var b = HueToChannel(p, q, h - 1.0 / 3.0);

        return 0xFF000000u
             | ((uint)Math.Round(r * 255) << 16)
             | ((uint)Math.Round(g * 255) << 8)
             | (uint)Math.Round(b * 255);
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }
}
