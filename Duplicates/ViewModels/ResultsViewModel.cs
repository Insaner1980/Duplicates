using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Engine.Analysis;
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
    private readonly IResultExportService _resultExportService;
    private readonly IFileLinkService _fileLinkService;
    private readonly IAppOperationCoordinator _operationCoordinator;
    private readonly List<DuplicateGroupViewModel> _allGroups = [];
    private IReadOnlyDictionary<string, bool>? _selectionSnapshot;
    private bool _isApplyingSelectionRule;
    private CancellationTokenSource? _actionCancellation;

    public ResultsViewModel(
        ResultsStore resultsStore,
        IFileActionService fileActionService,
        ISettingsService settingsService,
        IResultExportService resultExportService,
        IFileLinkService? fileLinkService = null,
        IAppOperationCoordinator? operationCoordinator = null)
    {
        _resultsStore = resultsStore;
        _fileActionService = fileActionService;
        _settingsService = settingsService;
        _resultExportService = resultExportService;
        _fileLinkService = fileLinkService ?? new FileLinkService();
        _operationCoordinator = operationCoordinator ?? new AppOperationCoordinator();
        _resultsStore.ResultChanged += ResultsChanged;
        _operationCoordinator.ActiveOperationChanged += OperationChanged;
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
    public partial bool IsPreviewPaneOpen { get; set; }

    [ObservableProperty]
    public partial string SelectionRuleText { get; set; } = "Keep newest";

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

    public long TotalReclaimableBytes => _allGroups.Sum(static group => group.WastedBytes);

    public string SummaryText => $"{GroupCount:N0} groups, {TotalDuplicateFiles:N0} duplicate files, {ByteFormatter.Format(TotalReclaimableBytes)} reclaimable total";

    public int SelectedFileCount => _allGroups.Sum(static group => group.SelectedCount);

    public int SelectedGroupCount => _allGroups.Count(static group => group.SelectedCount > 0);

    public long SelectedBytes => _allGroups.Sum(static group => group.SelectedBytes);

    public string DeleteButtonText => $"Delete {SelectedFileCount:N0} files ({ByteFormatter.Format(SelectedBytes)})";

    public bool CanDelete =>
        !IsDeleting &&
        _operationCoordinator.ActiveOperation is null &&
        SelectedFileCount > 0 &&
        _allGroups.All(static group => group.SelectedCount < group.Files.Count);

    public bool CanMove => CanDelete;

    public bool CanReplaceWithLinks =>
        !IsDeleting &&
        _operationCoordinator.ActiveOperation is null &&
        SelectedFileCount > 0 &&
        _allGroups
            .Where(static group => group.SelectedCount > 0)
            .All(static group =>
                group.LinkSurvivor is { IsSelected: false } survivor &&
                group.Files.Contains(survivor));

    public bool CanExport => !IsDeleting && _operationCoordinator.ActiveOperation is null;

    public bool CanStartNewScan => _operationCoordinator.ActiveOperation is null;

    public bool CanMutateSelection => !IsDeleting && _operationCoordinator.ActiveOperation is null;

    public bool IsDeleteStatusOpen => !string.IsNullOrWhiteSpace(DeleteStatusMessage);

    public Visibility DeleteProgressVisibility => IsDeleting ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeleteFailureDetailsVisibility => string.IsNullOrWhiteSpace(DeleteFailureDetailsText) ? Visibility.Collapsed : Visibility.Visible;

    public IReadOnlyList<DuplicateFileViewModel> SelectedFiles => _allGroups.SelectMany(static group => group.Files).Where(static file => file.IsSelected).ToArray();

    public bool CanUndoSelection => _selectionSnapshot is not null && CanMutateSelection;

    internal Action? BeforeLinkDispatch { get; set; }

    public Visibility PreviewVisibility => SelectedFile is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PreviewPaneVisibility => IsPreviewPaneOpen ? Visibility.Visible : Visibility.Collapsed;

    public string PreviewFileName => SelectedFile?.FileName ?? "No file selected";

    public string PreviewPath => SelectedFile?.FullPath ?? "Select a file row to preview metadata.";

    public string PreviewDetails => SelectedFile is null
        ? string.Empty
        : $"{SelectedFile.SizeText}, Created {SelectedFile.CreatedText}, Modified {SelectedFile.ModifiedText}{BuildPreviewGroupDetails()}";

    public ImageSource? PreviewImage => SelectedFile is not null && IsImageExtension(SelectedFile.Extension)
        ? CreatePreviewImage(SelectedFile.FullPath)
        : null;

    public Visibility PreviewImageVisibility => PreviewImage is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PreviewMetadataVisibility => PreviewImage is null ? Visibility.Visible : Visibility.Collapsed;

    [RelayCommand(CanExecute = nameof(CanMutateSelection))]
    private void AutoSelectKeepNewest()
    {
        ApplySelectionRule("Keep newest", static group => group.ApplyKeepNewest());
    }

    [RelayCommand(CanExecute = nameof(CanMutateSelection))]
    private void AutoSelectKeepOldest()
    {
        ApplySelectionRule("Keep oldest", static group => group.ApplyKeepOldest());
    }

    [RelayCommand(CanExecute = nameof(CanMutateSelection))]
    private void AutoSelectKeepShortestPath()
    {
        ApplySelectionRule("Keep shortest path", static group => group.ApplyKeepShortestPath());
    }

    public void AutoSelectKeepPreferredFolder(string preferredFolder)
    {
        if (!CanMutateSelection)
        {
            return;
        }

        ApplySelectionRule("Keep preferred folder", group => group.ApplyKeepPreferredFolder(preferredFolder));
    }

    [RelayCommand(CanExecute = nameof(CanMutateSelection))]
    private void ClearSelection()
    {
        ApplySelectionRule("Manual selection", static group => group.ClearSelection());
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
        SelectionRuleText = "Manual selection";
        OnPropertyChanged(nameof(CanUndoSelection));
        UndoSelectionCommand.NotifyCanExecuteChanged();
        RefreshSelectionTotals();
    }

    [RelayCommand(CanExecute = nameof(CanMutateSelection))]
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

        return await DeleteFilesAsync(SelectedFiles, cancellationToken);
    }

    public async Task<DeleteSummary> DeleteFileAsync(DuplicateFileViewModel file, CancellationToken cancellationToken)
    {
        DuplicateGroupViewModel? group = _allGroups.FirstOrDefault(group => group.Files.Contains(file));
        if (IsDeleting || group is null || group.Files.Count <= 1)
        {
            throw new InvalidOperationException("The file must belong to a duplicate group with another remaining copy.");
        }

        return await DeleteFilesAsync([file], cancellationToken);
    }

    public ExactLinkReplacementSnapshot CreateLinkReplacementSnapshot(LinkReplacementMode mode)
    {
        ExactResultsSession session = _resultsStore.CurrentSession ??
            throw new InvalidOperationException("Run a scan before replacing duplicates with links.");
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        DuplicateGroupViewModel[] actedGroups = _allGroups
            .Where(static group => group.SelectedCount > 0)
            .ToArray();
        if (actedGroups.Length == 0)
        {
            throw new InvalidOperationException("Select at least one duplicate to replace with a link.");
        }

        var requests = new LinkReplacementGroup[actedGroups.Length];
        var states = new ExactLinkReplacementGroupState[actedGroups.Length];
        int totalCount = 0;
        for (int index = 0; index < actedGroups.Length; index++)
        {
            DuplicateGroupViewModel group = actedGroups[index];
            DuplicateFileViewModel survivor = group.LinkSurvivor is { IsSelected: false } candidate &&
                group.Files.Contains(candidate)
                    ? candidate
                    : throw new InvalidOperationException(
                        "Every selected duplicate group requires one explicit nonselected link survivor.");
            DuplicateFileViewModel[] duplicates = group.Files
                .Where(static file => file.IsSelected)
                .ToArray();
            if (duplicates.Length == 0 || duplicates.Contains(survivor))
            {
                throw new InvalidOperationException(
                    "Every selected duplicate group requires one explicit nonselected link survivor.");
            }

            states[index] = new ExactLinkReplacementGroupState(group, survivor, duplicates);
            requests[index] = new LinkReplacementGroup(
                MapLinkFile(survivor),
                Array.AsReadOnly(duplicates.Select(MapLinkFile).ToArray()));
            totalCount += duplicates.Length;
        }

        string modeText = mode == LinkReplacementMode.HardLink ? "hard links" : "symbolic links";
        string survivors = string.Join(
            Environment.NewLine,
            requests.Select(group => $"{group.Survivor.FullPath} — {group.Duplicates.Count:N0} selected"));
        string developerMode = mode == LinkReplacementMode.SymbolicLink
            ? $"{Environment.NewLine}{Environment.NewLine}Symbolic links require Windows Developer Mode or existing link privilege. Duplicates never requests elevation."
            : string.Empty;
        string confirmation =
            $"Replace {totalCount:N0} selected {(totalCount == 1 ? "file" : "files")} with {modeText}?" +
            $"{Environment.NewLine}{Environment.NewLine}Survivors:{Environment.NewLine}{survivors}" +
            $"{Environment.NewLine}{Environment.NewLine}Each duplicate is first moved to an owned rollback path. " +
            "Only after the new link is verified is the rollback file sent to the Recycle Bin." +
            developerMode;
        return new ExactLinkReplacementSnapshot(session, mode, requests, states, confirmation);
    }

    public bool IsLinkReplacementSnapshotCurrent(ExactLinkReplacementSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(_resultsStore.CurrentSession, snapshot.Session))
        {
            return false;
        }

        foreach (ExactLinkReplacementGroupState state in snapshot.CanonicalStates)
        {
            if (!_allGroups.Contains(state.Group) ||
                !state.Group.Files.Contains(state.Survivor) ||
                !ReferenceEquals(state.Group.LinkSurvivor, state.Survivor) ||
                state.Survivor.IsSelected ||
                state.Duplicates.Length == 0 ||
                state.Duplicates.Any(file =>
                    !state.Group.Files.Contains(file) ||
                    !file.IsSelected ||
                    ReferenceEquals(file, state.Survivor)))
            {
                return false;
            }
        }

        HashSet<string> expectedSelection = snapshot.CanonicalStates
            .SelectMany(static state => state.Duplicates)
            .Select(static file => file.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> currentSelection = SelectedFiles
            .Select(static file => file.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expectedSelection.SetEquals(currentSelection);
    }

    public async Task<FileOperationSummary> ReplaceWithLinksAsync(
        ExactLinkReplacementSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsLinkReplacementSnapshotCurrent(snapshot))
        {
            throw new InvalidOperationException("The duplicate selection changed after confirmation.");
        }

        using CancellationTokenSource actionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_operationCoordinator.TryAcquire(
                new AppOperationDescriptor(AppOperationKind.ExactResultsAction),
                actionCancellation.Cancel,
                out IAppOperationLease? lease))
        {
            throw new InvalidOperationException("Another file operation is already running.");
        }

        _actionCancellation = actionCancellation;
        IsDeleting = true;
        DeleteStatusMessage = snapshot.Mode == LinkReplacementMode.HardLink
            ? "Replacing selected duplicates with hard links..."
            : "Replacing selected duplicates with symbolic links...";
        DeleteFailureDetailsText = string.Empty;
        int totalCount = snapshot.Groups.Sum(static group => group.Duplicates.Count);
        DeleteProgressText = $"0 of {totalCount:N0} files processed";
        DeleteProgressValue = 0;
        try
        {
            if (!IsLinkReplacementSnapshotCurrent(snapshot))
            {
                throw new InvalidOperationException("The duplicate selection changed after confirmation.");
            }

            BeforeLinkDispatch?.Invoke();
            if (!IsLinkReplacementSnapshotCurrent(snapshot))
            {
                throw new InvalidOperationException("The duplicate results changed before link replacement began.");
            }

            FileOperationSummary summary;
            try
            {
                summary = await _fileLinkService.ReplaceWithLinksAsync(
                    snapshot.Groups,
                    snapshot.Mode,
                    new InlineProgress<FileOperationProgress>(UpdateFileOperationProgress),
                    actionCancellation.Token);
            }
            catch (FileOperationCanceledException ex)
            {
                ApplyLinkSummary(snapshot, ex.Summary, wasCancelled: true);
                throw;
            }

            ApplyLinkSummary(snapshot, summary, wasCancelled: false);
            return summary;
        }
        finally
        {
            IsDeleting = false;
            _actionCancellation = null;
            lease!.Dispose();
        }
    }

    public async Task<FileOperationSummary> MoveSelectedAsync(
        string destinationFolder,
        MoveCollisionBehavior collisionBehavior,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DuplicateFileViewModel> files = SelectedFiles;
        EnsureSurvivorInvariant(files);
        using CancellationTokenSource actionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_operationCoordinator.TryAcquire(
                new AppOperationDescriptor(AppOperationKind.ExactResultsAction),
                actionCancellation.Cancel,
                out IAppOperationLease? lease))
        {
            throw new InvalidOperationException("Another file operation is already running.");
        }

        _actionCancellation = actionCancellation;
        IsDeleting = true;
        DeleteStatusMessage = files.Count == 1 ? "Moving file..." : "Moving selected files...";
        DeleteFailureDetailsText = string.Empty;
        try
        {
            DeleteProgressText = $"0 of {files.Count:N0} files processed";
            DeleteProgressValue = 0;
            FileOperationSummary summary;
            try
            {
                summary = await _fileActionService.MoveAsync(
                    MapTargets(files),
                    destinationFolder,
                    collisionBehavior,
                    new InlineProgress<FileOperationProgress>(UpdateFileOperationProgress),
                    actionCancellation.Token);
            }
            catch (FileOperationCanceledException ex)
            {
                ApplyMoveSummary(ex.Summary, wasCancelled: true);
                throw;
            }

            ApplyMoveSummary(summary, wasCancelled: false);
            return summary;
        }
        finally
        {
            IsDeleting = false;
            _actionCancellation = null;
            lease!.Dispose();
        }
    }

    private void ApplyMoveSummary(FileOperationSummary summary, bool wasCancelled)
    {
        HashSet<string> movedPaths = summary.Results
            .Where(static result => result.Succeeded)
            .Select(static result => result.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveSuccessfulPaths(movedPaths);
        FileActionFailure[] failures = summary.Results
            .Where(static result => !result.Succeeded)
            .Select(static result => result.Failure!)
            .ToArray();
        int movedCount = summary.Results.Count(static result => result.Succeeded);
        DeleteStatusMessage = wasCancelled
            ? $"Move cancelled after {movedCount:N0} {(movedCount == 1 ? "file" : "files")} moved."
            : failures.Length == 0
                ? movedCount == 1 ? "1 file moved." : $"{movedCount:N0} files moved."
                : $"{movedCount:N0} files moved, {failures.Length:N0} could not be moved.";
        DeleteFailureDetailsText = BuildFailureDetailsText(failures);
        ApplySearchAndSort();
        RefreshAllComputedProperties();
    }

    public async Task ExportAsync(
        ResultExportFormat format,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ExactResultsSession session = _resultsStore.CurrentSession ??
            throw new InvalidOperationException("Run a scan before exporting results.");
        ResultExportItem[] items = _allGroups.SelectMany(group => group.Files.Select(file => new ResultExportItem(
            file.FullPath,
            FileActionTargetKind.File,
            "Byte-identical duplicate",
            null,
            group.Source.ContentHash.ToString("X16", CultureInfo.InvariantCulture),
            null,
            file.SizeBytes,
            file.File.CreatedUtc,
            file.File.ModifiedUtc,
            new Dictionary<string, string>(StringComparer.Ordinal))))
            .ToArray();
        SkippedPath[] skippedPaths = session.Result.SkippedPaths.Select(static skipped => new SkippedPath
        {
            Path = skipped.Path,
            Reason = skipped.Reason,
        }).ToArray();
        var snapshot = new ResultExportSnapshot(
            ToolKind.DuplicateFiles,
            session.CompletedAtUtc,
            BuildScopeSummary(session.Scope),
            items,
            skippedPaths);
        using CancellationTokenSource actionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_operationCoordinator.TryAcquire(
                new AppOperationDescriptor(AppOperationKind.ExactResultsAction),
                actionCancellation.Cancel,
                out IAppOperationLease? lease))
        {
            throw new InvalidOperationException("Another file operation is already running.");
        }

        _actionCancellation = actionCancellation;
        IsDeleting = true;
        try
        {
            await _resultExportService.ExportAsync(
                snapshot,
                format,
                destinationPath,
                actionCancellation.Token);
        }
        finally
        {
            IsDeleting = false;
            _actionCancellation = null;
            lease!.Dispose();
        }
    }

    private async Task<DeleteSummary> DeleteFilesAsync(
        IReadOnlyList<DuplicateFileViewModel> files,
        CancellationToken cancellationToken)
    {
        EnsureSurvivorInvariant(files);
        using CancellationTokenSource actionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_operationCoordinator.TryAcquire(
                new AppOperationDescriptor(AppOperationKind.ExactResultsAction),
                actionCancellation.Cancel,
                out IAppOperationLease? lease))
        {
            throw new InvalidOperationException("Another file operation is already running.");
        }

        _actionCancellation = actionCancellation;
        IsDeleting = true;
        DeleteStatusMessage = files.Count == 1 ? "Deleting file..." : "Deleting selected files...";
        DeleteFailureDetailsText = string.Empty;
        try
        {
            DeleteProgressText = $"0 of {files.Count:N0} files processed";
            DeleteProgressValue = 0;
            DeleteSummary summary;
            try
            {
                summary = await _fileActionService.DeleteAsync(
                    MapTargets(files),
                    new InlineProgress<DeleteProgress>(UpdateDeleteProgress),
                    actionCancellation.Token);
            }
            catch (DeleteOperationCanceledException ex)
            {
                ApplyDeleteSummary(files, ex.Summary, wasCancelled: true);
                throw;
            }

            ApplyDeleteSummary(files, summary, wasCancelled: false);
            return summary;
        }
        finally
        {
            IsDeleting = false;
            _actionCancellation = null;
            lease!.Dispose();
        }
    }

    public void RefreshSelectionTotals()
    {
        foreach (DuplicateGroupViewModel group in _allGroups)
        {
            group.SetCanMutateSelection(CanMutateSelection);
        }

        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(SelectedGroupCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(DeleteButtonText));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanMove));
        OnPropertyChanged(nameof(CanReplaceWithLinks));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanStartNewScan));
        OnPropertyChanged(nameof(CanMutateSelection));
        OnPropertyChanged(nameof(CanUndoSelection));
        AutoSelectKeepNewestCommand.NotifyCanExecuteChanged();
        AutoSelectKeepOldestCommand.NotifyCanExecuteChanged();
        AutoSelectKeepShortestPathCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        ExcludeFileCommand.NotifyCanExecuteChanged();
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

    partial void OnSelectedFileChanged(DuplicateFileViewModel? value)
    {
        if (value is not null)
        {
            IsPreviewPaneOpen = true;
        }
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
        IsPreviewPaneOpen = false;
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
                    if (args.PropertyName is nameof(DuplicateFileViewModel.IsSelected) or
                        nameof(DuplicateFileViewModel.IsLinkSurvivor))
                    {
                        if (args.PropertyName == nameof(DuplicateFileViewModel.IsSelected) &&
                            !_isApplyingSelectionRule)
                        {
                            SelectionRuleText = "Manual selection";
                        }

                        RefreshSelectionTotals();
                    }
                };
            }

            _allGroups.Add(groupViewModel);
        }

        if (_allGroups.Count > 0)
        {
            ApplySelectionRule("Keep newest", static group => group.ApplyKeepNewest());
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

    private void ApplySelectionRule(string ruleText, Action<DuplicateGroupViewModel> applyRule)
    {
        CaptureSelectionSnapshot();
        _isApplyingSelectionRule = true;
        try
        {
            foreach (DuplicateGroupViewModel group in _allGroups)
            {
                applyRule(group);
            }
        }
        finally
        {
            _isApplyingSelectionRule = false;
        }

        SelectionRuleText = ruleText;
        RefreshSelectionTotals();
    }

    private void UpdateDeleteProgress(DeleteProgress progress)
    {
        DeleteProgressText = $"{progress.ProcessedCount:N0} of {progress.TotalCount:N0} files processed";
        DeleteProgressValue = progress.TotalCount <= 0
            ? 0
            : Math.Clamp(progress.ProcessedCount * 100d / progress.TotalCount, 0, 100);
    }

    private void ApplyDeleteSummary(
        IReadOnlyList<DuplicateFileViewModel> files,
        DeleteSummary summary,
        bool wasCancelled)
    {
        IEnumerable<string> deletedPaths = summary.DeletedPaths ?? files
            .Where(file => !summary.Failures.Any(failure =>
                string.Equals(failure.Path, file.FullPath, StringComparison.OrdinalIgnoreCase)))
            .Select(static file => file.FullPath);
        RemoveSuccessfulPaths(deletedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
        DeleteStatusMessage = wasCancelled
            ? $"Delete cancelled after {summary.DeletedCount:N0} " +
                (summary.DeletedCount == 1 ? "file deleted." : "files deleted.")
            : summary.Failures.Count == 0
                ? summary.DeletedCount == 1
                    ? "1 file deleted."
                    : $"{summary.DeletedCount:N0} files deleted."
                : $"{summary.DeletedCount:N0} files deleted, {summary.Failures.Count:N0} could not be deleted.";
        DeleteFailureDetailsText = BuildFailureDetailsText(summary.Failures);
        ApplySearchAndSort();
        RefreshAllComputedProperties();
    }

    private void ApplyLinkSummary(
        ExactLinkReplacementSnapshot snapshot,
        FileOperationSummary summary,
        bool wasCancelled)
    {
        if (ReferenceEquals(_resultsStore.CurrentSession, snapshot.Session))
        {
            HashSet<string> replacedPaths = summary.Results
                .Where(static result => result.Succeeded)
                .Select(static result => result.SourcePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            RemoveSuccessfulPaths(replacedPaths);
            ApplySearchAndSort();
            RefreshAllComputedProperties();
        }

        FileActionFailure[] failures = summary.Results
            .Where(static result => !result.Succeeded)
            .Select(static result => result.Failure!)
            .ToArray();
        int succeeded = summary.Results.Count(static result => result.Succeeded);
        DeleteStatusMessage = wasCancelled
            ? $"Link replacement cancelled after {succeeded:N0} {(succeeded == 1 ? "file" : "files")} committed."
            : failures.Length == 0
                ? succeeded == 1 ? "1 duplicate replaced with a link." : $"{succeeded:N0} duplicates replaced with links."
                : $"{succeeded:N0} duplicates replaced, {failures.Length:N0} could not be replaced.";
        DeleteFailureDetailsText = BuildFailureDetailsText(failures);
    }

    private void UpdateFileOperationProgress(FileOperationProgress progress)
    {
        DeleteProgressText = $"{progress.ProcessedCount:N0} of {progress.TotalCount:N0} files processed";
        DeleteProgressValue = progress.TotalCount <= 0
            ? 0
            : Math.Clamp(progress.ProcessedCount * 100d / progress.TotalCount, 0, 100);
    }

    private void EnsureSurvivorInvariant(IReadOnlyList<DuplicateFileViewModel> files)
    {
        if (files.Count == 0 || _allGroups.Any(group =>
                group.Files.Count(file => files.Contains(file)) >= group.Files.Count))
        {
            throw new InvalidOperationException("At least one file must remain in every duplicate group.");
        }
    }

    private static FileActionTarget[] MapTargets(IReadOnlyList<DuplicateFileViewModel> files) =>
        files.Select(static file => new FileActionTarget(
            file.FullPath,
            file.SizeBytes,
            FileActionTargetKind.File))
            .ToArray();

    private static LinkReplacementFile MapLinkFile(DuplicateFileViewModel file) => new(
        file.FullPath,
        file.SizeBytes,
        file.File.ModifiedUtc);

    private void RemoveSuccessfulPaths(IReadOnlySet<string> paths)
    {
        if (SelectedFile is not null && paths.Contains(SelectedFile.FullPath))
        {
            SelectedFile = null;
        }

        for (int index = _allGroups.Count - 1; index >= 0; index--)
        {
            _allGroups[index].RemoveDeleted(paths);
            if (_allGroups[index].Files.Count < 2)
            {
                _allGroups.RemoveAt(index);
            }
        }
    }

    private static string BuildScopeSummary(AnalysisScope scope)
    {
        int folderCount = scope.IncludedFolders.Count;
        int fileCount = scope.IncludedFiles.Count;
        int excludedCount = scope.ExcludedPaths.Count;
        return $"{folderCount:N0} {(folderCount == 1 ? "folder" : "folders")}, " +
            $"{fileCount:N0} {(fileCount == 1 ? "file" : "files")}, " +
            $"{excludedCount:N0} excluded; subfolders {(scope.IncludeSubfolders ? "included" : "excluded")}";
    }

    private static string BuildFailureDetailsText(IReadOnlyList<FileActionFailure> failures)
    {
        return failures.Count == 0
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                failures.Select(static failure => failure.RecoveryPath is null
                    ? $"{failure.Path}: {failure.Reason}"
                    : $"{failure.Path}: {failure.Reason} Recovery path: {failure.RecoveryPath}"));
    }

    private string BuildPreviewGroupDetails()
    {
        DuplicateGroupViewModel? group = SelectedFile is null
            ? null
            : _allGroups.FirstOrDefault(group => group.Files.Contains(SelectedFile));

        return group is null ? string.Empty : $", Group reclaimable {group.WastedText}";
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
            _ => groups.OrderByDescending(static group => group.WastedBytes),
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

    private void OperationChanged(object? sender, EventArgs e)
    {
        RefreshSelectionTotals();
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
