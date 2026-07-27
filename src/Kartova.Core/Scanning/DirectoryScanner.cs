using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using Kartova.Core.Files;
using Kartova.Core.Model;

namespace Kartova.Core.Scanning;

/// <summary>
/// Walks one or more directory trees in parallel and returns an aggregated
/// <see cref="ScanResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// Work is distributed through a lock-free stack of pending directories drained by a fixed
/// worker pool. A stack rather than a queue keeps traversal depth-first, which holds the
/// working set small and keeps directory metadata hot in the OS cache.
/// </para>
/// <para>
/// Enumeration uses <see cref="FileSystemEnumerable{TResult}"/> with a struct projection,
/// so the hot path allocates only the node itself and its name — no <c>FileInfo</c> per entry.
/// </para>
/// <para>
/// Sizes are rolled up in a single pass after the walk rather than with interlocked adds up
/// the parent chain during it. That trades a brief post-pass for zero contention on the root.
/// </para>
/// </remarks>
public sealed class DirectoryScanner
{
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = false,
        // Denials are handled per directory so they can be reported, not silently dropped.
        IgnoreInaccessible = true,
        // Default skips Hidden|System; we want to see everything and decide ourselves.
        AttributesToSkip = FileAttributes.None,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    /// <summary>Projection run per directory entry. Must not allocate beyond the name.</summary>
    private static readonly FileSystemEnumerable<RawEntry>.FindTransform Project =
        static (ref FileSystemEntry entry) =>
        {
            var isDir = entry.IsDirectory;
            return new RawEntry(
                entry.FileName.ToString(),
                isDir ? 0L : entry.Length,
                entry.Attributes,
                entry.LastWriteTimeUtc.UtcTicks,
                isDir);
        };

    private readonly record struct RawEntry(
        string Name,
        long Length,
        FileAttributes Attributes,
        long LastWriteUtcTicks,
        bool IsDirectory);

    private readonly record struct WorkItem(FileNode Node, string Path);

