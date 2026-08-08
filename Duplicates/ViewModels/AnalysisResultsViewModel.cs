using System.Collections.ObjectModel;
using System.Globalization;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
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
    private readonly List<PathFindingViewModel> _allFindings = [];
    private readonly List<SimilarityGroupViewModel> _allGroups = [];

    public AnalysisResultsViewModel(AnalysisSessionStore sessionStore)
        : this(sessionStore, null, null)
    {
    }

    public AnalysisResultsViewModel(
        AnalysisSessionStore sessionStore,
        IFileActionService? fileActionService,
        IResultExportService? resultExportService)
    {
        _sessionStore = sessionStore;
        _fileActionService = fileActionService;
        _resultExportService = resultExportService;
        _sessionStore.ResultChanged += ResultsChanged;
    }

    public event EventHandler<ToolKind>? NewAnalysisRequested;

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
    public partial bool IsPreviewPaneOpen { get; set; }

    [ObservableProperty]
    public partial string ActionStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActOnSelection))]
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
        IsMutationToolSupported &&
        SelectedFindings.Count > 0 &&
        SelectedSimilarityItems.Count == 0;

    public IReadOnlyList<PathFindingViewModel> SelectedFindings =>
        _allFindings.Where(static item => item.IsSelected).ToArray();

    public IReadOnlyList<SimilarityItemViewModel> SelectedSimilarityItems =>
        _allGroups.SelectMany(static group => group.Items).Where(static item => item.IsSelected).ToArray();

    public string PreviewPath => SelectedResult switch
    {
        PathFindingViewModel finding => finding.FullPath,
        SimilarityGroupViewModel group => group.FullPath,
        _ => "Select a result to preview details.",
    };

    public string PreviewTitle => SelectedResult switch
    {
        PathFindingViewModel finding => finding.DisplayName,
        SimilarityGroupViewModel group => group.DisplayName,
        _ => "No result selected",
    };

    public string PreviewSummary => SelectedResult switch
    {
        PathFindingViewModel finding => finding.SummaryText,
        SimilarityGroupViewModel group => group.SummaryText,
        _ => string.Empty,
    };

    public string PreviewMetadata => SelectedResult switch
    {
        PathFindingViewModel finding => finding.MetadataText,
        SimilarityGroupViewModel group => group.MetadataText,
        _ => string.Empty,
    };

    [RelayCommand]
    private void NewAnalysis()
    {
        ToolKind tool = _sessionStore.CurrentSession?.Tool ?? ToolKind.EmptyFolders;
        SearchText = string.Empty;
        SelectedSortIndex = 0;
        SelectedResult = null;
        IsPreviewPaneOpen = false;
        _sessionStore.Clear();
        NewAnalysisRequested?.Invoke(this, tool);
    }

    [RelayCommand]
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

    public async Task<DeleteSummary> DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        EnsureSelectedMutationIsSupported();
        IFileActionService fileActions = _fileActionService ??
            throw new InvalidOperationException("File actions are not configured.");
        SelectionTargets selection = BuildValidatedSelection();
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
                ApplyDeleteSummary(ex.Summary, selection, wasCancelled: true);
                throw;
            }

            return ApplyDeleteSummary(serviceSummary, selection, wasCancelled: false);
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    public async Task<FileOperationSummary> MoveSelectedAsync(
        string destinationFolder,
        MoveCollisionBehavior collisionBehavior,
        CancellationToken cancellationToken)
    {
        EnsureSelectedMutationIsSupported();
        IFileActionService fileActions = _fileActionService ??
            throw new InvalidOperationException("File actions are not configured.");
        SelectionTargets selection = BuildValidatedSelection();
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
                ApplyMoveSummary(ex.Summary, selection.Failures, wasCancelled: true);
                throw;
            }

            return ApplyMoveSummary(serviceSummary, selection.Failures, wasCancelled: false);
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    public async Task<FileOperationResult> RenameFindingAsync(
        PathFindingViewModel finding,
        string newName,
        CancellationToken cancellationToken)
    {
        if (!IsMutationToolSupported || !_allFindings.Contains(finding))
        {
            throw new InvalidOperationException("Actions are not available for these results yet.");
        }

        IFileActionService fileActions = _fileActionService ??
            throw new InvalidOperationException("File actions are not configured.");
        if (!TryMapFinding(finding, out FileActionTarget? target, out FileActionFailure? failure))
        {
            return new FileOperationResult(finding.FullPath, null, failure);
        }

        FileOperationResult result = await fileActions.RenameAsync(target!, newName, cancellationToken);
        if (result.Succeeded)
        {
            RemoveSuccessfulPaths(new HashSet<string>([result.SourcePath], StringComparer.OrdinalIgnoreCase));
        }

        return result;
    }

    public Task ExportAsync(
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
        ResultExportItem[] similarityItems = _allGroups.SelectMany(group => group.Items.Select(item => new ResultExportItem(
            item.FullPath,
            FileActionTargetKind.File,
            "Similarity match",
            "Review manually",
            group.Id,
            item.Source.SimilarityPercent,
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

    partial void OnSearchTextChanged(string value) => ApplyFilterAndSort();

    partial void OnSelectedSortIndexChanged(int value) => ApplyFilterAndSort();

    partial void OnSelectedResultChanged(object? value)
    {
        if (value is not null)
        {
            IsPreviewPaneOpen = true;
        }
    }

    private void ResultsChanged(object? sender, AnalysisSession? session)
    {
        _allFindings.Clear();
        _allGroups.Clear();
        SelectedResult = null;
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
        group.Items.Any(item => item.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
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

    private bool IsMutationToolSupported => _sessionStore.CurrentSession switch
    {
        { Tool: ToolKind.EmptyFiles or ToolKind.EmptyFolders or ToolKind.BigFiles } => true,
        { Tool: ToolKind.TemporaryFiles, ToolOptions: TemporaryFileToolOptions } => true,
        { Tool: ToolKind.InvalidLinks, ToolOptions: NoToolOptions } => true,
        _ => false,
    };

    private void EnsureSelectedMutationIsSupported()
    {
        if (!IsMutationToolSupported || SelectedSimilarityItems.Count > 0)
        {
            throw new InvalidOperationException("Actions are not available for these results yet.");
        }
    }

    private FileOperationSummary ApplyMoveSummary(
        FileOperationSummary serviceSummary,
        IReadOnlyList<FileActionFailure> localFailures,
        bool wasCancelled)
    {
        FileOperationResult[] localResults = localFailures
            .Select(static failure => new FileOperationResult(failure.Path, null, failure))
            .ToArray();
        FileOperationResult[] results = [.. localResults, .. serviceSummary.Results];
        HashSet<string> successfulPaths = results
            .Where(static result => result.Succeeded)
            .Select(static result => result.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveSuccessfulPaths(successfulPaths);
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
        bool wasCancelled)
    {
        FileActionFailure[] failures = selection.Failures.Concat(serviceSummary.Failures).ToArray();
        IEnumerable<string> successfulPaths = serviceSummary.DeletedPaths ?? selection.Targets
            .Where(target => !serviceSummary.Failures.Any(failure =>
                string.Equals(failure.Path, target.FullPath, StringComparison.OrdinalIgnoreCase)))
            .Select(static target => target.FullPath);
        HashSet<string> successfulPathSet = successfulPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveSuccessfulPaths(successfulPathSet);
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
}
