namespace DirStat.Core.Model;

/// <summary>The outcome of a scan. Always usable, even when cancelled part way.</summary>
public sealed class ScanResult
{
    /// <summary>
    /// Synthetic parent of all scan roots when several were requested; otherwise the
    /// single root itself. Always safe to treat as the tree entry point.
    /// </summary>
    public required FileNode Root { get; init; }

    /// <summary>The roots the user actually asked for.</summary>
    public required IReadOnlyList<FileNode> Roots { get; init; }

    public required TimeSpan Duration { get; init; }
    public required long TotalFiles { get; init; }
    public required long TotalDirectories { get; init; }
    public required long TotalBytes { get; init; }

    /// <summary>Directories that could not be opened. Their contents are absent from the tree.</summary>
    public required IReadOnlyList<string> DeniedPaths { get; init; }

    /// <summary>True when the user cancelled; the tree holds whatever had been walked.</summary>
    public required bool WasCancelled { get; init; }

    /// <summary>Per-extension rollup, ordered by descending total size.</summary>
    public required IReadOnlyList<ExtensionStat> Extensions { get; init; }

    public DateTime CompletedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Aggregate figures for one file extension across the whole scan.</summary>
public sealed class ExtensionStat
{
    public required string Extension { get; init; }
    public required long TotalSize { get; init; }
    public required int FileCount { get; init; }

    /// <summary>Share of the scanned total, 0..1.</summary>
    public double Fraction { get; set; }

    /// <summary>Packed 0xAARRGGBB colour shared by the treemap and the extension list.</summary>
    public uint Color { get; set; }

    /// <summary>Label shown in the UI. Extensionless files are grouped under one entry.</summary>
    public string DisplayName =>
        Extension.Length == 0 ? "(no extension)" : Extension.ToUpperInvariant().TrimStart('.');
}
