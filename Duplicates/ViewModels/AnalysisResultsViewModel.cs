using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Engine.Analysis;
using Duplicates.Models;
using Duplicates.Services;
using Microsoft.UI.Xaml;

namespace Duplicates.ViewModels;

public sealed partial class AnalysisResultsViewModel : ObservableObject
{
    private readonly AnalysisSessionStore _sessionStore;
    private readonly List<PathFindingViewModel> _allFindings = [];
    private readonly List<SimilarityGroupViewModel> _allGroups = [];

    public AnalysisResultsViewModel(AnalysisSessionStore sessionStore)
    {
        _sessionStore = sessionStore;
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
        ResultItems.Clear();
        foreach (PathFindingViewModel finding in Findings)
        {
            ResultItems.Add(finding);
        }

        foreach (SimilarityGroupViewModel group in Groups)
        {
            ResultItems.Add(group);
        }

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
}
