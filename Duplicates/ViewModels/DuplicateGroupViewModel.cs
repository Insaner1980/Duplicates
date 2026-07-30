using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Duplicates.Engine.Models;

namespace Duplicates.ViewModels;

public sealed class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Source = group;
        Files = new ObservableCollection<DuplicateFileViewModel>(
            group.Files.Select(file => new DuplicateFileViewModel(file, this)));
    }

    public DuplicateGroup Source { get; }

    public ObservableCollection<DuplicateFileViewModel> Files { get; }

    public string DisplayName => Files.FirstOrDefault()?.FileName ?? "Duplicate group";

    public string WastedText => ByteFormatter.Format(Source.WastedBytes);

    public string FilesSummaryText => $"{Files.Count:N0} identical files, {WastedText} reclaimable";

    public string SelectedSummaryText => $"{SelectedCount:N0} selected, {ByteFormatter.Format(SelectedBytes)}";

    public int SelectedCount => Files.Count(static file => file.IsSelected);

    public long SelectedBytes => Files.Where(static file => file.IsSelected).Sum(static file => file.SizeBytes);

    public bool CanSelectForDeletion(DuplicateFileViewModel candidate)
    {
        return Files.Count(file => !file.IsSelected && file != candidate) >= 1;
    }

    public void ApplyKeepNewest()
    {
        ApplySurvivor(OrderByNewest(Files).First());
    }

    public void ApplyKeepOldest()
    {
        ApplySurvivor(OrderByOldest(Files).First());
    }

    public void ApplyKeepShortestPath()
    {
        ApplySurvivor(Files.OrderBy(static file => file.FullPath.Length).ThenBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase).First());
    }

    public void ApplyKeepPreferredFolder(string preferredFolder)
    {
        string normalizedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(preferredFolder));
        IEnumerable<DuplicateFileViewModel> preferredCandidates = Files
            .Where(file => IsUnderFolder(file.FullPath, normalizedFolder));
        DuplicateFileViewModel? preferred = OrderByNewest(preferredCandidates).FirstOrDefault();

        ApplySurvivor(preferred ?? OrderByNewest(Files).First());
    }

    public void ClearSelection()
    {
        foreach (DuplicateFileViewModel file in Files)
        {
            file.SetSelectedFromRule(false);
        }

        NotifySelectionChanged();
    }

    public void RemoveDeleted(IReadOnlySet<string> deletedPaths)
    {
        for (int index = Files.Count - 1; index >= 0; index--)
        {
            if (deletedPaths.Contains(Files[index].FullPath))
            {
                Files.RemoveAt(index);
            }
        }

        NotifySelectionChanged();
        OnPropertyChanged(nameof(FilesSummaryText));
    }

    public bool RemoveFile(DuplicateFileViewModel file)
    {
        bool removed = Files.Remove(file);
        if (removed)
        {
            NotifySelectionChanged();
            OnPropertyChanged(nameof(FilesSummaryText));
        }

        return removed;
    }

    internal void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedSummaryText));
        foreach (DuplicateFileViewModel file in Files)
        {
            file.NotifyKeptChanged();
        }
    }

    private void ApplySurvivor(DuplicateFileViewModel survivor)
    {
        foreach (DuplicateFileViewModel file in Files)
        {
            file.SetSelectedFromRule(false);
        }

        foreach (DuplicateFileViewModel file in Files.Where(file => file != survivor))
        {
            file.SetSelectedFromRule(true);
        }

        NotifySelectionChanged();
    }

    private static IOrderedEnumerable<DuplicateFileViewModel> OrderByNewest(IEnumerable<DuplicateFileViewModel> files)
    {
        return files
            .OrderByDescending(static file => file.File.ModifiedUtc)
            .ThenBy(static file => file.FullPath.Length)
            .ThenBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase);
    }

    private static IOrderedEnumerable<DuplicateFileViewModel> OrderByOldest(IEnumerable<DuplicateFileViewModel> files)
    {
        return files
            .OrderBy(static file => file.File.ModifiedUtc)
            .ThenBy(static file => file.FullPath.Length)
            .ThenBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUnderFolder(string filePath, string folderPath)
    {
        string normalizedFile = Path.GetFullPath(filePath);
        return normalizedFile.Length > folderPath.Length &&
            normalizedFile.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase) &&
            (folderPath.EndsWith(Path.DirectorySeparatorChar) || normalizedFile[folderPath.Length] == Path.DirectorySeparatorChar);
    }
}
