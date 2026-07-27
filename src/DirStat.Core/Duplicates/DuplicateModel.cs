using DirStat.Core.Model;

namespace DirStat.Core.Duplicates;

/// <summary>Tunables for duplicate detection.</summary>
public sealed class DuplicateOptions
{
    /// <summary>
    /// Files smaller than this are ignored. Tiny files duplicate constantly and reclaim
    /// nothing, so the default keeps the results about space rather than about noise.
    /// </summary>
    public long MinimumFileSize { get; set; } = 4096;

    /// <summary>Also look for whole directories whose contents match.</summary>
    public bool FindDuplicateFolders { get; set; } = true;

    /// <summary>
    /// Compare candidates byte for byte after their hashes match. Costs a second full read
    /// of every confirmed duplicate; the 128-bit hash already makes an accidental collision
    /// vanishingly unlikely, so this is for the deliberately paranoid.
    /// </summary>
    public bool VerifyByteForByte { get; set; }

    /// <summary>Extensions never considered, each including the leading dot.</summary>
    public HashSet<string> IgnoredExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hashing is IO-bound. More threads help an SSD and hurt a spinning disk, so this stays
    /// modest by default rather than saturating the queue.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(80);
}

/// <summary>A set of files, or directories, with identical content.</summary>
public sealed class DuplicateGroup
{
    public required IReadOnlyList<FileNode> Items { get; init; }

    /// <summary>Size of a single copy.</summary>
    public required long ItemSize { get; init; }

    /// <summary>True when the members are directories rather than files.</summary>
    public required bool IsFolder { get; init; }

    /// <summary>Content signature, shown for diagnostics and used as a stable group key.</summary>
    public required string Signature { get; init; }

    public int CopyCount => Items.Count;

    /// <summary>Space recoverable by keeping one copy and removing the rest.</summary>
    public long WastedBytes => ItemSize * (Items.Count - 1);

    /// <summary>Name shown for the group, taken from the first member.</summary>
    public string DisplayName => Items.Count > 0 ? Items[0].Name : string.Empty;
}

/// <summary>Stage the search is in, so the UI can say something meaningful.</summary>
public enum DuplicatePhase
{
    Grouping,
    Screening,
    Hashing,
    MatchingFolders,
    Verifying,
    Complete,
}

/// <summary>An immutable snapshot of duplicate-search progress.</summary>
public readonly record struct DuplicateProgress(
    DuplicatePhase Phase,
    long CandidateFiles,
    long FilesHashed,
    long BytesHashed,
    long BytesToHash,
    int GroupsFound,
    long WastedBytes,
    string CurrentPath,
    TimeSpan Elapsed)
{
    /// <summary>Share of the hashing work completed, 0..1. Zero when nothing needs hashing.</summary>
    public double Fraction =>
        BytesToHash <= 0 ? 0 : Math.Clamp((double)BytesHashed / BytesToHash, 0, 1);

    public double BytesPerSecond =>
        Elapsed.TotalSeconds <= 0.001 ? 0 : BytesHashed / Elapsed.TotalSeconds;
}

/// <summary>The outcome of a duplicate search.</summary>
public sealed class DuplicateResult
{
    /// <summary>Duplicate files, largest recoverable space first.</summary>
    public required IReadOnlyList<DuplicateGroup> FileGroups { get; init; }

    /// <summary>
    /// Duplicate directories, largest first. A directory inside an already-reported duplicate
    /// directory is omitted, so the list names the biggest thing worth removing rather than
    /// every nested copy underneath it.
    /// </summary>
    public required IReadOnlyList<DuplicateGroup> FolderGroups { get; init; }

    public required long BytesHashed { get; init; }
    public required long FilesHashed { get; init; }
    public required TimeSpan Duration { get; init; }
    public required bool WasCancelled { get; init; }

    /// <summary>Space recoverable from duplicate files.</summary>
    public long WastedInFiles => FileGroups.Sum(g => g.WastedBytes);

    /// <summary>Space recoverable from duplicate folders.</summary>
    public long WastedInFolders => FolderGroups.Sum(g => g.WastedBytes);

    public static DuplicateResult Empty { get; } = new()
    {
        FileGroups = [],
        FolderGroups = [],
        BytesHashed = 0,
        FilesHashed = 0,
        Duration = TimeSpan.Zero,
        WasCancelled = false,
    };
}
