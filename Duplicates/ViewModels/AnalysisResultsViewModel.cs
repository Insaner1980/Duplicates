using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Microsoft.UI.Xaml;

namespace Duplicates.ViewModels;

public sealed partial class AnalysisResultsViewModel : ObservableObject
{
    private readonly AnalysisSessionStore _sessionStore;
    private readonly IFileActionService? _fileActionService;
    private readonly IResultExportService? _resultExportService;
    private readonly Func<string, CancellationToken, ValueTask<DetectedFileType?>> _detectFileAsync;
    private readonly IFileFormatProbe? _fileFormatProbe;
    private readonly IAnalysisService? _analysisService;
    private readonly IMediaPreviewLoader? _mediaPreviewLoader;
    private readonly IAppOperationCoordinator _operationCoordinator;
    private readonly List<PathFindingViewModel> _allFindings = [];
    private readonly List<SimilarityGroupViewModel> _allGroups = [];
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _actionCancellation;
    private long _previewRequestGeneration;

    public AnalysisResultsViewModel(AnalysisSessionStore sessionStore)
        : this(sessionStore, null, null, FileSignatureDetector.DetectFileAsync, null, null, null)
    {
    }

    public AnalysisResultsViewModel(
        AnalysisSessionStore sessionStore,
        IFileActionService? fileActionService,
        IResultExportService? resultExportService)
        : this(sessionStore, fileActionService, resultExportService, FileSignatureDetector.DetectFileAsync, null, null, null)
    {
    }

    public AnalysisResultsViewModel(
        AnalysisSessionStore sessionStore,
        IFileActionService? fileActionService,
        IResultExportService? resultExportService,
        Func<string, CancellationToken, ValueTask<DetectedFileType?>> detectFileAsync)
        : this(sessionStore, fileActionService, resultExportService, detectFileAsync, null, null, null)
    {
    }

    public AnalysisResultsViewModel(
        AnalysisSessionStore sessionStore,
        IFileActionService? fileActionService,
        IResultExportService? resultExportService,
        IAnalysisService? analysisService,
        IMediaPreviewLoader? mediaPreviewLoader)
        : this(
            sessionStore,
            fileActionService,
            resultExportService,
            FileSignatureDetector.DetectFileAsync,
            null,
            analysisService,
            mediaPreviewLoader)
    {
    }

    public AnalysisResultsViewModel(
        AnalysisSessionStore sessionStore,
        IFileActionService? fileActionService,
        IResultExportService? resultExportService,
        IAnalysisService? analysisService,
        IMediaPreviewLoader? mediaPreviewLoader,
        IAppOperationCoordinator operationCoordinator)
        : this(
            sessionStore,
            fileActionService,
            resultExportService,
            FileSignatureDetector.DetectFileAsync,
            null,
            analysisService,
            mediaPreviewLoader,
            operationCoordinator)
    {
    }

    public AnalysisResultsViewModel(
        AnalysisSessionStore sessionStore,
        IFileActionService? fileActionService,
        IResultExportService? resultExportService,
        Func<string, CancellationToken, ValueTask<DetectedFileType?>> detectFileAsync,
        IFileFormatProbe? fileFormatProbe,
        IAnalysisService? analysisService = null,
        IMediaPreviewLoader? mediaPreviewLoader = null,
        IAppOperationCoordinator? operationCoordinator = null)
    {
        _sessionStore = sessionStore;
        _fileActionService = fileActionService;
        _resultExportService = resultExportService;
        _detectFileAsync = detectFileAsync;
        _fileFormatProbe = fileFormatProbe;
        _analysisService = analysisService;
        _mediaPreviewLoader = mediaPreviewLoader;
        _operationCoordinator = operationCoordinator ?? new AppOperationCoordinator();
        _sessionStore.ResultChanged += ResultsChanged;
        _operationCoordinator.ActiveOperationChanged += OperationChanged;
    }

    public event EventHandler<ToolKind>? NewAnalysisRequested;

    internal Action? SimilaritySelectionValidated { get; set; }

    public ObservableCollection<PathFindingViewModel> Findings { get; } = [];

    public ObservableCollection<SimilarityGroupViewModel> Groups { get; } = [];

