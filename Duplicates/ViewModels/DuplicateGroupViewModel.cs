using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Duplicates.Engine.Models;

namespace Duplicates.ViewModels;

public sealed class DuplicateGroupViewModel : ObservableObject
{
    private bool _isExpanded;

    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Source = group;
        Files = new ObservableCollection<DuplicateFileViewModel>(
            group.Files.Select(file => new DuplicateFileViewModel(file, this)));
    }

    public DuplicateGroup Source { get; }

    public ObservableCollection<DuplicateFileViewModel> Files { get; }

    public ObservableCollection<DuplicateFileViewModel> VisibleFiles { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                RefreshVisibleFiles();
            }
        }
    }

    public string DisplayName => Files.FirstOrDefault()?.FileName ?? "Duplicate group";

    public string CountText => $"x{Files.Count}";

    public string SizeText => ByteFormatter.Format(Source.SizeBytes);

    public string WastedText => ByteFormatter.Format(Source.WastedBytes);

    public string SelectedSummaryText => $"{SelectedCount} selected - {ByteFormatter.Format(SelectedBytes)}";

    public int SelectedCount => Files.Count(static file => file.IsSelected);

    public long SelectedBytes => Files.Where(static file => file.IsSelected).Sum(static file => file.SizeBytes);

    public bool HasVisibleFiles => Files.Count > 1;

    public bool CanSelectForDeletion(DuplicateFileViewModel candidate)
    {
        return Files.Count(file => !file.IsSelected && file != candidate) >= 1;
    }

    public void ApplyKeepNewest()
    {
        ApplySurvivor(Files.OrderByDescending(static file => file.File.ModifiedUtc).First());
    }

    public void ApplyKeepOldest()
    {
        ApplySurvivor(Files.OrderBy(static file => file.File.ModifiedUtc).First());
    }

    public void ApplyKeepShortestPath()
    {
        ApplySurvivor(Files.OrderBy(static file => file.FullPath.Length).ThenBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase).First());
    }

    public void ApplyKeepPreferredFolder(string preferredFolder)
    {
        string normalizedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(preferredFolder));
        DuplicateFileViewModel? preferred = Files
            .Where(file => IsUnderFolder(file.FullPath, normalizedFolder))
            .OrderByDescending(static file => file.File.ModifiedUtc)
            .FirstOrDefault();

        ApplySurvivor(preferred ?? Files.OrderByDescending(static file => file.File.ModifiedUtc).First());
    }

    public void ClearSelection()
    {
        foreach (DuplicateFileViewModel file in Files)
        {
            file.SetSelectedFromRule(false);
        }

        NotifySelectionChanged();
    }

    public void SelectAllButNewest()
    {
        ApplyKeepNewest();
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
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasVisibleFiles));
        RefreshVisibleFiles();
    }

    public bool RemoveFile(DuplicateFileViewModel file)
    {
        bool removed = Files.Remove(file);
        if (removed)
        {
            NotifySelectionChanged();
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(HasVisibleFiles));
            RefreshVisibleFiles();
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

    private void RefreshVisibleFiles()
    {
        VisibleFiles.Clear();
        if (!IsExpanded)
        {
            return;
        }

        foreach (DuplicateFileViewModel file in Files)
        {
            VisibleFiles.Add(file);
        }
    }

    private static bool IsUnderFolder(string filePath, string folderPath)
    {
        string normalizedFile = Path.GetFullPath(filePath);
        return normalizedFile.Length > folderPath.Length &&
            normalizedFile.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase) &&
            (folderPath.EndsWith(Path.DirectorySeparatorChar) || normalizedFile[folderPath.Length] == Path.DirectorySeparatorChar);
    }
}
