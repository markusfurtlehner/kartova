using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kartova.App.Cli;
using Kartova.App.Localization;
using Kartova.Core.Files;
using Kartova.Core.Model;
using Kartova.Core.Snapshots;

namespace Kartova.App.ViewModels;

/// <summary>One stored scan, offered as a comparison target.</summary>
public sealed class SnapshotViewModel(SnapshotInfo info)
{
    public SnapshotInfo Info { get; } = info;

    public string RootPath => Info.RootPath;
    public string TakenText => Loc.Format("Snap.Taken", Info.TakenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
    public string SizeText => SizeFormatter.Format(Info.TotalBytes);
    public string FilesText => Loc.Format("Summary.NFiles", SizeFormatter.FormatCount(Info.TotalFiles));
}

/// <summary>One line in the change list.</summary>
public sealed class ChangeViewModel
{
    public ChangeViewModel(ChangeNode change, string rootPath)
    {
        Change = change;
        FullPath = change.GetFullPath();

        RelativePath = !string.IsNullOrEmpty(rootPath) &&
                       FullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
            ? FullPath[rootPath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : FullPath;

        if (RelativePath.Length == 0) RelativePath = change.Name;
    }

    public ChangeNode Change { get; }
    public string FullPath { get; }
    public string RelativePath { get; }

    public string Name => Change.Name;

    public string KindText => Loc.T($"Snap.{Change.Kind}");

    /// <summary>Signed size, so the direction reads without decoding a colour.</summary>
    public string DeltaText =>
        (Change.Delta >= 0 ? "+" : "−") + SizeFormatter.Format(Math.Abs(Change.Delta));

    public string BeforeText => Change.OldSize > 0 ? SizeFormatter.Format(Change.OldSize) : "—";
    public string AfterText => Change.NewSize > 0 ? SizeFormatter.Format(Change.NewSize) : "—";

    /// <summary>Growth reads warm, shrinkage reads cool — the same convention as a diff.</summary>
    public IBrush AccentBrush => new SolidColorBrush(Color.FromUInt32(Change.Kind switch
    {
        ChangeKind.Added => 0xFFFF9E4C,
        ChangeKind.Grew => 0xFFFF6B8A,
        ChangeKind.Removed => 0xFF37D6B0,
        ChangeKind.Shrank => 0xFF5AC8FA,
        _ => 0xFF8A93A6,
    }));

    /// <summary>Bar width relative to the largest change in the list, 0..1.</summary>
    public double Fraction { get; set; }
}

/// <summary>Owns snapshot storage and the comparison between two scans.</summary>
public sealed partial class ComparisonViewModel : ObservableObject
{
    public ObservableCollection<SnapshotViewModel> Snapshots { get; } = [];
    public ObservableCollection<ChangeViewModel> Changes { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasComparison;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private string _beforeText = string.Empty;
    [ObservableProperty] private string _afterText = string.Empty;
    [ObservableProperty] private string _changeText = string.Empty;
    [ObservableProperty] private IBrush _changeBrush = Brushes.Gray;

    public Func<FileNode?>? GetScanRoot { get; set; }

    public void RefreshSnapshots()
    {
        Snapshots.Clear();
        foreach (var info in SnapshotFile.List(SnapshotStore.DefaultDirectory))
            Snapshots.Add(new SnapshotViewModel(info));
    }

    [RelayCommand]
    private void SaveSnapshot()
    {
        var root = GetScanRoot?.Invoke();
        if (root is null) return;

        try
        {
            var path = SnapshotStore.SuggestPath(root.Name);
            SnapshotStore.EnsureDirectory(Path.GetDirectoryName(path));
            SnapshotFile.Save(root, path);

            StatusMessage = Loc.Format("Status.SnapshotSaved", path);
            RefreshSnapshots();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = Loc.Format("Status.SnapshotFailed", e.Message);
        }
    }

    [RelayCommand]
    private async Task CompareAsync(SnapshotViewModel? snapshot)
    {
        var current = GetScanRoot?.Invoke();
        if (snapshot is null || current is null) return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var comparison = await Task.Run(() =>
            {
                var stored = SnapshotFile.Load(snapshot.Info.FilePath);
                return stored is null ? null : TreeComparer.Compare(stored.Root, current);
            });

            if (comparison is null)
            {
                StatusMessage = Loc.Format("Status.SnapshotFailed", snapshot.Info.FilePath);
                return;
            }

            Populate(comparison, current.GetFullPath());
            HasComparison = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Populate(ComparisonResult comparison, string rootPath)
    {
        BeforeText = SizeFormatter.Format(comparison.OldTotal);
        AfterText = SizeFormatter.Format(comparison.NewTotal);
        ChangeText = (comparison.Delta >= 0 ? "+" : "−") + SizeFormatter.Format(Math.Abs(comparison.Delta));

        ChangeBrush = new SolidColorBrush(Color.FromUInt32(
            comparison.Delta > 0 ? 0xFFFF6B8A : comparison.Delta < 0 ? 0xFF37D6B0 : 0xFF8A93A6));

        Changes.Clear();

        // Bars are scaled against the largest change, so the list reads as a ranking rather
        // than as a set of numbers that all look the same length.
        var largest = comparison.Changes.Count > 0 ? comparison.Changes[0].Magnitude : 1;

        foreach (var change in comparison.Changes.Take(300))
        {
            Changes.Add(new ChangeViewModel(change, rootPath)
            {
                Fraction = largest > 0 ? Math.Clamp((double)change.Magnitude / largest, 0, 1) : 0,
            });
        }

        IsEmpty = Changes.Count == 0;
    }

    public void Reset()
    {
        Changes.Clear();
        HasComparison = false;
        IsEmpty = false;
        StatusMessage = string.Empty;
    }
}
