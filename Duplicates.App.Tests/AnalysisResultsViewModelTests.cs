using Duplicates.Engine.Analysis;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;

namespace Duplicates.App.Tests;

public sealed class AnalysisResultsViewModelTests
{
    [Fact]
    public void NewSessionMapsFindingsGroupsAndSkippedPathsWithoutSelectingActions()
    {
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store);

        store.SetCompleted(
            ToolKind.SimilarImages,
            new AnalysisScope { IncludedFolders = [@"C:\photos"] },
            NewResult(
                [NewFinding(@"C:\photos\large.jpg", 400)],
                [NewGroup("photos", @"C:\photos\one.jpg", @"C:\photos\two.jpg")],
                [new SkippedPath { Path = @"C:\photos\locked.jpg", Reason = "Access denied" }]));

        PathFindingViewModel finding = Assert.Single(viewModel.Findings);
        SimilarityGroupViewModel group = Assert.Single(viewModel.Groups);
        Assert.False(finding.IsSelected);
        Assert.All(group.Items, static item => Assert.False(item.IsSelected));
        Assert.Equal(0, viewModel.SelectedItemCount);
        Assert.Equal(Visibility.Visible, viewModel.SkippedPathsVisibility);
        Assert.Contains(@"C:\photos\locked.jpg", viewModel.SkippedPathsDetailsText);
        Assert.Equal(2, viewModel.ResultItems.Count);
    }

    [Fact]
    public void SearchKeepsCanonicalSelectionsAndHiddenSelectionTotals()
    {
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store);
        store.SetCompleted(
            ToolKind.BigFiles,
            new AnalysisScope(),
            NewResult(
                [
                    NewFinding(@"C:\visible\large.iso", 100),
                    NewFinding(@"C:\hidden\archive.zip", 250),
                ]));
        PathFindingViewModel hidden = viewModel.Findings.Single(item => item.FullPath == @"C:\hidden\archive.zip");
        hidden.IsSelected = true;

        viewModel.SearchText = "visible";

        Assert.Single(viewModel.Findings);
        Assert.Equal(@"C:\visible\large.iso", viewModel.Findings[0].FullPath);
        Assert.Equal(1, viewModel.SelectedItemCount);
        Assert.Equal(250, viewModel.SelectedBytes);
        Assert.Contains(viewModel.SelectedFindings, item => item.FullPath == hidden.FullPath);

        viewModel.SearchText = string.Empty;
        Assert.Equal(2, viewModel.Findings.Count);
        Assert.True(viewModel.Findings.Single(item => item.FullPath == hidden.FullPath).IsSelected);
    }

    [Fact]
    public void SortReordersVisibleCollectionsWithoutChangingSelection()
    {
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store);
        store.SetCompleted(
            ToolKind.BigFiles,
            new AnalysisScope(),
            NewResult(
                [
                    NewFinding(@"C:\scan\small.bin", 10),
                    NewFinding(@"C:\scan\large.bin", 200),
                ],
                [
                    NewGroup("b", @"C:\scan\b.jpg", @"C:\scan\b-copy.jpg", 20),
                    NewGroup("a", @"C:\scan\a.jpg", @"C:\scan\a-copy.jpg", 300),
                ]));
        viewModel.Findings.Single(item => item.FullPath == @"C:\scan\small.bin").IsSelected = true;

        viewModel.SelectedSortIndex = 1;

        Assert.Equal(@"C:\scan\large.bin", viewModel.Findings[0].FullPath);
        Assert.Equal("a", viewModel.Groups[0].Id);
        Assert.Equal(1, viewModel.SelectedItemCount);
        Assert.True(viewModel.Findings.Single(item => item.FullPath == @"C:\scan\small.bin").IsSelected);
    }

    [Fact]
    public void SimilaritySelectionsContributeToTotalsEvenWhenGroupIsFilteredOut()
    {
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store);
        store.SetCompleted(
            ToolKind.SimilarImages,
            new AnalysisScope(),
            NewResult(groups: [NewGroup("hidden", @"C:\hidden\one.jpg", @"C:\hidden\two.jpg", 75)]));
        SimilarityItemViewModel selected = viewModel.Groups[0].Items[1];
        selected.IsSelected = true;

        viewModel.SearchText = "no match";

        Assert.Empty(viewModel.Groups);
        Assert.Equal(1, viewModel.SelectedItemCount);
        Assert.Equal(75, viewModel.SelectedBytes);
        Assert.Contains(viewModel.SelectedSimilarityItems, item => item.FullPath == selected.FullPath);
    }

    [Fact]
    public void PreviewSelectionDoesNotChangeActionSelection()
    {
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store);
        store.SetCompleted(
            ToolKind.EmptyFiles,
            new AnalysisScope(),
            NewResult([NewFinding(@"C:\scan\empty.txt", 0)]));
        PathFindingViewModel finding = Assert.Single(viewModel.Findings);

        viewModel.SelectedResult = finding;

        Assert.Same(finding, viewModel.SelectedResult);
        Assert.Equal(@"C:\scan\empty.txt", viewModel.PreviewPath);
        Assert.False(finding.IsSelected);
        Assert.Equal(0, viewModel.SelectedItemCount);
    }

    [Fact]
    public void NewAnalysisClearsSessionFiltersSelectionsAndPreview()
    {
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store);
        store.SetCompleted(
            ToolKind.BigFiles,
            new AnalysisScope(),
            NewResult([NewFinding(@"C:\scan\large.iso", 500)]));
        viewModel.Findings[0].IsSelected = true;
        viewModel.SelectedResult = viewModel.Findings[0];
        viewModel.SearchText = "large";

        viewModel.NewAnalysisCommand.Execute(null);

        Assert.Null(store.CurrentSession);
        Assert.Empty(viewModel.Findings);
        Assert.Empty(viewModel.Groups);
        Assert.Empty(viewModel.ResultItems);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Null(viewModel.SelectedResult);
        Assert.Equal(0, viewModel.SelectedItemCount);
        Assert.Equal(Visibility.Visible, viewModel.BeforeFirstAnalysisVisibility);
    }

    private static AnalysisResult NewResult(
        IReadOnlyList<PathFinding>? findings = null,
        IReadOnlyList<SimilarityGroup>? groups = null,
        IReadOnlyList<SkippedPath>? skippedPaths = null) => new()
        {
            Findings = findings ?? [],
            Groups = groups ?? [],
            SkippedPaths = skippedPaths ?? [],
            Elapsed = TimeSpan.FromSeconds(2),
        };

    private static PathFinding NewFinding(string path, long size) => new()
    {
        FullPath = path,
        Kind = PathFindingKind.File,
        Reason = "Test finding",
        Suggestion = "Review this item.",
        SizeBytes = size,
        ModifiedUtc = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc),
    };

    private static SimilarityGroup NewGroup(
        string id,
        string referencePath,
        string candidatePath,
        long candidateSize = 50)
    {
        SimilarityItem reference = NewSimilarityItem(referencePath, 100, 10);
        return new SimilarityGroup
        {
            Id = id,
            ReferenceItem = reference,
            Items = [reference, NewSimilarityItem(candidatePath, 88, candidateSize)],
        };
    }

    private static SimilarityItem NewSimilarityItem(string path, double similarity, long size) => new()
    {
        FullPath = path,
        SizeBytes = size,
        ModifiedUtc = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc),
        SimilarityPercent = similarity,
    };
}
