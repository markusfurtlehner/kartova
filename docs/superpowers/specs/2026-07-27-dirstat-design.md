# DirStat — Design Document

**Date:** 2026-07-27
**Status:** Approved (stack), implementing

## Goal

A cross-platform disk usage analyzer with feature parity to WinDirStat, QDirStat, and
SpaceSniffer, but with a modern, fast, professional UI. Ships as a single self-contained
executable per platform with no runtime dependencies to install. MIT licensed.

The existing tools are functionally excellent but visually dated and occasionally sluggish
on large volumes. DirStat keeps their capabilities and replaces the interaction and
rendering layer.

## Stack

**.NET 9 + Avalonia UI 11**, published self-contained and trimmed.

| Concern | Resolution |
|---|---|
| Runtime deps | Self-contained publish. Windows and macOS need nothing. Linux needs only `libX11`, `libICE`, `libSM`, `fontconfig` — present on every desktop install. |
| Rendering | Avalonia renders through Skia with GPU acceleration. The treemap is a custom control that blits a pre-rasterized bitmap, so pan/hover/select cost nothing. |
| Scan speed | `System.IO.Enumeration.FileSystemEnumerator<T>` (no `FileInfo` allocation) driven by a work-stealing parallel worker pool. |
| Binary size | ~45 MB trimmed per RID. |
| License compat | Avalonia is MIT. .NET is MIT. No copyleft dependencies. |

Rejected: Tauri (requires `webkit2gtk` on Linux — violates the no-dependency rule),
Electron (~200 MB, slow cold start — the exact problem being solved), Go (no GUI toolkit
capable of the target visual quality without building a widget layer from scratch).

## Architecture

Three assemblies. `Core` has no UI dependency and is fully unit-testable.

```
DirStat.Core        scanning, treemap layout, rasterization, exports, shell ops
DirStat.App         Avalonia views, view models, theming
DirStat.Core.Tests  xUnit
```

### Data flow

```
VolumeProvider ──▶ DriveSelectView
                        │ user picks roots
                        ▼
                  DirectoryScanner ──progress(20Hz)──▶ ScanningView
                        │ ScanResult (FileNode tree)
                        ▼
        ┌───────────────┼────────────────┐
        ▼               ▼                ▼
  DirectoryTree    TreemapLayout    ExtensionStats
   (virtualized)         │           (aggregated)
        │                ▼                │
        │        CushionRasterizer        │
        │         BGRA + owner[]          │
        └────────────┬───┴────────────────┘
                     ▼
             SelectionCoordinator
        (bidirectional sync across all three panes)
```

## Core components

### FileNode — the memory-critical type

A scan of a full system volume can exceed 1M nodes, so the node is deliberately compact.
Full paths are **not** stored; they are reconstructed by walking `Parent`. Only the segment
name lives on the node.

```
sealed class FileNode
    string      Name          // segment only, not full path
    FileNode?   Parent
    FileNode[]? Children      // null for files
    long        Size          // logical bytes; aggregate for directories
    long        SizeOnDisk    // cluster-rounded (allocation) size
    long        LastWriteUtcTicks
    int         FileCount     // subtree
    int         DirCount      // subtree
    NodeFlags   Flags         // Directory | ReparsePoint | Hidden | System | Denied
                              // | HardLinkDuplicate | FreeSpace | Unknown
```

Roughly 56 bytes plus the name string per node. Extension strings are interned so the
common tail (`.dll`, `.png`) costs one allocation total.

### DirectoryScanner

- Producer/consumer over a `ConcurrentStack<FileNode>` of pending directories, drained by
  `Environment.ProcessorCount` workers. Depth-first ordering keeps the working set small.
- Enumeration through a custom `FileSystemEnumerator<Entry>` returning a struct — no
  `FileInfo`/`DirectoryInfo` allocation on the hot path.
- Progress published at ~20 Hz through a throttled channel so the UI never floods.
- Fully cancellable; a cancelled scan still yields the partial tree.
- **Reparse points** (junctions, symlinks) are not descended into. They appear in the tree
  at zero size, flagged, which prevents both cycles and double-counting.
- **Hard links** are deduplicated. Windows: the file index is read only when the link count
  exceeds 1. Unix: `(dev, ino)` pairs are tracked, again only when `st_nlink > 1`. The first
  encountered instance carries the size; later ones are flagged `HardLinkDuplicate` and
  contribute zero.