    public Task<ScanResult> ScanAsync(
        IReadOnlyList<string> roots,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Scan(roots, options, progress, cancellationToken), CancellationToken.None);
    }

    public ScanResult Scan(
        IReadOnlyList<string> roots,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0) throw new ArgumentException("At least one root is required.", nameof(roots));

        options ??= new ScanOptions();

        var normalized = new List<string>(roots.Count);
        foreach (var raw in roots) normalized.Add(NormalizeRoot(raw));

        // Probe the allocation unit once, from the first root, rather than per directory.
        var state = new ScanState(options, cancellationToken, normalized[0]);
        var stopwatch = Stopwatch.StartNew();

        var rootNodes = new List<FileNode>(roots.Count);
        foreach (var full in normalized)
        {
            var node = new FileNode(full, NodeFlags.Directory | NodeFlags.Root);
            rootNodes.Add(node);
            state.Push(new WorkItem(node, full));
        }

        using var progressPump = StartProgressPump(state, stopwatch, progress, options.ProgressInterval);

        RunWorkers(state, options.MaxDegreeOfParallelism);

        stopwatch.Stop();

        // Roll up sizes and counts even when cancelled — a partial tree is still useful.
        foreach (var root in rootNodes) Aggregate(root);

        if (options.IncludeFreeSpace) AddVolumeNodes(rootNodes);

        var tree = BuildTreeRoot(rootNodes);
        tree.SortBySizeDescending();

        var extensions = BuildExtensionStats(rootNodes);

        progress?.Report(state.Snapshot(stopwatch.Elapsed, isComplete: true));

        return new ScanResult
        {
            Root = tree,
            Roots = rootNodes,
            Duration = stopwatch.Elapsed,
            TotalFiles = state.Files,
            TotalDirectories = state.Directories,
            TotalBytes = rootNodes.Sum(r => r.Size),
            DeniedPaths = state.Denied.ToArray(),
            WasCancelled = cancellationToken.IsCancellationRequested,
            Extensions = extensions,
        };
    }

    // ------------------------------------------------------------- worker pool

    private void RunWorkers(ScanState state, int workerCount)
    {
        var workers = new Thread[Math.Max(1, workerCount)];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = new Thread(() => WorkerLoop(state))
            {
                IsBackground = true,
                Name = $"kartova-scan-{i}",
                // Directory walking is IO-bound; a small stack is plenty.
                Priority = ThreadPriority.BelowNormal,
            };
            workers[i].Start();
        }

        foreach (var w in workers) w.Join();
    }

    private void WorkerLoop(ScanState state)
    {
        while (true)
        {
            if (!state.TryTake(out var item)) return;

            try
            {
                ProcessDirectory(item, state);
            }
            catch (OperationCanceledException)
            {
                // Cancellation unwinds every worker; the partial tree survives.
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException)
            {
                item.Node.Flags |= NodeFlags.AccessDenied;
                state.RecordDenied(item.Path);
            }
            finally
            {
                state.CompleteOne();
            }
        }
    }

    private void ProcessDirectory(WorkItem item, ScanState state)
    {
        var options = state.Options;
        state.CancellationToken.ThrowIfCancellationRequested();
        state.SetCurrentPath(item.Path);

        List<FileNode>? children = null;
        List<WorkItem>? subdirectories = null;

        try
        {
            var entries = new FileSystemEnumerable<RawEntry>(item.Path, Project, Enumeration);

            foreach (var entry in entries)
            {
                if (state.CancellationToken.IsCancellationRequested) break;

                var attributes = entry.Attributes;

                if (options.SkipHidden && (attributes & FileAttributes.Hidden) != 0) continue;
                if (options.SkipSystem && (attributes & FileAttributes.System) != 0) continue;

                var flags = TranslateAttributes(attributes);
                var isReparse = (flags & NodeFlags.ReparsePoint) != 0;

                if (entry.IsDirectory && !isReparse)
                {
                    var childPath = Path.Combine(item.Path, entry.Name);

                    if (options.ExcludedPaths.Contains(childPath) ||
                        options.ExcludedDirectoryNames.Contains(entry.Name))
                    {
                        continue;
                    }

                    var dirNode = new FileNode(entry.Name, flags | NodeFlags.Directory)
                    {
                        Parent = item.Node,
                        LastWriteUtcTicks = entry.LastWriteUtcTicks,
                    };

                    (children ??= new List<FileNode>(16)).Add(dirNode);
                    (subdirectories ??= new List<WorkItem>(8)).Add(new WorkItem(dirNode, childPath));
                }
                else
                {
                    // Files, plus symlinks and junctions, which are recorded but never entered.
                    var fileNode = new FileNode(entry.Name, flags)
                    {
                        Parent = item.Node,
                        LastWriteUtcTicks = entry.LastWriteUtcTicks,
                    };

                    if (entry.IsDirectory)
                    {
                        // A directory symlink: keep it visible, but it owns no bytes of its own.
                        fileNode.Flags |= NodeFlags.Directory;
                        fileNode.Children = [];
                    }
                    else
                    {
                        ApplySize(fileNode, entry, item.Path, state);
                    }

                    (children ??= new List<FileNode>(16)).Add(fileNode);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            item.Node.Flags |= NodeFlags.AccessDenied;
            state.RecordDenied(item.Path);
        }
        catch (DirectoryNotFoundException)
        {
            // Removed while we walked. Nothing to record.
        }
        catch (IOException)
        {
            item.Node.Flags |= NodeFlags.AccessDenied;
            state.RecordDenied(item.Path);
        }

        item.Node.Children = children is null ? [] : children.ToArray();
        state.CountDirectory();

        if (subdirectories is null) return;
        foreach (var sub in subdirectories) state.Push(sub);
    }

    /// <summary>Assigns logical and allocated size, applying hard-link dedup when enabled.</summary>
    private static void ApplySize(FileNode node, in RawEntry entry, string directory, ScanState state)
    {
        var options = state.Options;
        var size = entry.Length;
        var needsNative = (options.DetectHardLinks || options.ExactAllocation) && NativeFs.IsSupported;

        long allocated;

        if (needsNative &&
            NativeFs.TryGetMetadata(Path.Combine(directory, entry.Name), out var meta))
        {
            if (options.DetectHardLinks && meta.IsHardLinked && !state.ClaimHardLink(meta))
            {
                // Content already counted through another path. Keep the node, drop the bytes.
                node.Flags |= NodeFlags.HardLinkDuplicate;
                node.Size = 0;
                node.SizeOnDisk = 0;
                state.CountFile(0);
                return;
            }

            allocated = options.ExactAllocation ? meta.AllocatedSize : state.RoundToCluster(size);
        }
        else
        {
            allocated = state.RoundToCluster(size);
        }

        node.Size = size;
        node.SizeOnDisk = options.ComputeSizeOnDisk ? allocated : size;
        state.CountFile(size);
    }

    private static NodeFlags TranslateAttributes(FileAttributes attributes)
    {
        var flags = NodeFlags.None;
        if ((attributes & FileAttributes.ReparsePoint) != 0) flags |= NodeFlags.ReparsePoint;
        if ((attributes & FileAttributes.Hidden) != 0) flags |= NodeFlags.Hidden;
        if ((attributes & FileAttributes.System) != 0) flags |= NodeFlags.System;
        if ((attributes & FileAttributes.ReadOnly) != 0) flags |= NodeFlags.ReadOnly;
        if ((attributes & FileAttributes.Compressed) != 0) flags |= NodeFlags.Compressed;
        if ((attributes & FileAttributes.SparseFile) != 0) flags |= NodeFlags.Sparse;
        if ((attributes & FileAttributes.Encrypted) != 0) flags |= NodeFlags.Encrypted;
        return flags;
    }

    // -------------------------------------------------------------- aggregation

    /// <summary>
    /// Rolls sizes and counts from leaves to root in one pass.
    /// </summary>
    /// <remarks>
    /// Breadth-first ordering visits every parent before its children, so walking the
    /// resulting list backwards guarantees a child is fully totalled before it is added
    /// to its parent. This avoids recursion, which would overflow on deep trees.
    /// </remarks>
    internal static void Aggregate(FileNode root)
    {
        var order = new List<FileNode>(1024);
        var queue = new Queue<FileNode>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            order.Add(node);
            var kids = node.Children;
            if (kids is null) continue;
            foreach (var child in kids) queue.Enqueue(child);
        }

        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            var parent = node.Parent;
            if (parent is null) continue;

            parent.Size += node.Size;
            parent.SizeOnDisk += node.SizeOnDisk;

            if (node.IsDirectory)
            {
                parent.FileCount += node.FileCount;
                parent.DirCount += node.DirCount + 1;
            }
            else
            {
                parent.FileCount += 1;
            }
        }
    }

    /// <summary>
    /// Adds free-space and unknown-space nodes so the treemap represents the whole volume,
    /// the way WinDirStat does. "Unknown" is the gap between what the filesystem reports as
    /// used and what the scan could actually see — denied directories, snapshots, metadata.
    /// </summary>
    private static void AddVolumeNodes(List<FileNode> roots)
    {
        foreach (var root in roots)
        {
            if (!IsVolumeRoot(root.Name)) continue;

            var volume = VolumeProvider.TryDescribe(root.Name);
            if (volume is null || volume.TotalBytes <= 0) continue;

            var extra = new List<FileNode>(2);

            if (volume.FreeBytes > 0)
            {
                extra.Add(new FileNode("<Free space>", NodeFlags.FreeSpace)
                {
                    Parent = root,
                    Size = volume.FreeBytes,
                    SizeOnDisk = volume.FreeBytes,
                });
            }

            var used = volume.TotalBytes - volume.FreeBytes;
            var unaccounted = used - root.Size;
            // Ignore noise; only surface a gap worth explaining.
            if (unaccounted > 16L * 1024 * 1024)
            {
                extra.Add(new FileNode("<Unknown>", NodeFlags.Unknown)
                {
                    Parent = root,
                    Size = unaccounted,
                    SizeOnDisk = unaccounted,
                });
            }

            if (extra.Count == 0) continue;

            var merged = new FileNode[(root.Children?.Length ?? 0) + extra.Count];
            root.Children?.CopyTo(merged, 0);
            extra.CopyTo(merged, root.Children?.Length ?? 0);
            root.Children = merged;

            foreach (var node in extra) root.Size += node.Size;
            foreach (var node in extra) root.SizeOnDisk += node.SizeOnDisk;
        }
    }

    private static FileNode BuildTreeRoot(List<FileNode> roots)
    {
        if (roots.Count == 1) return roots[0];

        var combined = new FileNode("Scan", NodeFlags.Directory | NodeFlags.Root)
        {
            Children = roots.ToArray(),
        };

        foreach (var root in roots)
        {
            root.Parent = combined;
            combined.Size += root.Size;
            combined.SizeOnDisk += root.SizeOnDisk;
            combined.FileCount += root.FileCount;
            combined.DirCount += root.DirCount + 1;
        }

        return combined;
    }

    private static IReadOnlyList<ExtensionStat> BuildExtensionStats(List<FileNode> roots)
    {
        var totals = new Dictionary<string, (long Size, int Count)>(StringComparer.OrdinalIgnoreCase);
        long grandTotal = 0;

        foreach (var root in roots)
        {
            foreach (var node in root.DescendantsAndSelf())
            {
                if (node.IsDirectory || node.IsSynthetic) continue;
                if (node.HasFlag(NodeFlags.HardLinkDuplicate)) continue;

                var ext = node.Extension;
                totals.TryGetValue(ext, out var current);
                totals[ext] = (current.Size + node.Size, current.Count + 1);
                grandTotal += node.Size;
            }
        }

        var stats = new List<ExtensionStat>(totals.Count);
        foreach (var (ext, value) in totals)
        {
            stats.Add(new ExtensionStat
            {
                Extension = ext,
                TotalSize = value.Size,
                FileCount = value.Count,
                Fraction = grandTotal > 0 ? (double)value.Size / grandTotal : 0,
                Color = FileTypeColors.ForExtension(ext),
            });
        }

        stats.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));
        return stats;
    }

    // ------------------------------------------------------------------ helpers

    private static string NormalizeRoot(string path)
    {
        var full = Path.GetFullPath(path);
        // Keep the separator on a bare root ("C:\", "/"), strip it everywhere else.
        if (full.Length > 1 && !IsVolumeRoot(full))
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full;
    }

    private static bool IsVolumeRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) &&
               string.Equals(root, path, PathComparison) ;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static IDisposable StartProgressPump(
        ScanState state, Stopwatch stopwatch, IProgress<ScanProgress>? progress, TimeSpan interval)
    {
        if (progress is null) return NullDisposable.Instance;

        var cts = new CancellationTokenSource();
        var token = cts.Token;

        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    progress.Report(state.Snapshot(stopwatch.Elapsed, isComplete: false));
                    token.WaitHandle.WaitOne(interval);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "kartova-progress",
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

    /// <summary>
    /// Shared mutable state for one scan: the pending-work stack, counters, and the
    /// completion protocol that lets workers agree the walk is finished.
    /// </summary>
    private sealed class ScanState(ScanOptions options, CancellationToken cancellationToken, string clusterProbePath)
    {
        private readonly ConcurrentStack<WorkItem> _pending = new();
        private readonly ConcurrentDictionary<(ulong Volume, ulong File), byte> _hardLinks = new();

        private long _outstanding;
        private long _files;
        private long _directories;
        private long _bytes;
        private volatile bool _finished;
        private volatile string _currentPath = string.Empty;

        public ScanOptions Options { get; } = options;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public ConcurrentBag<string> Denied { get; } = [];

        public long Files => Interlocked.Read(ref _files);
        public long Directories => Interlocked.Read(ref _directories);

        public void Push(WorkItem item)
        {
            Interlocked.Increment(ref _outstanding);
            _pending.Push(item);
        }

        /// <summary>
        /// Takes the next directory, waiting while peers are still producing work.
        /// Returns <c>false</c> only once no further work can ever arrive.
        /// </summary>
        /// <remarks>
        /// The stack is the single source of truth. An earlier draft gated workers on a
        /// semaphore, which could strand an item whenever a permit was consumed by a worker
        /// that then lost the pop race — leaving every worker parked while work remained.
        /// Polling the stack directly and backing off with <see cref="SpinWait"/> cannot
        /// deadlock: escalating to a 1 ms sleep keeps idle workers essentially free.
        /// </remarks>
        public bool TryTake(out WorkItem item)
        {
            var spin = new SpinWait();
            while (true)
            {
                if (_pending.TryPop(out item)) return true;

                if (_finished || CancellationToken.IsCancellationRequested)
                {
                    item = default;
                    return false;
                }

                spin.SpinOnce();
            }
        }

        /// <summary>
        /// Marks one directory done. Reaching zero means every directory that was ever
        /// queued has been processed, so the walk is over.
        /// </summary>
        public void CompleteOne()
        {
            if (Interlocked.Decrement(ref _outstanding) == 0) _finished = true;
        }

        public void CountFile(long size)
        {
            Interlocked.Increment(ref _files);
            if (size > 0) Interlocked.Add(ref _bytes, size);
        }

        public void CountDirectory() => Interlocked.Increment(ref _directories);

        public void RecordDenied(string path) => Denied.Add(path);

        public void SetCurrentPath(string path) => _currentPath = path;

        /// <summary>Reserves a hard-linked inode. Returns false when another path already owns it.</summary>
        public bool ClaimHardLink(in FileMetadata meta) =>
            _hardLinks.TryAdd((meta.VolumeId, meta.FileId), 0);

        /// <summary>Rounds a logical size up to the volume's allocation unit.</summary>
        public long RoundToCluster(long size)
        {
            if (size <= 0) return 0;
            var cluster = ClusterSize;
            return (size + cluster - 1) / cluster * cluster;
        }

        private readonly Lazy<long> _clusterSize =
            new(() => NativeFs.GetClusterSize(clusterProbePath), isThreadSafe: true);

        private long ClusterSize => _clusterSize.Value;

        public ScanProgress Snapshot(TimeSpan elapsed, bool isComplete) => new(
            Interlocked.Read(ref _files),
            Interlocked.Read(ref _directories),
            Interlocked.Read(ref _bytes),
            Denied.Count,
            _currentPath,
            elapsed,
            isComplete);
    }
}
