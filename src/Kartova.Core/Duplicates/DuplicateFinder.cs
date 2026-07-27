using System.Collections.Concurrent;
using System.Diagnostics;
using Kartova.Core.Model;
using Kartova.Core.Scanning;

namespace Kartova.Core.Duplicates;

/// <summary>
/// Finds files, and whole directories, whose contents are identical.
/// </summary>
/// <remarks>
/// <para>
/// Reading every byte on a volume would take far longer than the scan itself, so the search
/// is staged and each stage only sees what survived the last:
/// </para>
/// <list type="number">
/// <item>Group by size. Free, because the scan already measured every file, and two files of
/// different length cannot possibly match. This alone discards the overwhelming majority.</item>
/// <item>Hash a 16 KB prefix of what remains. Files that differ tend to differ early, so this
/// eliminates most same-size candidates for a fraction of a read.</item>
/// <item>Hash in full only the handful still indistinguishable.</item>
/// </list>
/// <para>
/// Directories are handled bottom-up: a directory's signature combines its children's names
/// and signatures, so two folders match exactly when their whole trees do. Candidates are
/// pre-filtered on size and item counts, which are already known, so full trees are only
/// hashed when they could plausibly match.
/// </para>
/// </remarks>
public sealed class DuplicateFinder
{
    private sealed class SearchState
    {
        public long CandidateFiles;
        public long FilesHashed;
        public long BytesHashed;
        public long BytesToHash;
        public int GroupsFound;
        public long WastedBytes;
        public volatile string CurrentPath = string.Empty;
        public DuplicatePhase Phase = DuplicatePhase.Grouping;
    }