- **Access denied** directories are recorded and flagged rather than aborting the scan; the
  count surfaces in the UI.
- Platform exclusions by default: `/proc`, `/sys`, `/dev`, `/run` on Linux; `/System/Volumes`
  on macOS; pagefile and `System Volume Information` on Windows.
- Size-on-disk uses the volume cluster size, with sparse and compressed files measured by
  their actual allocation where the platform exposes it.

### Treemap

Squarified layout (Bruls, Huizing, van Wijk) — the algorithm behind WinDirStat's
"squarified" mode, which yields near-square rectangles and so the most readable result.

Rendering is a **rasterizer, not a scene graph**. This is the central performance decision:
drawing 500k retained visual elements would be hopeless, but rasterizing 500k rectangles
into a pixel buffer once is milliseconds.

The rasterizer produces two parallel buffers of `W × H`:
- `uint[] pixels` — BGRA colour with WinDirStat-style cushion shading
- `int[] owner` — the index of the node owning that pixel

`owner` makes hit-testing **O(1)**: hovering is an array lookup, not a tree walk. Selection
highlight and hover outline are drawn as a cheap overlay on top of the cached bitmap, so
neither triggers re-rasterization. Re-rasterization happens only on resize, zoom, or filter
change, and runs on a background thread with the previous bitmap left on screen.

Rectangles below ~1px are culled, which bounds work regardless of tree size.

### Visual direction

Decided rather than deferred, to keep implementation moving:

- **Cushion shading** from WinDirStat — the ridged, lit-from-upper-left look that makes
  nested structure readable at a glance. Retained because nothing else conveys hierarchy as
  well in a dense treemap.
- **Nested framing** from SpaceSniffer — directories get a subtle inset border and label
  when large enough to carry one, so structure is legible without hovering.
- **Dark-first** palette with a light theme available. Custom chromeless title bar.
- File-type colours are stable and curated for common extensions, with a hash-derived
  fallback so unknown types remain distinguishable across sessions.

## UI

Three screens.

1. **Volume picker** — cards per volume showing used/free as a ring, filesystem, and mount
   point. Also accepts an arbitrary folder, and multiple roots in one scan.
2. **Scanning** — live file/directory counters, current path, throughput, elapsed, cancel.
   Partial results are viewable during the scan.
3. **Results** — three synchronized panes:
   - Directory tree: virtualized, sortable columns (size, % of parent, items, files, subdirs,
     last modified). Percentage bars inline.
   - Treemap: centre stage. Zoom into a directory, breadcrumb trail back out.
   - Extension list: aggregated by type with colour swatch, total size, %, file count.

Selecting in any pane selects in the other two. This is the WinDirStat interaction that
makes the tool useful, and it is preserved exactly.

## Feature parity checklist

| Feature | Source |
|---|---|
| Squarified cushion treemap | WinDirStat |
| Tri-pane synchronized selection | WinDirStat |
| Extension/type breakdown with colours | WinDirStat |
| Sortable directory tree with % bars | QDirStat |
| Free space + unknown space nodes | WinDirStat |
| Multi-root scan | QDirStat |
| Refresh single subtree | QDirStat |
| Zoom into / out of directory | SpaceSniffer |
| Live filter by name, size, date, type | SpaceSniffer |
| Open / reveal in file manager / copy path | all three |
| Delete to trash, or permanently | all three |
| Export CSV / JSON | QDirStat |
| Keyboard-driven navigation | QDirStat |

## Error handling

- Unreadable directories are flagged and counted, never fatal.
- A volume that disappears mid-scan ends that root cleanly and keeps the rest.
- Cancellation always returns a usable partial tree.
- Deletions are confirmed, default to trash, and refresh only the affected subtree.
- Rasterization failure falls back to flat fill rather than a blank pane.

## Testing

`DirStat.Core.Tests` covers the parts where correctness is not visually obvious:
synthetic-tree scanning and aggregation, hard-link dedup, reparse-point non-descent,
cancellation yielding partial results, squarified layout invariants (no overlap, full
coverage, area proportional to size), owner-buffer hit-test agreement with layout rects,
and size formatting.

## Distribution

`dotnet publish` self-contained per RID: `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`,
`linux-x64`, `linux-arm64`. Windows and Linux ship a single file; macOS ships a `.app`
bundle. A GitHub Actions workflow builds all six on tag.
