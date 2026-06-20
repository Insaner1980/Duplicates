using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Duplicates.ViewModels;

public sealed partial class ResultsViewModel : ObservableObject
{
    private readonly ResultsStore _resultsStore;
    private readonly IFileActionService _fileActionService;
    private readonly ISettingsService _settingsService;
    private readonly List<DuplicateGroupViewModel> _allGroups = [];
    private IReadOnlyDictionary<string, bool>? _selectionSnapshot;

    public ResultsViewModel(ResultsStore resultsStore, IFileActionService fileActionService, ISettingsService settingsService)
    {
        _resultsStore = resultsStore;
        _fileActionService = fileActionService;
        _settingsService = settingsService;
        _resultsStore.ResultChanged += ResultsChanged;
    }

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedSortIndex { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Run a scan to see results.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeleteStatusOpen))]
    public partial string DeleteStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteProgressVisibility))]
    public partial bool IsDeleting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPaneVisibility))]
    public partial bool IsPreviewPaneOpen { get; set; } = true;

    [ObservableProperty]
    public partial string DeleteProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double DeleteProgressValue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteFailureDetailsVisibility))]
    public partial string DeleteFailureDetailsText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewVisibility))]
    [NotifyPropertyChangedFor(nameof(PreviewFileName))]
    [NotifyPropertyChangedFor(nameof(PreviewPath))]
    [NotifyPropertyChangedFor(nameof(PreviewDetails))]
    [NotifyPropertyChangedFor(nameof(PreviewImage))]
    [NotifyPropertyChangedFor(nameof(PreviewImageVisibility))]
    [NotifyPropertyChangedFor(nameof(PreviewMetadataVisibility))]
    public partial DuplicateFileViewModel? SelectedFile { get; set; }

    public Visibility BeforeFirstScanVisibility => _resultsStore.CurrentResult is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoDuplicatesVisibility => _resultsStore.CurrentResult is not null && _allGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ResultsVisibility => Groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SkippedFilesVisibility => _resultsStore.CurrentResult?.SkippedPaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string SkippedFilesSummaryText
    {
        get
        {
            int count = _resultsStore.CurrentResult?.SkippedPaths.Count ?? 0;
            return count == 1 ? "1 file was skipped" : $"{count:N0} files were skipped";
        }
    }

    public string SkippedFilesDetailsText => _resultsStore.CurrentResult is null
        ? string.Empty
        : string.Join(Environment.NewLine, _resultsStore.CurrentResult.SkippedPaths.Select(static skipped => $"{skipped.Path}: {skipped.Reason}"));

    public int GroupCount => _allGroups.Count;

    public int TotalDuplicateFiles => _allGroups.Sum(static group => Math.Max(0, group.Files.Count - 1));

    public long TotalReclaimableBytes => _allGroups.Sum(static group => group.Source.SizeBytes * Math.Max(0, group.Files.Count - 1));

    public string SummaryText => $"{GroupCount:N0} groups - {TotalDuplicateFiles:N0} duplicate files - {ByteFormatter.Format(TotalReclaimableBytes)} reclaimable total";

    public int SelectedFileCount => _allGroups.Sum(static group => group.SelectedCount);

    public long SelectedBytes => _allGroups.Sum(static group => group.SelectedBytes);

    public string DeleteButtonText => $"Delete {SelectedFileCount:N0} files ({ByteFormatter.Format(SelectedBytes)})";

    public bool CanDelete => !IsDeleting && SelectedFileCount > 0 && _allGroups.All(static group => group.SelectedCount < group.Files.Count);

    public bool IsDeleteStatusOpen => !string.IsNullOrWhiteSpace(DeleteStatusMessage);

    public Visibility DeleteProgressVisibility => IsDeleting ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeleteFailureDetailsVisibility => string.IsNullOrWhiteSpace(DeleteFailureDetailsText) ? Visibility.Collapsed : Visibility.Visible;

    public IReadOnlyList<DuplicateFileViewModel> SelectedFiles => _allGroups.SelectMany(static group => group.Files).Where(static file => file.IsSelected).ToArray();

    public bool CanUndoSelection => _selectionSnapshot is not null;

    public Visibility PreviewVisibility => SelectedFile is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PreviewPaneVisibility => IsPreviewPaneOpen ? Visibility.Visible : Visibility.Collapsed;

    public string PreviewFileName => SelectedFile?.FileName ?? "No file selected";

    public string PreviewPath => SelectedFile?.FullPath ?? "Select a file row to preview metadata.";

    public string PreviewDetails => SelectedFile is null
        ? string.Empty
        : $"{SelectedFile.SizeText} - created {SelectedFile.CreatedText} - modified {SelectedFile.ModifiedText}{BuildPreviewGroupDetails()}";

    public ImageSource? PreviewImage => SelectedFile is not null && IsImageExtension(SelectedFile.Extension)
        ? CreatePreviewImage(SelectedFile.FullPath)
        : null;

    public Visibility PreviewImageVisibility => PreviewImage is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PreviewMetadataVisibility => PreviewImage is null ? Visibility.Visible : Visibility.Collapsed;

    [RelayCommand]
    private void AutoSelectKeepNewest()
    {
        CaptureSelectionSnapshot();
        foreach (DuplicateGroupViewModel group in _allGroups)
        {
            group.ApplyKeepNewest();
        }

        RefreshSelectionTotals();
    }

    [RelayCommand]
    private void AutoSelectKeepOldest()
    {
        CaptureSelectionSnapshot();
        foreach (DuplicateGroupViewModel group in _allGroups)
        {
            group.ApplyKeepOldest();
        }

        RefreshSelectionTotals();
    }

    [RelayCommand]
    private void AutoSelectKeepShortestPath()
    {
        CaptureSelectionSnapshot();
        foreach (DuplicateGroupViewModel group in _allGroups)
        {
            group.ApplyKeepShortestPath();
        }

        RefreshSelectionTotals();
    }

    public void AutoSelectKeepPreferredFolder(string preferredFolder)
    {
        CaptureSelectionSnapshot();
        foreach (DuplicateGroupViewModel group in _allGroups)
        {
            group.ApplyKeepPreferredFolder(preferredFolder);
        }

        RefreshSelectionTotals();
    }

    [RelayCommand]
    private void SelectAll()
    {
        CaptureSelectionSnapshot();
        foreach (DuplicateGroupViewModel group in _allGroups)
        {
            group.SelectAllButNewest();
        }

        RefreshSelectionTotals();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        CaptureSelectionSnapshot();
        foreach (DuplicateGroupViewModel group in _allGroups)
        {
            group.ClearSelection();
        }

        RefreshSelectionTotals();
    }

    [RelayCommand(CanExecute = nameof(CanUndoSelection))]
    private void UndoSelection()
    {
        if (_selectionSnapshot is null)
        {
            return;
        }

        IReadOnlyDictionary<string, bool> snapshot = _selectionSnapshot;
        foreach (DuplicateFileViewModel file in _allGroups.SelectMany(static group => group.Files))
        {
            file.SetSelectedFromRule(snapshot.TryGetValue(file.FullPath, out bool wasSelected) && wasSelected);
        }

        foreach (DuplicateGroupViewModel group in _allGroups)
        {
            group.NotifySelectionChanged();
        }

        _selectionSnapshot = null;
        OnPropertyChanged(nameof(CanUndoSelection));
        UndoSelectionCommand.NotifyCanExecuteChanged();
        RefreshSelectionTotals();
    }

    [RelayCommand]
    private void ExcludeFile(DuplicateFileViewModel file)
    {
        DuplicateGroupViewModel? group = _allGroups.FirstOrDefault(group => group.Files.Contains(file));
        if (group is null)
        {
            return;
        }

        group.RemoveFile(file);
        if (SelectedFile == file)
        {
            SelectedFile = null;
        }

        if (group.Files.Count < 2)
        {
            _allGroups.Remove(group);
        }

        ApplySearchAndSort();
        RefreshAllComputedProperties();
    }

    [RelayCommand]
    private void TogglePreviewPane()
    {
        IsPreviewPaneOpen = !IsPreviewPaneOpen;
    }

    public async Task<DeleteSummary> DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        if (!CanDelete || _allGroups.Any(static group => group.SelectedCount >= group.Files.Count))
        {
            throw new InvalidOperationException("At least one file must remain in every duplicate group.");
        }

        IsDeleting = true;
        DeleteStatusMessage = "Deleting selected files...";
        DeleteFailureDetailsText = string.Empty;
        try
        {
            IReadOnlyList<DuplicateFileViewModel> selectedFiles = SelectedFiles;
            DeleteProgressText = $"0 of {selectedFiles.Count:N0} files processed";
            DeleteProgressValue = 0;
            DeleteSummary summary = await _fileActionService.DeleteAsync(
                selectedFiles,
                new InlineProgress<DeleteProgress>(UpdateDeleteProgress),
                cancellationToken);
            var deletedPaths = selectedFiles
                .Where(file => !summary.Failures.Any(failure => string.Equals(failure.Path, file.FullPath, StringComparison.OrdinalIgnoreCase)))
                .Select(static file => file.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (int index = _allGroups.Count - 1; index >= 0; index--)
            {
                _allGroups[index].RemoveDeleted(deletedPaths);
                if (_allGroups[index].Files.Count < 2)
                {
                    _allGroups.RemoveAt(index);
                }
            }

            DeleteStatusMessage = summary.Failures.Count == 0
                ? $"{summary.DeletedCount:N0} files deleted."
                : $"{summary.DeletedCount:N0} files deleted, {summary.Failures.Count:N0} could not be deleted.";
            DeleteFailureDetailsText = BuildFailureDetailsText(summary.Failures);
            ApplySearchAndSort();
            RefreshAllComputedProperties();
            return summary;
        }
        finally
        {
            IsDeleting = false;
        }
    }

    public void RefreshSelectionTotals()
    {
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(DeleteButtonText));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanUndoSelection));
        UndoSelectionCommand.NotifyCanExecuteChanged();
    }

    public void RefreshAllComputedProperties()
    {
        OnPropertyChanged(nameof(BeforeFirstScanVisibility));
        OnPropertyChanged(nameof(NoDuplicatesVisibility));
        OnPropertyChanged(nameof(ResultsVisibility));
        OnPropertyChanged(nameof(SkippedFilesVisibility));
        OnPropertyChanged(nameof(SkippedFilesSummaryText));
        OnPropertyChanged(nameof(SkippedFilesDetailsText));
        OnPropertyChanged(nameof(GroupCount));
        OnPropertyChanged(nameof(TotalDuplicateFiles));
        OnPropertyChanged(nameof(TotalReclaimableBytes));
        OnPropertyChanged(nameof(SummaryText));
        RefreshSelectionTotals();
    }

    partial void OnIsDeletingChanged(bool value)
    {
        RefreshSelectionTotals();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplySearchAndSort();
    }

    partial void OnSelectedSortIndexChanged(int value)
    {
        ApplySearchAndSort();
    }

    private void ResultsChanged(object? sender, ScanResult? result)
    {
        ReloadFromCurrentResult();
    }

    private void ReloadFromCurrentResult()
    {
        Groups.Clear();
        _allGroups.Clear();
        _selectionSnapshot = null;
        SelectedFile = null;
        ScanResult? result = _resultsStore.CurrentResult;
        if (result is null)
        {
            StatusMessage = "Run a scan to see results.";
            RefreshAllComputedProperties();
            return;
        }

        foreach (DuplicateGroup group in result.Groups)
        {
            DuplicateGroupViewModel groupViewModel = new(group);
            foreach (DuplicateFileViewModel file in groupViewModel.Files)
            {
                file.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(DuplicateFileViewModel.IsSelected))
                    {
                        RefreshSelectionTotals();
                    }
                };
            }

            _allGroups.Add(groupViewModel);
        }

        ApplySearchAndSort();
        StatusMessage = result.Groups.Count == 0
            ? "Every file in the scanned folders is unique."
            : $"{result.Groups.Count:N0} duplicate groups found.";
        RefreshAllComputedProperties();
    }

    private void CaptureSelectionSnapshot()
    {
        _selectionSnapshot = _allGroups
            .SelectMany(static group => group.Files)
            .ToDictionary(static file => file.FullPath, static file => file.IsSelected, StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(CanUndoSelection));
        UndoSelectionCommand.NotifyCanExecuteChanged();
    }

    private void UpdateDeleteProgress(DeleteProgress progress)
    {
        DeleteProgressText = $"{progress.ProcessedCount:N0} of {progress.TotalCount:N0} files processed";
        DeleteProgressValue = progress.TotalCount <= 0
            ? 0
            : Math.Clamp(progress.ProcessedCount * 100d / progress.TotalCount, 0, 100);
    }

    private static string BuildFailureDetailsText(IReadOnlyList<FileActionFailure> failures)
    {
        return failures.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, failures.Select(static failure => $"{failure.Path}: {failure.Reason}"));
    }

    private string BuildPreviewGroupDetails()
    {
        DuplicateGroupViewModel? group = SelectedFile is null
            ? null
            : _allGroups.FirstOrDefault(group => group.Files.Contains(SelectedFile));

        return group is null ? string.Empty : $" - group reclaimable {ByteFormatter.Format(group.Source.WastedBytes)}";
    }

    private static BitmapImage CreatePreviewImage(string path)
    {
        return new BitmapImage(new Uri(path))
        {
            DecodePixelWidth = 512,
        };
    }

    private void ApplySearchAndSort()
    {
        IEnumerable<DuplicateGroupViewModel> groups = _allGroups;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            groups = groups.Where(group => group.Files.Any(file =>
                file.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                file.FullPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase)));
        }

        groups = SelectedSortIndex switch
        {
            1 => groups.OrderByDescending(static group => group.Source.SizeBytes),
            2 => groups.OrderByDescending(static group => group.Files.Count),
            3 => groups.OrderBy(static group => group.Files[0].FileName, StringComparer.OrdinalIgnoreCase),
            4 => groups.OrderBy(static group => group.Files[0].Extension, StringComparer.OrdinalIgnoreCase),
            _ => groups.OrderByDescending(static group => group.Source.WastedBytes),
        };

        Groups.Clear();
        foreach (DuplicateGroupViewModel group in groups)
        {
            Groups.Add(group);
        }

        RefreshAllComputedProperties();
    }

    private static bool IsImageExtension(string extension)
    {
        return extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tiff" or ".tif" or ".webp" or ".heic" or ".heif" or ".raw" or ".cr2" or ".nef" or ".arw" or ".dng" or ".svg" or ".ico" or ".psd";
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public InlineProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }
}
