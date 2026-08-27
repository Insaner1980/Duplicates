using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Duplicates.Engine.Models;

namespace Duplicates.ViewModels;

public sealed class DuplicateGroupViewModel : ObservableObject
{
    private DuplicateFileViewModel? _linkSurvivor;
    private bool _canMutateSelection = true;

    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Source = group;
        Files = new ObservableCollection<DuplicateFileViewModel>(
            group.Files.Select(file => new DuplicateFileViewModel(file, this)));
    }

    public DuplicateGroup Source { get; }

    public ObservableCollection<DuplicateFileViewModel> Files { get; }

    public string DisplayName => Files.FirstOrDefault()?.FileName ?? "Duplicate group";

    public long WastedBytes => Files.FirstOrDefault()?.SizeBytes * Math.Max(0, Files.Count - 1) ?? 0;

    public string WastedText => ByteFormatter.Format(WastedBytes);

    public string FilesSummaryText => $"{Files.Count:N0} identical files, {WastedText} reclaimable";

    public string SelectedSummaryText => $"{SelectedCount:N0} selected, {ByteFormatter.Format(SelectedBytes)}";

    public int SelectedCount => Files.Count(static file => file.IsSelected);

    public long SelectedBytes => Files.Where(static file => file.IsSelected).Sum(static file => file.SizeBytes);

    public DuplicateFileViewModel? LinkSurvivor => _linkSurvivor;

    public bool HasLinkSurvivor => LinkSurvivor is not null;

    internal bool CanMutateSelection => _canMutateSelection;

    public bool CanSelectForDeletion(DuplicateFileViewModel candidate)
    {
        return Files.Count(file => !file.IsSelected && file != candidate) >= 1;
    }

    public bool CanSetLinkSurvivor(DuplicateFileViewModel candidate) =>
        CanMutateSelection && Files.Contains(candidate) && !candidate.IsSelected;

    public void SetLinkSurvivor(DuplicateFileViewModel survivor)
    {
        if (!CanSetLinkSurvivor(survivor) || ReferenceEquals(_linkSurvivor, survivor))
        {
            return;
        }

        _linkSurvivor?.SetLinkSurvivorCore(false);
        _linkSurvivor = survivor;
        survivor.SetLinkSurvivorCore(true);
        OnPropertyChanged(nameof(LinkSurvivor));
        OnPropertyChanged(nameof(HasLinkSurvivor));
    }

    public void ClearLinkSurvivor(DuplicateFileViewModel? survivor = null)
    {
        if (_linkSurvivor is null || (survivor is not null && !ReferenceEquals(_linkSurvivor, survivor)))
        {
            return;
        }

        DuplicateFileViewModel previous = _linkSurvivor;
        _linkSurvivor = null;
        previous.SetLinkSurvivorCore(false);
        OnPropertyChanged(nameof(LinkSurvivor));
        OnPropertyChanged(nameof(HasLinkSurvivor));
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
                if (ReferenceEquals(_linkSurvivor, Files[index]))
                {
                    ClearLinkSurvivor(Files[index]);
                }

                Files.RemoveAt(index);
            }
        }

        NotifySelectionChanged();
        NotifyCurrentSummaryChanged();
    }

    public bool RemoveFile(DuplicateFileViewModel file)
    {
        bool removed = Files.Remove(file);
        if (removed)
        {
            if (ReferenceEquals(_linkSurvivor, file))
            {
                ClearLinkSurvivor(file);
            }

            NotifySelectionChanged();
            NotifyCurrentSummaryChanged();
        }

        return removed;
    }

    internal void NotifySelectionChanged()
    {
        if (_linkSurvivor?.IsSelected == true)
        {
            ClearLinkSurvivor(_linkSurvivor);
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedSummaryText));
        foreach (DuplicateFileViewModel file in Files)
        {
            file.NotifyKeptChanged();
        }
    }

    internal void NotifyCurrentSummaryChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(WastedBytes));
        OnPropertyChanged(nameof(WastedText));
        OnPropertyChanged(nameof(FilesSummaryText));
    }

    internal void SetCanMutateSelection(bool value)
    {
        if (_canMutateSelection == value)
        {
            return;
        }

        _canMutateSelection = value;
        foreach (DuplicateFileViewModel file in Files)
        {
            file.NotifyMutationAvailabilityChanged();
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