    public ObservableCollection<object> ResultItems { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedSortIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPath))]
    [NotifyPropertyChangedFor(nameof(PreviewTitle))]
    [NotifyPropertyChangedFor(nameof(PreviewSummary))]
    [NotifyPropertyChangedFor(nameof(PreviewMetadata))]
    public partial object? SelectedResult { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPath))]
    [NotifyPropertyChangedFor(nameof(PreviewTitle))]
    [NotifyPropertyChangedFor(nameof(PreviewSummary))]
    [NotifyPropertyChangedFor(nameof(PreviewMetadata))]
    public partial SimilarityItemViewModel? SelectedSimilarityPreviewItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimilarityPreviewVisibility))]
    public partial MediaPreviewData? SimilarityPreview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimilarityPreviewStatusVisibility))]
    public partial string SimilarityPreviewStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPreviewPaneOpen { get; set; }

    [ObservableProperty]
    public partial string ActionStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActOnSelection))]
    [NotifyPropertyChangedFor(nameof(CanRenameSelection))]
    [NotifyPropertyChangedFor(nameof(RenameSelection))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    [NotifyPropertyChangedFor(nameof(CanStartNewAnalysis))]
    [NotifyPropertyChangedFor(nameof(CanMutateSelection))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))]
    public partial bool IsActionRunning { get; set; }

    public Visibility BeforeFirstAnalysisVisibility => _sessionStore.CurrentSession is null
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility NoFindingsVisibility => _sessionStore.CurrentSession is not null &&
        _allFindings.Count == 0 &&
        _allGroups.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ResultsVisibility => ResultItems.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SkippedPathsVisibility => _sessionStore.CurrentSession?.Result.SkippedPaths.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool HasSkippedPaths => _sessionStore.CurrentSession?.Result.SkippedPaths.Count > 0;

    public string SkippedPathsSummaryText
    {
        get
        {
            int count = _sessionStore.CurrentSession?.Result.SkippedPaths.Count ?? 0;
            return count == 1 ? "1 path was skipped" : $"{count:N0} paths were skipped";
        }
    }

    public string SkippedPathsDetailsText => _sessionStore.CurrentSession is null
        ? string.Empty
        : string.Join(
            Environment.NewLine,
            _sessionStore.CurrentSession.Result.SkippedPaths.Select(
                static skipped => $"{skipped.Path}: {skipped.Reason}"));

    public string SummaryText => _sessionStore.CurrentSession is null
        ? "Run an analysis to see results."
        : $"{_allFindings.Count:N0} findings, {_allGroups.Count:N0} similarity groups";

    public int SelectedItemCount => SelectedFindings.Count + SelectedSimilarityItems.Count;

    public long SelectedBytes => SelectedFindings.Sum(static item => item.SizeBytes) +
        SelectedSimilarityItems.Sum(static item => item.SizeBytes);

    public bool CanActOnSelection =>
        !IsActionRunning &&
        _operationCoordinator.ActiveOperation is null &&
        IsBulkMutationToolSupported &&
        SelectedItemCount > 0;

    public bool CanRenameSelection =>
        !IsActionRunning &&
        _operationCoordinator.ActiveOperation is null &&
        SupportsRenameDialog(_sessionStore.CurrentSession) &&
        SelectedFindings.Count == 1 &&
        SelectedSimilarityItems.Count == 0;

    public PathFindingViewModel? RenameSelection => CanRenameSelection
        ? SelectedFindings[0]
        : null;

    public bool CanExport => !IsActionRunning && _operationCoordinator.ActiveOperation is null;

    public bool CanStartNewAnalysis => _operationCoordinator.ActiveOperation is null;

    public bool CanMutateSelection => !IsActionRunning && _operationCoordinator.ActiveOperation is null;

    public IReadOnlyList<PathFindingViewModel> SelectedFindings =>
        _allFindings.Where(static item => item.IsSelected).ToArray();

    public IReadOnlyList<SimilarityItemViewModel> SelectedSimilarityItems =>
        _allGroups.SelectMany(static group => group.Items).Where(static item => item.IsSelected).ToArray();

    public Visibility SimilarityPreviewVisibility => SimilarityPreview is null
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility SimilarityPreviewStatusVisibility => string.IsNullOrWhiteSpace(SimilarityPreviewStatusText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string PreviewPath => SelectedSimilarityPreviewItem is not null
        ? SelectedSimilarityPreviewItem.FullPath
        : SelectedResult switch
        {
            PathFindingViewModel finding => finding.FullPath,
            SimilarityGroupViewModel group => group.FullPath,
            _ => "Select a result to preview details.",
        };

    public string PreviewTitle => SelectedSimilarityPreviewItem is not null
        ? SelectedSimilarityPreviewItem.DisplayName
        : SelectedResult switch
        {
            PathFindingViewModel finding => finding.DisplayName,
            SimilarityGroupViewModel group => group.DisplayName,
            _ => "No result selected",
        };

    public string PreviewSummary => SelectedSimilarityPreviewItem is not null
        ? SelectedSimilarityPreviewItem.SummaryText
        : SelectedResult switch
        {
            PathFindingViewModel finding => finding.SummaryText,
            SimilarityGroupViewModel group => group.SummaryText,
            _ => string.Empty,
        };

    public string PreviewMetadata => SelectedSimilarityPreviewItem is not null
        ? SelectedSimilarityPreviewItem.MetadataText
        : SelectedResult switch
        {
            PathFindingViewModel finding => finding.MetadataText,
            SimilarityGroupViewModel group => group.MetadataText,
            _ => string.Empty,
        };

    [RelayCommand(CanExecute = nameof(CanStartNewAnalysis))]
    private void NewAnalysis()
    {
        if (!CanStartNewAnalysis)
        {
            return;
        }

        ToolKind tool = _sessionStore.CurrentSession?.Tool ?? ToolKind.EmptyFolders;
        SearchText = string.Empty;
        SelectedSortIndex = 0;
        SelectedResult = null;
        ResetSimilarityPreview();
        IsPreviewPaneOpen = false;
        _sessionStore.Clear();
        NewAnalysisRequested?.Invoke(this, tool);
    }

    [RelayCommand(CanExecute = nameof(CanMutateSelection))]
    private void ClearSelection()
    {
        foreach (PathFindingViewModel finding in _allFindings)
        {
            finding.IsSelected = false;
        }

        foreach (SimilarityItemViewModel item in _allGroups.SelectMany(static group => group.Items))
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand]
    private void TogglePreviewPane() => IsPreviewPaneOpen = !IsPreviewPaneOpen;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SelectSimilarityPreviewItemAsync(SimilarityItemViewModel? item)
    {
        AnalysisSession? session = _sessionStore.CurrentSession;
        if (item is null || session is null || !IsCanonicalSimilarityItem(item))
        {
            return;
        }

        SelectedSimilarityPreviewItem = item;
        IsPreviewPaneOpen = true;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        if (session.Tool == ToolKind.MusicDuplicates)
        {
            _previewCancellation = null;
            _previewRequestGeneration++;
            SimilarityPreview = null;
            SimilarityPreviewStatusText = string.Empty;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        long requestGeneration = ++_previewRequestGeneration;
        SimilarityPreview = null;
        SimilarityPreviewStatusText = "Loading preview...";

        if (_mediaPreviewLoader is null)
        {
            SimilarityPreviewStatusText = "Preview unavailable";
            _previewCancellation = null;
            cancellation.Dispose();
            return;
        }

        try
        {
            MediaPreviewData preview = await _mediaPreviewLoader.LoadAsync(
                session.Tool,
                item.Source,
                cancellation.Token);
            if (cancellation.IsCancellationRequested ||
                requestGeneration != _previewRequestGeneration ||
                !ReferenceEquals(_sessionStore.CurrentSession, session) ||
                !ReferenceEquals(SelectedSimilarityPreviewItem, item) ||
                !IsCanonicalSimilarityItem(item))
            {
                return;
            }

            if (preview.Width <= 0 ||
                preview.Height <= 0 ||
                preview.Width > 512 ||
                preview.Height > 512 ||
                preview.Bgra8?.Length != checked(preview.Width * preview.Height * 4))
            {
                throw new InvalidDataException("The preview is structurally invalid.");
            }

            SimilarityPreview = preview;
            SimilarityPreviewStatusText = string.Empty;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (IsExpectedSimilarityProviderFailure(ex))
        {
            if (!cancellation.IsCancellationRequested &&
                requestGeneration == _previewRequestGeneration &&
                ReferenceEquals(_sessionStore.CurrentSession, session) &&
                ReferenceEquals(SelectedSimilarityPreviewItem, item) &&
                IsCanonicalSimilarityItem(item))
            {
                SimilarityPreview = null;
                SimilarityPreviewStatusText = "Preview unavailable";
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    public Task<DeleteSummary> DeleteSelectedAsync(CancellationToken cancellationToken) =>
        RunCoordinatedActionAsync(DeleteSelectedCoreAsync, cancellationToken);

    private async Task<DeleteSummary> DeleteSelectedCoreAsync(CancellationToken cancellationToken)
    {
        EnsureSelectedMutationIsSupported();
        AnalysisSession? initiatingSession = _sessionStore.CurrentSession;
        IFileActionService fileActions = _fileActionService ??
            throw new InvalidOperationException("File actions are not configured.");
        if (IsSimilarityActionSession(initiatingSession))
        {
            return await DeleteSelectedSimilarityAsync(
                initiatingSession!,
                fileActions,
                cancellationToken);
        }

        SelectionTargets selection = await BuildValidatedSelectionAsync(
            initiatingSession,
            cancellationToken);
        selection = RejectBrokenTargetsAfterSessionChange(initiatingSession, selection);
        if (selection.SelectedCount == 0)
        {
            throw new InvalidOperationException("Select at least one result first.");
        }

        IsActionRunning = true;
        ActionStatusMessage = "Deleting selected items...";
        try
        {
            DeleteSummary serviceSummary;
            try
            {
                serviceSummary = selection.Targets.Count == 0
                    ? new DeleteSummary(0, 0, [])
                    : await fileActions.DeleteAsync(selection.Targets, null, cancellationToken);
            }
            catch (DeleteOperationCanceledException ex)
            {
                ApplyDeleteSummary(ex.Summary, selection, initiatingSession, wasCancelled: true);
                throw;
            }

            return ApplyDeleteSummary(serviceSummary, selection, initiatingSession, wasCancelled: false);
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    public Task<FileOperationSummary> MoveSelectedAsync(
        string destinationFolder,
        MoveCollisionBehavior collisionBehavior,
        CancellationToken cancellationToken) => RunCoordinatedActionAsync(
            token => MoveSelectedCoreAsync(destinationFolder, collisionBehavior, token),
            cancellationToken);

    private async Task<FileOperationSummary> MoveSelectedCoreAsync(
        string destinationFolder,
        MoveCollisionBehavior collisionBehavior,
        CancellationToken cancellationToken)
    {
        EnsureSelectedMutationIsSupported();
        AnalysisSession? initiatingSession = _sessionStore.CurrentSession;
        IFileActionService fileActions = _fileActionService ??
            throw new InvalidOperationException("File actions are not configured.");
        if (IsSimilarityActionSession(initiatingSession))
        {
            return await MoveSelectedSimilarityAsync(
                initiatingSession!,
                fileActions,
                destinationFolder,
                collisionBehavior,
                cancellationToken);
        }

        SelectionTargets selection = await BuildValidatedSelectionAsync(
            initiatingSession,
            cancellationToken);
        selection = RejectBrokenTargetsAfterSessionChange(initiatingSession, selection);
        if (selection.SelectedCount == 0)
        {
            throw new InvalidOperationException("Select at least one result first.");
        }

        IsActionRunning = true;
        ActionStatusMessage = "Moving selected items...";
        try
        {
            FileOperationSummary serviceSummary;
            try
            {
                serviceSummary = selection.Targets.Count == 0
                    ? new FileOperationSummary([], 0)
                    : await fileActions.MoveAsync(
                        selection.Targets,
                        destinationFolder,
                        collisionBehavior,
                        null,
                        cancellationToken);
            }
            catch (FileOperationCanceledException ex)
            {
                ApplyMoveSummary(ex.Summary, selection.Failures, initiatingSession, wasCancelled: true);
                throw;
            }

            return ApplyMoveSummary(serviceSummary, selection.Failures, initiatingSession, wasCancelled: false);
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    private async Task<DeleteSummary> DeleteSelectedSimilarityAsync(
        AnalysisSession initiatingSession,
        IFileActionService fileActions,
        CancellationToken cancellationToken)
    {
        SimilarityActionSnapshot snapshot = CreateSimilarityActionSnapshot(initiatingSession);
        IAnalysisService analysisService = _analysisService ??
            throw new InvalidOperationException("Similarity revalidation is not configured.");
        IsActionRunning = true;
        ActionStatusMessage = "Deleting selected items...";
        try
        {
            SelectionTargets selection = await BuildValidatedSimilaritySelectionAsync(
                snapshot,
                analysisService,
                cancellationToken);
            SimilaritySelectionValidated?.Invoke();
            DeleteSummary serviceSummary;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                selection = RejectSimilarityTargetsAfterSessionChange(snapshot, selection);
                serviceSummary = selection.Targets.Count == 0
                    ? new DeleteSummary(0, 0, [])
                    : await fileActions.DeleteAsync(selection.Targets, null, cancellationToken);
            }
            catch (DeleteOperationCanceledException ex)
            {
                ApplyDeleteSummary(ex.Summary, selection, initiatingSession, wasCancelled: true, snapshot);
                throw;
            }

            return ApplyDeleteSummary(
                serviceSummary,
                selection,
                initiatingSession,
                wasCancelled: false,
                snapshot);
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    private async Task<FileOperationSummary> MoveSelectedSimilarityAsync(
        AnalysisSession initiatingSession,
        IFileActionService fileActions,
        string destinationFolder,
        MoveCollisionBehavior collisionBehavior,
        CancellationToken cancellationToken)
    {
        SimilarityActionSnapshot snapshot = CreateSimilarityActionSnapshot(initiatingSession);
        IAnalysisService analysisService = _analysisService ??
            throw new InvalidOperationException("Similarity revalidation is not configured.");
        IsActionRunning = true;
        ActionStatusMessage = "Moving selected items...";
        try
        {
            SelectionTargets selection = await BuildValidatedSimilaritySelectionAsync(
                snapshot,
                analysisService,
                cancellationToken);
            SimilaritySelectionValidated?.Invoke();
            FileOperationSummary serviceSummary;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                selection = RejectSimilarityTargetsAfterSessionChange(snapshot, selection);
                serviceSummary = selection.Targets.Count == 0
                    ? new FileOperationSummary([], 0)
                    : await fileActions.MoveAsync(
                        selection.Targets,
                        destinationFolder,
                        collisionBehavior,
                        null,
                        cancellationToken);
            }
            catch (FileOperationCanceledException ex)
            {
                ApplyMoveSummary(ex.Summary, selection.Failures, initiatingSession, wasCancelled: true, snapshot);
                throw;
            }

            return ApplyMoveSummary(
                serviceSummary,
                selection.Failures,
                initiatingSession,
                wasCancelled: false,
                snapshot);
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    private SimilarityActionSnapshot CreateSimilarityActionSnapshot(AnalysisSession initiatingSession)
    {
        SimilarityItemViewModel[] selectedItems = SelectedSimilarityItems.ToArray();
        if (selectedItems.Length == 0)
        {
            throw new InvalidOperationException("Select at least one result first.");
        }

        return new SimilarityActionSnapshot(
            initiatingSession,
            selectedItems,
            selectedItems.Select(static item => item.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase),
            SelectedSimilarityPreviewItem?.FullPath);
    }

    private async Task<SelectionTargets> BuildValidatedSimilaritySelectionAsync(
        SimilarityActionSnapshot snapshot,
        IAnalysisService analysisService,
        CancellationToken cancellationToken)
    {
        var targets = new List<FileActionTarget>(snapshot.SelectedItems.Count);
        var failures = new List<FileActionFailure>();
        foreach (SimilarityItemViewModel item in snapshot.SelectedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentSimilarityItem(snapshot.Session, item) ||
                !TryReadSimilaritySnapshot(item.FullPath, out SimilarityFileSnapshot before) ||
                !MatchesSimilaritySnapshot(item, before))
            {
                failures.Add(ChangedFailure(item.FullPath));
                continue;
            }

            bool matches = false;
            Exception? providerFailure = null;
            try
            {
                matches = await analysisService.RevalidateSimilarityItemAsync(
                    snapshot.Session.Tool,
                    item.Source,
                    cancellationToken);
            }
            catch (MissingRequiredMusicMetadataException ex)
                when (snapshot.Session.Tool == ToolKind.MusicDuplicates)
            {
                providerFailure = ex;
            }
            catch (Exception ex) when (IsExpectedSimilarityProviderFailure(ex))
            {
                providerFailure = ex;
            }

            cancellationToken.ThrowIfCancellationRequested();
            bool current = IsCurrentSimilarityItem(snapshot.Session, item);
            bool unchanged = TryReadSimilaritySnapshot(item.FullPath, out SimilarityFileSnapshot after) &&
                after == before &&
                MatchesSimilaritySnapshot(item, after);
            if (!current || !unchanged)
            {
                failures.Add(ChangedFailure(item.FullPath));
                continue;
            }

            if (providerFailure is not null)
            {
                string reason = providerFailure is MissingRequiredMusicMetadataException
                    ? "Required music metadata is missing."
                    : snapshot.Session.Tool switch
                    {
                        ToolKind.SimilarVideos => "Could not decode video.",
                        ToolKind.MusicDuplicates => "Could not read music metadata.",
                        _ => "Could not decode image.",
                    };
                failures.Add(new FileActionFailure(item.FullPath, reason));
                continue;
            }

            if (!matches)
            {
                failures.Add(ChangedFailure(item.FullPath));
                continue;
            }

            targets.Add(new FileActionTarget(
                item.FullPath,
                item.SizeBytes,
                FileActionTargetKind.File,
                ExpectedModifiedUtc: item.ModifiedUtc));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return RejectSimilarityTargetsAfterSessionChange(
            snapshot,
            new SelectionTargets(targets, failures, snapshot.SelectedItems.Count));
    }

    private SelectionTargets RejectSimilarityTargetsAfterSessionChange(
        SimilarityActionSnapshot snapshot,
        SelectionTargets selection)
    {
        if (!ReferenceEquals(_sessionStore.CurrentSession, snapshot.Session) ||
            snapshot.SelectedItems.Any(item => !IsCanonicalSimilarityItem(item)))
        {
            return new SelectionTargets(
                [],
                snapshot.SelectedItems.Select(static item => ChangedFailure(item.FullPath)).ToArray(),
                snapshot.SelectedItems.Count);
        }

        return selection;
    }

    public Task<FileOperationResult> RenameFindingAsync(
        PathFindingViewModel finding,
        string newName,
        CancellationToken cancellationToken) => RunCoordinatedActionAsync(
            token => RenameFindingCoreAsync(finding, newName, token),
            cancellationToken);

    private async Task<FileOperationResult> RenameFindingCoreAsync(
        PathFindingViewModel finding,
        string newName,
        CancellationToken cancellationToken)
    {
        AnalysisSession? initiatingSession = _sessionStore.CurrentSession;
        if (!SupportsRename(initiatingSession))
        {
            throw new InvalidOperationException("Actions are not available for these results yet.");
        }

        if (!_allFindings.Contains(finding))
        {
            return new FileOperationResult(
                finding.FullPath,
                null,
                ChangedFailure(finding.FullPath));
        }

        IFileActionService fileActions = _fileActionService ??
            throw new InvalidOperationException("File actions are not configured.");
        FileActionTarget? target;
        FileActionFailure? failure;
        if (_sessionStore.CurrentSession?.Tool == ToolKind.BadExtensions)
        {
            (target, failure) = await TryMapBadExtensionRenameAsync(
                finding,
                newName,
                cancellationToken);
            if (!ReferenceEquals(_sessionStore.CurrentSession, initiatingSession) ||
                initiatingSession is not { Tool: ToolKind.BadExtensions, ToolOptions: NoToolOptions } ||
                !_allFindings.Contains(finding))
            {
                return new FileOperationResult(
                    finding.FullPath,
                    null,
                    ChangedFailure(finding.FullPath));
            }
        }
        else if (_sessionStore.CurrentSession?.Tool == ToolKind.BadNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryMapBadNameRename(finding, newName, out target, out failure) ||
                !ReferenceEquals(_sessionStore.CurrentSession, initiatingSession) ||
                initiatingSession is not { Tool: ToolKind.BadNames, ToolOptions: NoToolOptions } ||
                !_allFindings.Contains(finding))
            {
                return new FileOperationResult(
                    finding.FullPath,
                    null,
                    ChangedFailure(finding.FullPath));
            }
        }
        else if (!TryMapFinding(finding, out target, out failure))
        {
            return new FileOperationResult(finding.FullPath, null, failure);
        }

        if (target is null)
        {
            return new FileOperationResult(finding.FullPath, null, failure);
        }

        FileOperationResult result = await fileActions.RenameAsync(target!, newName, cancellationToken);
        if (result.Succeeded &&
            ReferenceEquals(_sessionStore.CurrentSession, initiatingSession) &&
            _allFindings.Contains(finding))
        {
            RemoveSuccessfulPaths(new HashSet<string>([result.SourcePath], StringComparer.OrdinalIgnoreCase));
        }

        return result;
    }

    public bool TryValidateRenameCandidate(
        PathFindingViewModel finding,
        string newName,
        out string destinationPath,
        out string validationMessage)
    {
        destinationPath = string.Empty;
        validationMessage = string.Empty;
        AnalysisSession? session = _sessionStore.CurrentSession;
        if (!SupportsRenameDialog(session) || !_allFindings.Contains(finding))
        {
            validationMessage = "The finding is no longer current.";
            return false;
        }

        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(finding.FullPath));
        if (string.IsNullOrWhiteSpace(parent))
        {
            validationMessage = "The destination cannot be verified.";
            return false;
        }

        destinationPath = Path.Combine(parent, newName);

        if (newName.Contains(Path.DirectorySeparatorChar) ||
            newName.Contains(Path.AltDirectorySeparatorChar) ||
            BadNameAnalyzer.Detect(Path.Combine(parent, newName), newName) is not null)
        {
            validationMessage = "Enter a valid Windows file name.";
            return false;
        }

        string currentName = Path.GetFileName(finding.FullPath);
        if (string.Equals(newName, currentName, StringComparison.OrdinalIgnoreCase))
        {
            validationMessage = "Choose a different file name.";
            return false;
        }

        if (session?.Tool == ToolKind.BadExtensions &&
            !string.Equals(newName, finding.Suggestion, StringComparison.Ordinal))
        {
            validationMessage = "Use the recommended file name.";
            return false;
        }

        try
        {
            if (Directory.EnumerateFileSystemEntries(parent).Any(path =>
                    !string.Equals(path, finding.FullPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileName(path), newName, StringComparison.OrdinalIgnoreCase)))
            {
                validationMessage = "A file or folder with the same name already exists.";
                return false;
            }
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            validationMessage = "The destination cannot be verified.";
            return false;
        }

        return true;
    }

    public Task ExportAsync(
        ResultExportFormat format,
        string destinationPath,
        CancellationToken cancellationToken) => RunCoordinatedActionAsync(
            async token =>
            {
                await ExportCoreAsync(format, destinationPath, token);
                return true;
            },
            cancellationToken);

    private Task ExportCoreAsync(
        ResultExportFormat format,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        IResultExportService exporter = _resultExportService ??
            throw new InvalidOperationException("Result export is not configured.");
        AnalysisSession session = _sessionStore.CurrentSession ??
            throw new InvalidOperationException("Run an analysis before exporting results.");
        ResultExportItem[] findingItems = _allFindings.Select(static finding => new ResultExportItem(
            finding.FullPath,
            MapFindingKind(finding.Source),
            finding.Source.Reason,
            finding.Source.Suggestion,
            null,
            null,
            finding.Source.SizeBytes,
            finding.Source.CreatedUtc,
            finding.Source.ModifiedUtc,
            finding.Source.Metadata.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal)))
            .ToArray();
        bool isMusic = session.Tool == ToolKind.MusicDuplicates;
        ResultExportItem[] similarityItems = _allGroups.SelectMany(group => group.Items.Select(item => new ResultExportItem(
            item.FullPath,
            FileActionTargetKind.File,
            isMusic ? "Music metadata and duration match" : "Similarity match",
            "Review manually",
            group.Id,
            isMusic ? null : item.Source.SimilarityPercent,
            item.SizeBytes,
            null,
            item.ModifiedUtc,
            MergeMetadata(group.Source.Metadata, item.Source.Metadata))))
            .ToArray();
        SkippedPath[] skippedPaths = session.Result.SkippedPaths.Select(static skipped => new SkippedPath
        {
            Path = skipped.Path,
            Reason = skipped.Reason,
        }).ToArray();
        var snapshot = new ResultExportSnapshot(
            session.Tool,
            session.CompletedAtUtc,
            BuildScopeSummary(session.Scope),
            [.. findingItems, .. similarityItems],
            skippedPaths);
        return exporter.ExportAsync(snapshot, format, destinationPath, cancellationToken);
    }

    private async Task<T> RunCoordinatedActionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource actionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_operationCoordinator.TryAcquire(
                new AppOperationDescriptor(AppOperationKind.AnalysisResultsAction),
                actionCancellation.Cancel,
                out IAppOperationLease? lease))
        {
            throw new InvalidOperationException("Another operation is already running.");
        }

        _actionCancellation = actionCancellation;
        IsActionRunning = true;
        try
        {
            return await action(actionCancellation.Token);
        }
        finally
        {
            IsActionRunning = false;
            _actionCancellation = null;
            lease!.Dispose();
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilterAndSort();

    partial void OnSelectedSortIndexChanged(int value) => ApplyFilterAndSort();

    partial void OnSelectedResultChanged(object? value)
    {
        if (value is not null)
        {
            IsPreviewPaneOpen = true;
        }

        if (value is SimilarityGroupViewModel group)
        {
            _ = SelectSimilarityPreviewItemAsync(group.ReferenceItem);
        }
        else if (value is PathFindingViewModel)
        {
            ResetSimilarityPreview();
        }
    }

    private void ResultsChanged(object? sender, AnalysisSession? session)
    {
        _allFindings.Clear();
        _allGroups.Clear();
        SelectedResult = null;
        ResetSimilarityPreview();
        IsPreviewPaneOpen = false;

        if (session is not null)
        {
            _allFindings.AddRange(
                session.Result.Findings.Select(finding => new PathFindingViewModel(finding, SelectionChanged)));
            _allGroups.AddRange(
                session.Result.Groups.Select(group => new SimilarityGroupViewModel(group, SelectionChanged)));
        }

        ApplyFilterAndSort();
        NotifyResultStateChanged();
    }

    private void ApplyFilterAndSort()
    {
        string search = SearchText.Trim();
        IEnumerable<PathFindingViewModel> findings = _allFindings.Where(
            finding => Matches(finding, search));
        IEnumerable<SimilarityGroupViewModel> groups = _allGroups.Where(
            group => Matches(group, search));

        (findings, groups) = SelectedSortIndex switch
        {
            1 => (
                findings.OrderByDescending(static item => item.SizeBytes)
                    .ThenBy(static item => item.FullPath, StringComparer.OrdinalIgnoreCase),
                groups.OrderByDescending(static group => group.Items.Sum(static item => item.SizeBytes))
                    .ThenBy(static group => group.FullPath, StringComparer.OrdinalIgnoreCase)),
            2 => (
                findings.OrderByDescending(static item => item.ModifiedUtc)
                    .ThenBy(static item => item.FullPath, StringComparer.OrdinalIgnoreCase),
                groups.OrderByDescending(static group => group.ReferenceItem.ModifiedUtc)
                    .ThenBy(static group => group.FullPath, StringComparer.OrdinalIgnoreCase)),
            _ => (
                findings.OrderBy(static item => item.FullPath, StringComparer.OrdinalIgnoreCase),
                groups.OrderBy(static group => group.FullPath, StringComparer.OrdinalIgnoreCase)),
        };

        Replace(Findings, findings);
        Replace(Groups, groups);
        IEnumerable<object> resultItems = Findings.Cast<object>().Concat(Groups);
        resultItems = SelectedSortIndex switch
        {
            1 => resultItems
                .OrderByDescending(GetResultSize)
                .ThenBy(GetResultPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetResultPath, StringComparer.Ordinal),
            2 => resultItems
                .OrderByDescending(GetResultModifiedUtc)
                .ThenBy(GetResultPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetResultPath, StringComparer.Ordinal),
            _ => resultItems
                .OrderBy(GetResultPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetResultPath, StringComparer.Ordinal),
        };
        Replace(ResultItems, resultItems);

        OnPropertyChanged(nameof(ResultsVisibility));
    }

    private static bool Matches(PathFindingViewModel finding, string search) =>
        string.IsNullOrEmpty(search) ||
        finding.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        finding.Reason.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        finding.Suggestion.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        finding.MetadataText.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(SimilarityGroupViewModel group, string search) =>
        string.IsNullOrEmpty(search) ||
        group.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        group.Items.Any(item =>
            item.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.MetadataText.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
        group.MetadataText.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static string GetResultPath(object item) => item switch
    {
        PathFindingViewModel finding => finding.FullPath,
        SimilarityGroupViewModel group => group.FullPath,
        _ => string.Empty,
    };

    private static long GetResultSize(object item) => item switch
    {
        PathFindingViewModel finding => finding.SizeBytes,
        SimilarityGroupViewModel group => group.Items.Sum(static candidate => candidate.SizeBytes),
        _ => 0,
    };

    private static DateTime GetResultModifiedUtc(object item) => item switch
    {
        PathFindingViewModel finding => finding.ModifiedUtc ?? DateTime.MinValue,
        SimilarityGroupViewModel group => group.ReferenceItem.ModifiedUtc,
        _ => DateTime.MinValue,
    };

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (T item in items)
        {
            target.Add(item);
        }
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedItemCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedFindings));
        OnPropertyChanged(nameof(SelectedSimilarityItems));
        OnPropertyChanged(nameof(CanActOnSelection));
        OnPropertyChanged(nameof(CanRenameSelection));
        OnPropertyChanged(nameof(RenameSelection));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanStartNewAnalysis));
        OnPropertyChanged(nameof(CanMutateSelection));
    }

    private void OperationChanged(object? sender, EventArgs e)
    {
        SelectionChanged();
        NewAnalysisCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private void NotifyResultStateChanged()
    {
        OnPropertyChanged(nameof(BeforeFirstAnalysisVisibility));
        OnPropertyChanged(nameof(NoFindingsVisibility));
        OnPropertyChanged(nameof(ResultsVisibility));
        OnPropertyChanged(nameof(SkippedPathsVisibility));
        OnPropertyChanged(nameof(HasSkippedPaths));
        OnPropertyChanged(nameof(SkippedPathsSummaryText));
        OnPropertyChanged(nameof(SkippedPathsDetailsText));
        OnPropertyChanged(nameof(SummaryText));
        SelectionChanged();
    }

    private bool IsBulkMutationToolSupported => SupportsBulkMutation(_sessionStore.CurrentSession);

    private static bool SupportsBulkMutation(AnalysisSession? session) => session switch
    {
        { Tool: ToolKind.EmptyFiles or ToolKind.EmptyFolders or ToolKind.BigFiles } => true,
        { Tool: ToolKind.TemporaryFiles, ToolOptions: TemporaryFileToolOptions } => true,
        { Tool: ToolKind.InvalidLinks, ToolOptions: NoToolOptions } => true,
        { Tool: ToolKind.BrokenFiles, ToolOptions: NoToolOptions } => true,
        { Tool: ToolKind.SimilarImages, ToolOptions: SimilarImageToolOptions } => true,
        { Tool: ToolKind.SimilarVideos, ToolOptions: SimilarVideoToolOptions } => true,
        { Tool: ToolKind.MusicDuplicates, ToolOptions: MusicDuplicateToolOptions } => true,
        _ => false,
    };

    private static bool SupportsRename(AnalysisSession? session) =>
        (SupportsBulkMutation(session) && session?.Tool != ToolKind.BrokenFiles) ||
        session is { Tool: ToolKind.BadExtensions or ToolKind.BadNames, ToolOptions: NoToolOptions };

    private static bool SupportsRenameDialog(AnalysisSession? session) =>
        session is { Tool: ToolKind.BadExtensions or ToolKind.BadNames, ToolOptions: NoToolOptions };

    private void EnsureSelectedMutationIsSupported()
    {
        if (!IsBulkMutationToolSupported)
        {
            throw new InvalidOperationException("Actions are not available for these results yet.");
        }
    }

    private FileOperationSummary ApplyMoveSummary(
        FileOperationSummary serviceSummary,
        IReadOnlyList<FileActionFailure> localFailures,
        AnalysisSession? initiatingSession,
        bool wasCancelled,
        SimilarityActionSnapshot? similaritySnapshot = null)
    {
        FileOperationResult[] localResults = localFailures
            .Select(static failure => new FileOperationResult(failure.Path, null, failure))
            .ToArray();
        FileOperationResult[] results = [.. localResults, .. serviceSummary.Results];
        HashSet<string> successfulPaths = results
            .Where(static result => result.Succeeded)
            .Select(static result => result.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ReferenceEquals(_sessionStore.CurrentSession, initiatingSession))
        {
            if (similaritySnapshot is null)
            {
                RemoveSuccessfulPaths(successfulPaths);
            }
            else
            {
                RebuildSimilarityGroups(successfulPaths, similaritySnapshot);
            }
        }
        int succeeded = results.Count(static result => result.Succeeded);
        int failed = results.Length - succeeded;
        ActionStatusMessage = wasCancelled
            ? $"Move cancelled after {succeeded:N0} {(succeeded == 1 ? "item" : "items")} moved."
            : failed == 0
                ? succeeded == 1 ? "1 item moved." : $"{succeeded:N0} items moved."
                : $"{succeeded:N0} items moved, {failed:N0} could not be moved.";
        return new FileOperationSummary(results, serviceSummary.SucceededBytes);
    }

    private DeleteSummary ApplyDeleteSummary(
        DeleteSummary serviceSummary,
        SelectionTargets selection,
        AnalysisSession? initiatingSession,
        bool wasCancelled,
        SimilarityActionSnapshot? similaritySnapshot = null)
    {
        FileActionFailure[] failures = selection.Failures.Concat(serviceSummary.Failures).ToArray();
        IEnumerable<string> successfulPaths = serviceSummary.DeletedPaths ?? selection.Targets
            .Where(target => !serviceSummary.Failures.Any(failure =>
                string.Equals(failure.Path, target.FullPath, StringComparison.OrdinalIgnoreCase)))
            .Select(static target => target.FullPath);
        HashSet<string> successfulPathSet = successfulPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ReferenceEquals(_sessionStore.CurrentSession, initiatingSession))
        {
            if (similaritySnapshot is null)
            {
                RemoveSuccessfulPaths(successfulPathSet);
            }
            else
            {
                RebuildSimilarityGroups(successfulPathSet, similaritySnapshot);
            }
        }
        ActionStatusMessage = wasCancelled
            ? $"Delete cancelled after {serviceSummary.DeletedCount:N0} " +
                (serviceSummary.DeletedCount == 1 ? "item deleted." : "items deleted.")
            : failures.Length == 0
                ? serviceSummary.DeletedCount == 1
                    ? "1 item deleted."
                    : $"{serviceSummary.DeletedCount:N0} items deleted."
                : $"{serviceSummary.DeletedCount:N0} items deleted, {failures.Length:N0} could not be deleted.";
        return new DeleteSummary(
            serviceSummary.DeletedCount,
            serviceSummary.DeletedBytes,
            failures,
            successfulPathSet.ToArray());
    }

    private async Task<SelectionTargets> BuildValidatedSelectionAsync(
        AnalysisSession? initiatingSession,
        CancellationToken cancellationToken)
    {
        if (initiatingSession is not { Tool: ToolKind.BrokenFiles, ToolOptions: NoToolOptions })
        {
            return BuildValidatedSelection();
        }

        IReadOnlyList<PathFindingViewModel> selectedFindings = SelectedFindings;
        var targets = new List<FileActionTarget>(selectedFindings.Count);
        var failures = new List<FileActionFailure>();
        foreach (PathFindingViewModel finding in selectedFindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (FileActionTarget? target, FileActionFailure? failure) = await TryMapBrokenFileAsync(
                finding,
                initiatingSession,
                cancellationToken);
            if (target is not null)
            {
                targets.Add(target);
            }
            else
            {
                failures.Add(failure!);
            }
        }

        return new SelectionTargets(targets, failures, selectedFindings.Count);
    }

    private SelectionTargets RejectBrokenTargetsAfterSessionChange(
        AnalysisSession? initiatingSession,
        SelectionTargets selection)
    {
        if (initiatingSession is not { Tool: ToolKind.BrokenFiles, ToolOptions: NoToolOptions } ||
            ReferenceEquals(_sessionStore.CurrentSession, initiatingSession))
        {
            return selection;
        }

        FileActionFailure[] failures =
        [
            .. selection.Failures,
            .. selection.Targets.Select(static target => ChangedFailure(target.FullPath)),
        ];
        return new SelectionTargets([], failures, selection.SelectedCount);
    }

    private SelectionTargets BuildValidatedSelection()
    {
        var targets = new List<FileActionTarget>();
        var failures = new List<FileActionFailure>();
        int selectedCount = SelectedFindings.Count + SelectedSimilarityItems.Count;
        IEnumerable<PathFindingViewModel> selectedFindings = SelectedFindings;
        if (_sessionStore.CurrentSession?.Tool == ToolKind.EmptyFolders)
        {
            selectedFindings = selectedFindings
                .OrderByDescending(GetDirectoryDepth)
                .ThenBy(static finding => finding.FullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static finding => finding.FullPath, StringComparer.Ordinal);
        }

        foreach (PathFindingViewModel finding in selectedFindings)
        {
            if (TryMapFinding(finding, out FileActionTarget? target, out FileActionFailure? failure))
            {
                targets.Add(target!);
            }
            else
            {
                failures.Add(failure!);
            }
        }

        foreach (SimilarityItemViewModel item in SelectedSimilarityItems)
        {
            if (TryMapSimilarityItem(item, out FileActionTarget? target, out FileActionFailure? failure))
            {
                targets.Add(target!);
            }
            else
            {
                failures.Add(failure!);
            }
        }

        return new SelectionTargets(targets, failures, selectedCount);
    }

    private async ValueTask<(FileActionTarget? Target, FileActionFailure? Failure)> TryMapBrokenFileAsync(
        PathFindingViewModel finding,
        AnalysisSession initiatingSession,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrentBrokenFinding(initiatingSession, finding) ||
                _fileFormatProbe is null ||
                finding.Source.Kind != PathFindingKind.File ||
                finding.Source.SizeBytes is not long expectedSize ||
                finding.Source.ModifiedUtc is not DateTime expectedModifiedUtc ||
                !TryGetExactMetadata(finding.Source.Metadata, "Validator", out string? expectedValidator) ||
                !TryGetExactMetadata(finding.Source.Metadata, "ErrorType", out string? expectedErrorType) ||
                !TryGetExpectedProbeStatus(finding.Source.Reason, out FileProbeStatus expectedStatus))
            {
                return (null, ChangedFailure(finding.FullPath));
            }

            FileAttributes beforeAttributes = File.GetAttributes(finding.FullPath);
            var before = new FileInfo(finding.FullPath);
            before.Refresh();
            if (beforeAttributes.HasFlag(FileAttributes.Directory) ||
                beforeAttributes.HasFlag(FileAttributes.ReparsePoint) ||
                before.Length != expectedSize ||
                before.LastWriteTimeUtc != expectedModifiedUtc)
            {
                return (null, ChangedFailure(finding.FullPath));
            }

            DetectedFileType? detectedType = null;
            FileProbeResult? probeResult = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                detectedType = await _detectFileAsync(
                    finding.FullPath,
                    cancellationToken);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                probeResult = new FileProbeResult(
                    FileProbeStatus.Invalid,
                    "HeaderReadFailure",
                    null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentBrokenFinding(initiatingSession, finding))
            {
                return (null, ChangedFailure(finding.FullPath));
            }

            if (probeResult is null)
            {
                probeResult = await _fileFormatProbe.ProbeAsync(
                    finding.FullPath,
                    detectedType,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentBrokenFinding(initiatingSession, finding))
            {
                return (null, ChangedFailure(finding.FullPath));
            }

            FileAttributes afterAttributes = File.GetAttributes(finding.FullPath);
            var after = new FileInfo(finding.FullPath);
            after.Refresh();
            bool hasExpectedDetectedType = TryGetExactMetadata(
                finding.Source.Metadata,
                "DetectedType",
                out string? expectedDetectedType);
            bool detectedTypeMatches = detectedType is null
                ? !hasExpectedDetectedType
                : hasExpectedDetectedType && string.Equals(
                    detectedType.Name,
                    expectedDetectedType,
                    StringComparison.Ordinal);
            if (afterAttributes.HasFlag(FileAttributes.Directory) ||
                afterAttributes.HasFlag(FileAttributes.ReparsePoint) ||
                after.Length != expectedSize ||
                after.LastWriteTimeUtc != expectedModifiedUtc ||
                probeResult.Status != expectedStatus ||
                !string.Equals(probeResult.ErrorType, expectedErrorType, StringComparison.Ordinal) ||
                !string.Equals(GetBrokenValidator(detectedType), expectedValidator, StringComparison.Ordinal) ||
                !detectedTypeMatches)
            {
                return (null, ChangedFailure(finding.FullPath));
            }

            return (
                new FileActionTarget(
                    finding.FullPath,
                    expectedSize,
                    FileActionTargetKind.File,
                    ExpectedModifiedUtc: expectedModifiedUtc),
                null);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return (null, ChangedFailure(finding.FullPath));
        }
    }

    private bool IsCurrentBrokenFinding(
        AnalysisSession initiatingSession,
        PathFindingViewModel finding) =>
        initiatingSession is { Tool: ToolKind.BrokenFiles, ToolOptions: NoToolOptions } &&
        ReferenceEquals(_sessionStore.CurrentSession, initiatingSession) &&
        _allFindings.Contains(finding);

    private static bool TryGetExpectedProbeStatus(string reason, out FileProbeStatus status)
    {
        if (string.Equals(reason, "Unreadable or malformed file.", StringComparison.Ordinal))
        {
            status = FileProbeStatus.Invalid;
            return true;
        }

        if (string.Equals(reason, "Unsupported or protected.", StringComparison.Ordinal))
        {
            status = FileProbeStatus.UnsupportedOrProtected;
            return true;
        }

        status = default;
        return false;
    }

    private static string GetBrokenValidator(DetectedFileType? detectedType) => detectedType?.Name switch
    {
        "JPEG" or "PNG" or "GIF" or "BMP" or "TIFF" or "WebP" => "Image",
        "MP3" or "FLAC" or "WAV" or "Ogg" or "MP4" or "QuickTime" or "ISO BMFF" or
            "WebM" or "Matroska" or "EBML" or "AVI" => "Media",
        "ZIP" => "Zip",
        _ => "Header",
    };

    private bool TryMapFinding(
        PathFindingViewModel finding,
        out FileActionTarget? target,
        out FileActionFailure? failure)
    {
        AnalysisSession? session = _sessionStore.CurrentSession;
        if (session?.Tool == ToolKind.EmptyFolders)
        {
            return TryMapEmptyFolder(finding, out target, out failure);
        }

        if (session?.Tool == ToolKind.TemporaryFiles)
        {
            return TryMapTemporaryFile(finding, session.ToolOptions, out target, out failure);
        }

        if (session?.Tool == ToolKind.InvalidLinks)
        {
            return TryMapInvalidLink(finding, out target, out failure);
        }

        target = null;
        failure = null;
        try
        {
            FileActionTargetKind kind = ReadTargetKind(finding.FullPath);
            long sizeBytes = kind == FileActionTargetKind.File ? new FileInfo(finding.FullPath).Length : 0;
            if (!FindingKindMatches(finding.Source.Kind, kind) ||
                !FindingPredicateStillMatches(finding, kind, sizeBytes))
            {
                failure = ChangedFailure(finding.FullPath);
                return false;
            }

            target = new FileActionTarget(finding.FullPath, sizeBytes, kind);
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            failure = ChangedFailure(finding.FullPath);
            return false;
        }
    }

    private static bool TryMapEmptyFolder(
        PathFindingViewModel finding,
        out FileActionTarget? target,
        out FileActionFailure? failure)
    {
        target = null;
        failure = null;
        try
        {
            FileAttributes attributes = File.GetAttributes(finding.FullPath);
            if (finding.Source.Kind != PathFindingKind.Directory ||
                GetDirectoryDepth(finding) < 0 ||
                !attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint) ||
                Directory.EnumerateFileSystemEntries(finding.FullPath).Take(1).Any())
            {
                failure = EmptyFolderChangedFailure(finding.FullPath);
                return false;
            }

            target = new FileActionTarget(finding.FullPath, 0, FileActionTargetKind.Directory);
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            failure = EmptyFolderChangedFailure(finding.FullPath);
            return false;
        }
    }

    private static bool TryMapTemporaryFile(
        PathFindingViewModel finding,
        ToolOptions toolOptions,
        out FileActionTarget? target,
        out FileActionFailure? failure)
    {
        target = null;
        failure = null;
        try
        {
            if (finding.Source.Kind != PathFindingKind.File ||
                toolOptions is not TemporaryFileToolOptions options ||
                !MatchesTemporaryFileName(Path.GetFileName(finding.FullPath)))
            {
                failure = TemporaryFileChangedFailure(finding.FullPath);
                return false;
            }

            FileAttributes attributes = File.GetAttributes(finding.FullPath);
            var file = new FileInfo(finding.FullPath);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint) ||
                file.LastWriteTimeUtc > options.UtcNow - options.MinimumAge)
            {
                failure = TemporaryFileChangedFailure(finding.FullPath);
                return false;
            }

            long sizeBytes = file.Length;
            using (new FileStream(finding.FullPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
            }

            target = new FileActionTarget(finding.FullPath, sizeBytes, FileActionTargetKind.File);
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            failure = TemporaryFileChangedFailure(finding.FullPath);
            return false;
        }
    }

    private static bool TryMapInvalidLink(
        PathFindingViewModel finding,
        out FileActionTarget? target,
        out FileActionFailure? failure)
    {
        target = null;
        failure = null;
        try
        {
            if (finding.Source.Kind != PathFindingKind.Link ||
                !TryGetExactMetadata(finding.Source.Metadata, "LinkKind", out string? linkKind))
            {
                failure = ChangedFailure(finding.FullPath);
                return false;
            }

            FileActionTargetKind? expectedKind = linkKind switch
            {
                "File" => FileActionTargetKind.FileLink,
                "Directory" => FileActionTargetKind.DirectoryLink,
                _ => null,
            };
            if (expectedKind is null || ReadTargetKind(finding.FullPath) != expectedKind.Value)
            {
                failure = ChangedFailure(finding.FullPath);
                return false;
            }

            FileSystemInfo source = expectedKind.Value == FileActionTargetKind.DirectoryLink
                ? new DirectoryInfo(finding.FullPath)
                : new FileInfo(finding.FullPath);
            if (source.LinkTarget is null ||
                !string.Equals(
                    InvalidLinkAnalyzer.GetInvalidReason(source),
                    finding.Source.Reason,
                    StringComparison.Ordinal))
            {
                failure = ChangedFailure(finding.FullPath);
                return false;
            }

            target = new FileActionTarget(
                finding.FullPath,
                0,
                expectedKind.Value,
                finding.Source.Reason);
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            failure = ChangedFailure(finding.FullPath);
            return false;
        }
    }

    private bool TryMapBadNameRename(
        PathFindingViewModel finding,
        string newName,
        out FileActionTarget? target,
        out FileActionFailure? failure)
    {
        target = null;
        failure = null;
        try
        {
            if (finding.Source.Kind != PathFindingKind.File ||
                finding.Source.SizeBytes is not long expectedSize ||
                finding.Source.ModifiedUtc is not DateTime expectedModifiedUtc ||
                !TryGetExactMetadata(finding.Source.Metadata, "CurrentName", out string? expectedCurrentName))
            {
                failure = ChangedFailure(finding.FullPath);
                return false;
            }

            FileAttributes attributes = File.GetAttributes(finding.FullPath);
            var file = new FileInfo(finding.FullPath);
            string currentName = Path.GetFileName(finding.FullPath);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint) ||
                file.Length != expectedSize ||
                file.LastWriteTimeUtc != expectedModifiedUtc ||
                !string.Equals(currentName, expectedCurrentName, StringComparison.Ordinal) ||
                BadNameAnalyzer.Detect(finding.FullPath, currentName) is null ||
                !TryValidateRenameCandidate(finding, newName, out _, out _))
            {
                failure = ChangedFailure(finding.FullPath);
                return false;
            }

            target = new FileActionTarget(
                finding.FullPath,
                file.Length,
                FileActionTargetKind.File);
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            failure = ChangedFailure(finding.FullPath);
            return false;
        }
    }

    private async ValueTask<(FileActionTarget? Target, FileActionFailure? Failure)>
        TryMapBadExtensionRenameAsync(
            PathFindingViewModel finding,
            string newName,
            CancellationToken cancellationToken)
    {
        try
        {
            if (finding.Source.Kind != PathFindingKind.File ||
                finding.Source.SizeBytes is not long expectedSize ||
                finding.Source.ModifiedUtc is not DateTime expectedModifiedUtc ||
                !TryGetExactMetadata(finding.Source.Metadata, "CurrentExtension", out string? expectedExtension) ||
                !TryGetExactMetadata(finding.Source.Metadata, "ProperExtension", out string? expectedRecommendation) ||
                !TryGetExactMetadata(finding.Source.Metadata, "DetectedType", out string? expectedType))
            {
                return (null, ChangedFailure(finding.FullPath));
            }

            FileAttributes attributes = File.GetAttributes(finding.FullPath);
            var file = new FileInfo(finding.FullPath);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint) ||
                file.Length != expectedSize ||
                file.LastWriteTimeUtc != expectedModifiedUtc)
            {
                return (null, ChangedFailure(finding.FullPath));
            }

            DetectedFileType? detected = await _detectFileAsync(
                finding.FullPath,
                cancellationToken);
            attributes = File.GetAttributes(finding.FullPath);
            file.Refresh();
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint) ||
                file.Length != expectedSize ||
                file.LastWriteTimeUtc != expectedModifiedUtc ||
                detected?.RecommendedExtension is not string recommendation ||
                !string.Equals(detected.Name, expectedType, StringComparison.Ordinal) ||
                !string.Equals(recommendation, expectedRecommendation, StringComparison.Ordinal))
            {
                return (null, ChangedFailure(finding.FullPath));
            }

            string currentExtension = Path.GetExtension(finding.FullPath);
            string currentExtensionMetadata = string.IsNullOrEmpty(currentExtension)
                ? "(none)"
                : currentExtension;
            string recommendedName = Path.GetFileNameWithoutExtension(finding.FullPath) + recommendation;
            if (!string.Equals(currentExtensionMetadata, expectedExtension, StringComparison.OrdinalIgnoreCase) ||
                detected.AllowedExtensions.Any(extension =>
                    string.Equals(extension, currentExtension, StringComparison.OrdinalIgnoreCase)) ||
                !string.Equals(newName, recommendedName, StringComparison.Ordinal))
            {
                return (null, ChangedFailure(finding.FullPath));
            }

            return (
                new FileActionTarget(
                    finding.FullPath,
                    file.Length,
                    FileActionTargetKind.File,
                    ExpectedBadExtensionContent: new BadExtensionContentConstraint(
                        expectedModifiedUtc,
                        detected.Name,
                        recommendation)),
                null);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return (null, ChangedFailure(finding.FullPath));
        }
    }

    private static bool TryGetExactMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        out string? value)
    {
        foreach ((string metadataKey, string metadataValue) in metadata)
        {
            if (string.Equals(metadataKey, key, StringComparison.Ordinal))
            {
                value = metadataValue;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryMapSimilarityItem(
        SimilarityItemViewModel item,
        out FileActionTarget? target,
        out FileActionFailure? failure)
    {
        target = null;
        failure = null;
        try
        {
            if (ReadTargetKind(item.FullPath) != FileActionTargetKind.File ||
                new FileInfo(item.FullPath).Length != item.SizeBytes)
            {
                failure = ChangedFailure(item.FullPath);
                return false;
            }

            target = new FileActionTarget(item.FullPath, item.SizeBytes, FileActionTargetKind.File);
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            failure = ChangedFailure(item.FullPath);
            return false;
        }
    }

    private bool FindingPredicateStillMatches(
        PathFindingViewModel finding,
        FileActionTargetKind kind,
        long currentSizeBytes)
    {
        AnalysisSession? session = _sessionStore.CurrentSession;
        return session?.Tool switch
        {
            ToolKind.EmptyFiles => kind == FileActionTargetKind.File && currentSizeBytes == 0,
            ToolKind.BigFiles => kind == FileActionTargetKind.File &&
                session.ToolOptions is LargeFileToolOptions options &&
                currentSizeBytes >= options.MinimumSizeBytes,
            _ => false,
        };
    }

    private void RebuildSimilarityGroups(
        IReadOnlySet<string> successfulPaths,
        SimilarityActionSnapshot snapshot)
    {
        if (!ReferenceEquals(_sessionStore.CurrentSession, snapshot.Session))
        {
            return;
        }

        IAnalysisService analysisService = _analysisService ??
            throw new InvalidOperationException("Similarity regrouping is not configured.");
        SimilarityItem[] survivors = _allGroups
            .SelectMany(static group => group.Items)
            .Where(item => !successfulPaths.Contains(item.FullPath))
            .Select(static item => item.Source)
            .ToArray();
        IReadOnlyList<SimilarityGroup> regrouped = analysisService.RegroupSimilarityItems(
            snapshot.Session.Tool,
            snapshot.Session.ToolOptions,
            survivors);

        _allGroups.Clear();
        _allGroups.AddRange(regrouped.Select(group => new SimilarityGroupViewModel(group, SelectionChanged)));
        foreach (SimilarityItemViewModel item in _allGroups.SelectMany(static group => group.Items))
        {
            item.IsSelected = snapshot.SelectionPaths.Contains(item.FullPath);
        }

        SimilarityGroupViewModel? previewGroup = null;
        SimilarityItemViewModel? previewItem = null;
        if (snapshot.PreviewPath is not null)
        {
            foreach (SimilarityGroupViewModel group in _allGroups)
            {
                SimilarityItemViewModel? matching = group.Items.FirstOrDefault(item =>
                    string.Equals(item.FullPath, snapshot.PreviewPath, StringComparison.OrdinalIgnoreCase));
                if (matching is not null)
                {
                    previewGroup = group;
                    previewItem = matching;
                    break;
                }
            }
        }

        if (previewItem is null)
        {
            SelectedResult = null;
            ResetSimilarityPreview();
        }
        else
        {
            SelectedResult = previewGroup;
            _ = SelectSimilarityPreviewItemAsync(previewItem);
        }

        ApplyFilterAndSort();
        NotifyResultStateChanged();
    }

    private void RemoveSuccessfulPaths(IReadOnlySet<string> successfulPaths)
    {
        if (successfulPaths.Count == 0)
        {
            return;
        }

        _allFindings.RemoveAll(finding => successfulPaths.Contains(finding.FullPath));
        for (int groupIndex = _allGroups.Count - 1; groupIndex >= 0; groupIndex--)
        {
            SimilarityGroupViewModel group = _allGroups[groupIndex];
            for (int itemIndex = group.Items.Count - 1; itemIndex >= 0; itemIndex--)
            {
                if (successfulPaths.Contains(group.Items[itemIndex].FullPath))
                {
                    group.Items.RemoveAt(itemIndex);
                }
            }

            if (group.Items.Count < 2)
            {
                _allGroups.RemoveAt(groupIndex);
            }
        }

        if (SelectedResult is PathFindingViewModel finding && successfulPaths.Contains(finding.FullPath) ||
            SelectedResult is SimilarityGroupViewModel groupResult && !_allGroups.Contains(groupResult))
        {
            SelectedResult = null;
        }

        ApplyFilterAndSort();
        NotifyResultStateChanged();
    }

    private static FileActionTargetKind ReadTargetKind(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        return (attributes.HasFlag(FileAttributes.Directory), attributes.HasFlag(FileAttributes.ReparsePoint)) switch
        {
            (false, false) => FileActionTargetKind.File,
            (true, false) => FileActionTargetKind.Directory,
            (false, true) => FileActionTargetKind.FileLink,
            (true, true) => FileActionTargetKind.DirectoryLink,
        };
    }

    private static FileActionTargetKind MapFindingKind(PathFinding finding) => finding.Kind switch
    {
        PathFindingKind.File => FileActionTargetKind.File,
        PathFindingKind.Directory => FileActionTargetKind.Directory,
        PathFindingKind.Link => finding.Metadata.TryGetValue("LinkKind", out string? kind) &&
            string.Equals(kind, "Directory", StringComparison.OrdinalIgnoreCase)
                ? FileActionTargetKind.DirectoryLink
                : FileActionTargetKind.FileLink,
        _ => FileActionTargetKind.File,
    };

    private static bool FindingKindMatches(PathFindingKind findingKind, FileActionTargetKind targetKind) => findingKind switch
    {
        PathFindingKind.File => targetKind == FileActionTargetKind.File,
        PathFindingKind.Directory => targetKind == FileActionTargetKind.Directory,
        PathFindingKind.Link => targetKind is FileActionTargetKind.FileLink or FileActionTargetKind.DirectoryLink,
        _ => false,
    };

    private static FileActionFailure ChangedFailure(string path) => new(path, "File changed since scan.");

    private static FileActionFailure EmptyFolderChangedFailure(string path) =>
        new(path, "Folder is no longer empty.");

    private static FileActionFailure TemporaryFileChangedFailure(string path) =>
        new(path, "File is active or changed.");

    private static int GetDirectoryDepth(PathFindingViewModel finding) =>
        finding.Source.Metadata.TryGetValue("Depth", out string? depthText) &&
        int.TryParse(depthText, NumberStyles.None, CultureInfo.InvariantCulture, out int depth)
            ? depth
            : -1;

    private static bool MatchesTemporaryFileName(string fileName) =>
        fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith('~') ||
        fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".temp", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".download", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".chk", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileSystemFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;

    private static bool IsExpectedSimilarityProviderFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or
            NotSupportedException or InvalidDataException or OverflowException or COMException;

    private static bool IsSimilarityActionSession(AnalysisSession? session) => session switch
    {
        { Tool: ToolKind.SimilarImages, ToolOptions: SimilarImageToolOptions } => true,
        { Tool: ToolKind.SimilarVideos, ToolOptions: SimilarVideoToolOptions } => true,
        { Tool: ToolKind.MusicDuplicates, ToolOptions: MusicDuplicateToolOptions } => true,
        _ => false,
    };

    private bool IsCurrentSimilarityItem(
        AnalysisSession session,
        SimilarityItemViewModel item) =>
        IsSimilarityActionSession(session) &&
        ReferenceEquals(_sessionStore.CurrentSession, session) &&
        IsCanonicalSimilarityItem(item);

    private bool IsCanonicalSimilarityItem(SimilarityItemViewModel item) =>
        _allGroups.Any(group => group.Items.Contains(item));

    private static bool TryReadSimilaritySnapshot(
        string path,
        out SimilarityFileSnapshot snapshot)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists ||
                attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                snapshot = default;
                return false;
            }

            snapshot = new SimilarityFileSnapshot(file.Length, file.LastWriteTimeUtc);
            return true;
        }
        catch (Exception ex) when (IsExpectedSimilarityProviderFailure(ex))
        {
            snapshot = default;
            return false;
        }
    }

    private static bool MatchesSimilaritySnapshot(
        SimilarityItemViewModel item,
        SimilarityFileSnapshot snapshot) =>
        snapshot.SizeBytes == item.SizeBytes && snapshot.ModifiedUtc.Ticks == item.ModifiedUtc.Ticks;

    private void ResetSimilarityPreview()
    {
        _previewRequestGeneration++;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        SelectedSimilarityPreviewItem = null;
        SimilarityPreview = null;
        SimilarityPreviewStatusText = string.Empty;
    }

    private static IReadOnlyDictionary<string, string> MergeMetadata(
        IReadOnlyDictionary<string, string> groupMetadata,
        IReadOnlyDictionary<string, string> itemMetadata)
    {
        var metadata = new Dictionary<string, string>(groupMetadata, StringComparer.Ordinal);
        foreach ((string key, string value) in itemMetadata)
        {
            metadata[key] = value;
        }

        return metadata;
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

    private sealed record SelectionTargets(
        IReadOnlyList<FileActionTarget> Targets,
        IReadOnlyList<FileActionFailure> Failures,
        int SelectedCount);

    private sealed record SimilarityActionSnapshot(
        AnalysisSession Session,
        IReadOnlyList<SimilarityItemViewModel> SelectedItems,
        IReadOnlySet<string> SelectionPaths,
        string? PreviewPath);

    private readonly record struct SimilarityFileSnapshot(long SizeBytes, DateTime ModifiedUtc);
}