    public Task<DuplicateResult> FindAsync(
        FileNode root,
        DuplicateOptions? options = null,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Find(root, options, progress, cancellationToken), CancellationToken.None);
    }

    public DuplicateResult Find(
        FileNode root,
        DuplicateOptions? options = null,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new DuplicateOptions();

        var state = new SearchState();
        var stopwatch = Stopwatch.StartNew();
        var cancelled = false;

        // Hashes are shared between the file and folder passes, so no file is read twice.
        var hashes = new ConcurrentDictionary<FileNode, ContentHash>();

        using var pump = StartProgressPump(state, stopwatch, progress, options.ProgressInterval);

        IReadOnlyList<DuplicateGroup> fileGroups = [];
        IReadOnlyList<DuplicateGroup> folderGroups = [];

        try
        {
            fileGroups = FindDuplicateFiles(root, options, state, hashes, cancellationToken);

            if (options.FindDuplicateFolders)
            {
                state.Phase = DuplicatePhase.MatchingFolders;
                folderGroups = FindDuplicateFolders(root, options, state, hashes, cancellationToken);
                fileGroups = SuppressFilesInsideDuplicateFolders(fileGroups, folderGroups);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        stopwatch.Stop();
        state.Phase = DuplicatePhase.Complete;
        progress?.Report(Snapshot(state, stopwatch.Elapsed));

        return new DuplicateResult
        {
            FileGroups = fileGroups,
            FolderGroups = folderGroups,
            BytesHashed = Interlocked.Read(ref state.BytesHashed),
            FilesHashed = Interlocked.Read(ref state.FilesHashed),
            Duration = stopwatch.Elapsed,
            WasCancelled = cancelled || cancellationToken.IsCancellationRequested,
        };
    }

    // ------------------------------------------------------------------ files

    private IReadOnlyList<DuplicateGroup> FindDuplicateFiles(
        FileNode root,
        DuplicateOptions options,
        SearchState state,
        ConcurrentDictionary<FileNode, ContentHash> hashes,
        CancellationToken cancellationToken)
    {
        // Stage 1: group by size. Costs nothing — the scan already knows every length.
        state.Phase = DuplicatePhase.Grouping;
        var bySize = new Dictionary<long, List<FileNode>>();

        foreach (var node in root.DescendantsAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsEligible(node, options)) continue;

            if (!bySize.TryGetValue(node.Size, out var bucket))
                bySize[node.Size] = bucket = new List<FileNode>(2);
            bucket.Add(node);
        }

        var candidates = bySize.Values.Where(b => b.Count > 1).ToList();
        Interlocked.Exchange(ref state.CandidateFiles, candidates.Sum(c => c.Count));

        if (candidates.Count == 0) return [];

        // Stage 2: screen on a prefix. Cheap, and it removes most of what stage 1 left.
        state.Phase = DuplicatePhase.Screening;
        var screened = Screen(candidates, options, state, cancellationToken);
        if (screened.Count == 0) return [];

        // Stage 3: full hashes, only for what is still ambiguous.
        state.Phase = DuplicatePhase.Hashing;
        Interlocked.Exchange(ref state.BytesToHash, screened.Sum(g => g.Sum(n => n.Size)));

        var confirmed = new ConcurrentBag<DuplicateGroup>();

        Parallel.ForEach(
            screened,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                CancellationToken = cancellationToken,
            },
            bucket =>
            {
                var byHash = new Dictionary<ContentHash, List<FileNode>>();

                foreach (var node in bucket)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = node.GetFullPath();
                    state.CurrentPath = path;

                    if (!TryHashFile(path, node, state, hashes, cancellationToken, out var hash)) continue;

                    if (!byHash.TryGetValue(hash, out var matches))
                        byHash[hash] = matches = new List<FileNode>(2);
                    matches.Add(node);
                }

                foreach (var (hash, rawMatches) in byHash)
                {
                    if (rawMatches.Count < 2) continue;

                    // Copies that are hard links to one another already share their bytes.
                    var matches = RemoveHardLinkAliases(rawMatches);
                    if (matches.Count < 2) continue;

                    var members = options.VerifyByteForByte
                        ? VerifyGroup(matches, cancellationToken)
                        : [matches];

                    foreach (var verified in members)
                    {
                        if (verified.Count < 2) continue;
                        var group = new DuplicateGroup
                        {
                            Items = OrderMembers(verified),
                            ItemSize = verified[0].Size,
                            IsFolder = false,
                            Signature = hash.ToString(),
                        };
                        confirmed.Add(group);
                        Interlocked.Increment(ref state.GroupsFound);
                        Interlocked.Add(ref state.WastedBytes, group.WastedBytes);
                    }
                }
            });

        return confirmed.OrderByDescending(g => g.WastedBytes).ToArray();
    }

    /// <summary>Splits size buckets by a prefix hash, keeping only those still colliding.</summary>
    private static List<List<FileNode>> Screen(
        List<List<FileNode>> candidates,
        DuplicateOptions options,
        SearchState state,
        CancellationToken cancellationToken)
    {
        var survivors = new ConcurrentBag<List<FileNode>>();

        Parallel.ForEach(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                CancellationToken = cancellationToken,
            },
            bucket =>
            {
                // Smaller than the screen window means the prefix is the whole file, so
                // screening it would just read everything twice.
                if (bucket[0].Size <= FileHasher.ScreenLength)
                {
                    survivors.Add(bucket);
                    return;
                }

                var byPrefix = new Dictionary<ContentHash, List<FileNode>>();

                foreach (var node in bucket)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = node.GetFullPath();
                    state.CurrentPath = path;

                    try
                    {
                        var prefix = FileHasher.HashPrefix(path, cancellationToken);
                        Interlocked.Add(ref state.BytesHashed, Math.Min(node.Size, FileHasher.ScreenLength));

                        if (!byPrefix.TryGetValue(prefix, out var matches))
                            byPrefix[prefix] = matches = new List<FileNode>(2);
                        matches.Add(node);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // Locked, vanished, or not ours to read. It simply is not a candidate.
                    }
                }

                foreach (var matches in byPrefix.Values)
                    if (matches.Count > 1)
                        survivors.Add(matches);
            });

        return survivors.ToList();
    }

    private static bool TryHashFile(
        string path,
        FileNode node,
        SearchState state,
        ConcurrentDictionary<FileNode, ContentHash> hashes,
        CancellationToken cancellationToken,
        out ContentHash hash)
    {
        if (hashes.TryGetValue(node, out hash)) return true;

        try
        {
            hash = FileHasher.HashFile(
                path,
                read => Interlocked.Add(ref state.BytesHashed, read),
                cancellationToken);

            Interlocked.Increment(ref state.FilesHashed);
            hashes[node] = hash;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            hash = ContentHash.Zero;
            return false;
        }
    }

    /// <summary>
    /// Keeps one entry per distinct piece of storage, dropping paths that are hard links to
    /// a copy already in the group.
    /// </summary>
    /// <remarks>
    /// Hard links point at the same bytes, so removing one recovers nothing — reporting them
    /// as duplicates would promise space that does not exist. They matter far more on Linux
    /// and macOS, where package managers and backup tools such as rsnapshot and Time Machine
    /// use them heavily, than on Windows.
    ///
    /// This runs only on already-confirmed groups, which are few, so the per-file metadata
    /// query costs nothing measurable. Where the platform cannot answer — an unsupported
    /// filesystem, or a layout the self-test rejected — the group is returned untouched,
    /// which is the pre-existing behaviour rather than a wrong one.
    /// </remarks>
    private static List<FileNode> RemoveHardLinkAliases(List<FileNode> matches)
    {
        if (!NativeFs.IsSupported) return matches;

        var seenStorage = new HashSet<(ulong Volume, ulong File)>();
        var distinct = new List<FileNode>(matches.Count);

        foreach (var node in matches)
        {
            if (!NativeFs.TryGetMetadata(node.GetFullPath(), out var meta) || meta.FileId == 0)
            {
                // Nothing to compare it against; keep it rather than silently discard it.
                distinct.Add(node);
                continue;
            }

            // A single-link file cannot alias anything, so skip the bookkeeping.
            if (!meta.IsHardLinked || seenStorage.Add((meta.VolumeId, meta.FileId)))
                distinct.Add(node);
        }

        return distinct;
    }

    /// <summary>Splits a hash-matched group into byte-identical subsets.</summary>
    private static List<List<FileNode>> VerifyGroup(List<FileNode> matches, CancellationToken cancellationToken)
    {
        var subsets = new List<List<FileNode>>();

        foreach (var node in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var placed = false;

            foreach (var subset in subsets)
            {
                try
                {
                    if (!FileHasher.ContentsEqual(subset[0].GetFullPath(), node.GetFullPath(), cancellationToken))
                        continue;

                    subset.Add(node);
                    placed = true;
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Unreadable now; leave it out rather than risk a wrong match.
                    placed = true;
                    break;
                }
            }

            if (!placed) subsets.Add([node]);
        }

        return subsets;
    }

    // ---------------------------------------------------------------- folders

    private IReadOnlyList<DuplicateGroup> FindDuplicateFolders(
        FileNode root,
        DuplicateOptions options,
        SearchState state,
        ConcurrentDictionary<FileNode, ContentHash> hashes,
        CancellationToken cancellationToken)
    {
        // Only directories that agree on size and both counts could possibly match, and all
        // three are already known, so this narrows the field before a single byte is read.
        var candidates = new Dictionary<(long Size, int Files, int Dirs), List<FileNode>>();

        foreach (var node in root.DescendantsAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!node.IsDirectory || node.IsSynthetic || node.IsRoot) continue;
            if (node.Size < options.MinimumFileSize) continue;
            if (node.Children is not { Length: > 0 }) continue;

            var key = (node.Size, node.FileCount, node.DirCount);
            if (!candidates.TryGetValue(key, out var bucket))
                candidates[key] = bucket = new List<FileNode>(2);
            bucket.Add(node);
        }

        var contenders = candidates.Values.Where(b => b.Count > 1).ToList();
        if (contenders.Count == 0) return [];

        var groups = new List<DuplicateGroup>();

        foreach (var bucket in contenders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var byHash = new Dictionary<ContentHash, List<FileNode>>();

            foreach (var directory in bucket)
            {
                state.CurrentPath = directory.GetFullPath();
                var hash = HashDirectory(directory, state, hashes, cancellationToken);
                if (hash.IsZero) continue;

                if (!byHash.TryGetValue(hash, out var matches))
                    byHash[hash] = matches = new List<FileNode>(2);
                matches.Add(directory);
            }

            foreach (var (hash, matches) in byHash)
            {
                if (matches.Count < 2) continue;
                groups.Add(new DuplicateGroup
                {
                    Items = OrderMembers(matches),
                    ItemSize = matches[0].Size,
                    IsFolder = true,
                    Signature = hash.ToString(),
                });
            }
        }

        return SuppressNestedFolderGroups(groups);
    }

    /// <summary>
    /// Signature for a directory: its children's names and signatures, in name order.
    /// </summary>
    /// <remarks>
    /// Iterative rather than recursive, because a deep tree would otherwise risk the stack,
    /// and every file signature is cached so the folder pass reuses whatever the file pass
    /// already read.
    /// </remarks>
    private static ContentHash HashDirectory(
        FileNode directory,
        SearchState state,
        ConcurrentDictionary<FileNode, ContentHash> hashes,
        CancellationToken cancellationToken)
    {
        // Post-order traversal: a node is only combined once all its children are known.
        var stack = new Stack<(FileNode Node, bool Expanded)>();
        stack.Push((directory, false));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, expanded) = stack.Pop();

            if (hashes.ContainsKey(node)) continue;

            if (!node.IsDirectory)
            {
                var path = node.GetFullPath();
                if (!TryHashFile(path, node, state, hashes, cancellationToken, out _))
                    hashes[node] = ContentHash.Zero; // unreadable: cache so we do not retry
                continue;
            }

            if (!expanded)
            {
                stack.Push((node, true));
                var children = node.Children;
                if (children is not null)
                    foreach (var child in children)
                        if (!child.IsSynthetic)
                            stack.Push((child, false));
                continue;
            }

            var parts = (node.Children ?? [])
                .Where(c => !c.IsSynthetic)
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => (c.Name, hashes.TryGetValue(c, out var h) ? h : ContentHash.Zero));

            hashes[node] = FileHasher.CombineDirectory(parts);
        }

        return hashes.TryGetValue(directory, out var result) ? result : ContentHash.Zero;
    }

    /// <summary>
    /// Drops folder groups whose members all sit inside members of a larger group.
    /// </summary>
    /// <remarks>
    /// When two trees match, every matching subdirectory inside them matches too. Listing all
    /// of those would bury the one finding that matters — the outermost folder worth deleting.
    /// </remarks>
    private static IReadOnlyList<DuplicateGroup> SuppressNestedFolderGroups(List<DuplicateGroup> groups)
    {
        var ordered = groups.OrderByDescending(g => g.ItemSize).ToList();
        var reported = new List<DuplicateGroup>(ordered.Count);
        var covered = new HashSet<FileNode>();

        foreach (var group in ordered)
        {
            if (group.Items.All(item => IsUnderAny(item, covered))) continue;

            reported.Add(group);
            foreach (var item in group.Items) covered.Add(item);
        }

        return reported.OrderByDescending(g => g.WastedBytes).ToArray();
    }

    /// <summary>Removes file groups entirely contained within reported duplicate folders.</summary>
    private static IReadOnlyList<DuplicateGroup> SuppressFilesInsideDuplicateFolders(
        IReadOnlyList<DuplicateGroup> fileGroups, IReadOnlyList<DuplicateGroup> folderGroups)
    {
        if (folderGroups.Count == 0 || fileGroups.Count == 0) return fileGroups;

        var folders = new HashSet<FileNode>();
        foreach (var group in folderGroups)
            foreach (var item in group.Items)
                folders.Add(item);

        // Keep a file group unless every copy already lives inside a duplicate folder, where
        // it would be removed along with the folder anyway.
        return fileGroups
            .Where(g => !g.Items.All(item => IsUnderAny(item, folders)))
            .ToArray();
    }

    private static bool IsUnderAny(FileNode node, HashSet<FileNode> ancestors)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
            if (ancestors.Contains(current))
                return true;
        return false;
    }

    // ------------------------------------------------------------------ misc

    private static bool IsEligible(FileNode node, DuplicateOptions options)
    {
        if (node.IsDirectory || node.IsSynthetic) return false;
        if (node.HasFlag(NodeFlags.ReparsePoint)) return false;
        // Hard links already share their bytes; "de-duplicating" them frees nothing.
        if (node.HasFlag(NodeFlags.HardLinkDuplicate)) return false;
        if (node.Size < options.MinimumFileSize) return false;

        return options.IgnoredExtensions.Count == 0 ||
               !options.IgnoredExtensions.Contains(node.Extension);
    }

    /// <summary>
    /// Orders group members so the most plausible one to keep comes first: oldest, then
    /// shallowest path, then alphabetical. The UI leans on this when preselecting.
    /// </summary>
    private static IReadOnlyList<FileNode> OrderMembers(List<FileNode> members)
    {
        return members
            .OrderBy(n => n.LastWriteUtcTicks == 0 ? long.MaxValue : n.LastWriteUtcTicks)
            .ThenBy(n => n.Depth)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private DuplicateProgress Snapshot(SearchState state, TimeSpan elapsed) => new(
        state.Phase,
        Interlocked.Read(ref state.CandidateFiles),
        Interlocked.Read(ref state.FilesHashed),
        Interlocked.Read(ref state.BytesHashed),
        Interlocked.Read(ref state.BytesToHash),
        state.GroupsFound,
        Interlocked.Read(ref state.WastedBytes),
        state.CurrentPath,
        elapsed);

    private IDisposable StartProgressPump(
        SearchState state, Stopwatch stopwatch, IProgress<DuplicateProgress>? progress, TimeSpan interval)
    {
        if (progress is null) return NullDisposable.Instance;

        var cts = new CancellationTokenSource();
        var token = cts.Token;

        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                progress.Report(Snapshot(state, stopwatch.Elapsed));
                token.WaitHandle.WaitOne(interval);
            }
        })
        {
            IsBackground = true,
            Name = "kartova-duplicates-progress",
        };
        thread.Start();

        return new ActionDisposable(() =>
        {
            cts.Cancel();
            thread.Join(TimeSpan.FromSeconds(1));
            cts.Dispose();
        });
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class ActionDisposable(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
