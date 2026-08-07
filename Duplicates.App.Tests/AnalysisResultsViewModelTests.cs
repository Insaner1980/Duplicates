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
    public void SkippedOnlyResultKeepsWarningStateVisibleWithoutResultRows()
    {
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store);

        store.SetCompleted(
            ToolKind.BrokenFiles,
            new AnalysisScope(),
            NewResult(skippedPaths:
                [new SkippedPath { Path = @"C:\scan\locked.bin", Reason = "Access denied" }]));

        Assert.True(viewModel.HasSkippedPaths);
        Assert.Equal(Visibility.Visible, viewModel.SkippedPathsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.ResultsVisibility);
        Assert.Contains(@"C:\scan\locked.bin", viewModel.SkippedPathsDetailsText);
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
    public void SortOrdersMixedResultRowsGloballyWithPathTieBreakers()
    {
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store);
        DateTime first = new(2026, 8, 7, 10, 0, 0, DateTimeKind.Utc);
        DateTime second = first.AddHours(1);
        DateTime third = second.AddHours(1);
        store.SetCompleted(
            ToolKind.SimilarImages,
            new AnalysisScope(),
            NewResult(
                [
                    NewFinding(@"C:\scan\c.bin", 100, second),
                    NewFinding(@"C:\scan\b.bin", 50, third),
                ],
                [NewGroup("a", @"C:\scan\a.jpg", @"C:\scan\a-copy.jpg", 90, second, 10)]));

        (int SortIndex, string[] ExpectedPaths)[] cases =
        [
            (0, [@"C:\scan\a.jpg", @"C:\scan\b.bin", @"C:\scan\c.bin"]),
            (1, [@"C:\scan\a.jpg", @"C:\scan\c.bin", @"C:\scan\b.bin"]),
            (2, [@"C:\scan\b.bin", @"C:\scan\a.jpg", @"C:\scan\c.bin"]),
        ];

        foreach ((int sortIndex, string[] expectedPaths) in cases)
        {
            viewModel.SelectedSortIndex = sortIndex;
            Assert.Equal(expectedPaths, viewModel.ResultItems.Select(ResultPath));
        }
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
    public void PathAndSimilarityActionSelectionRemainIndependentFromPreview()
    {
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store);
        store.SetCompleted(
            ToolKind.SimilarImages,
            new AnalysisScope(),
            NewResult(
                [NewFinding(@"C:\scan\finding.jpg", 30)],
                [NewGroup("group", @"C:\scan\reference.jpg", @"C:\scan\candidate.jpg", 40)]));
        PathFindingViewModel finding = Assert.Single(viewModel.Findings);
        SimilarityGroupViewModel group = Assert.Single(viewModel.Groups);
        SimilarityItemViewModel candidate = group.Items.Single(item => !item.IsReference);

        finding.IsSelected = true;
        candidate.IsSelected = true;
        viewModel.SelectedResult = group;

        Assert.True(finding.IsSelected);
        Assert.True(candidate.IsSelected);
        Assert.False(group.ReferenceItem.IsSelected);
        Assert.Equal(2, viewModel.SelectedItemCount);
        Assert.Equal(70, viewModel.SelectedBytes);
        Assert.Same(group, viewModel.SelectedResult);
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

    [Fact]
    public async Task BigFileDelete_UsesStoredRunThresholdAndCurrentLengthForTarget()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "large.bin");
        await File.WriteAllBytesAsync(path, new byte[12]);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService
            {
                NextSummary = new DeleteSummary(1, 12, []),
            };
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            var options = new LargeFileToolOptions(10);
            store.SetCompleted(
                ToolKind.BigFiles,
                new AnalysisScope { IncludedFolders = [root] },
                options,
                NewResult([NewFinding(path, 100)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;
            IReadOnlyList<FileActionTarget>? requestedTargets = null;
            fileActions.OnDelete = (targets, _) => requestedTargets = targets;

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Same(options, Assert.IsType<AnalysisSession>(store.CurrentSession).ToolOptions);
            Assert.Equal(
                [new FileActionTarget(path, 12, FileActionTargetKind.File)],
                requestedTargets);
            Assert.Equal(1, summary.DeletedCount);
            Assert.Empty(viewModel.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BigFileDelete_RejectsFileBelowStoredThresholdAndKeepsCanonicalSelection()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "changed.bin");
        await File.WriteAllBytesAsync(path, new byte[9]);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            PathFinding finding = NewFinding(path, 100) with
            {
                Metadata = new Dictionary<string, string> { ["MinimumSizeBytes"] = "1" },
            };
            store.SetCompleted(
                ToolKind.BigFiles,
                new AnalysisScope { IncludedFolders = [root] },
                new LargeFileToolOptions(10),
                NewResult([finding]));
            PathFindingViewModel selected = Assert.Single(viewModel.Findings);
            selected.IsSelected = true;

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            FileActionFailure failure = Assert.Single(summary.Failures);
            Assert.Equal(path, failure.Path);
            Assert.Equal("File changed since scan.", failure.Reason);
            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.Same(selected, Assert.Single(viewModel.Findings));
            Assert.True(selected.IsSelected);
            Assert.Contains(selected, viewModel.SelectedFindings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyFileDelete_RejectsChangedAndMissingFilesWithoutCallingService()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string changedPath = Path.Combine(root, "changed.txt");
        string missingPath = Path.Combine(root, "missing.txt");
        await File.WriteAllBytesAsync(changedPath, [1]);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.EmptyFiles,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult([NewFinding(changedPath, 0), NewFinding(missingPath, 0)]));
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(2, summary.Failures.Count);
            Assert.All(summary.Failures, static failure => Assert.Equal("File changed since scan.", failure.Reason));
            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.Equal(2, viewModel.Findings.Count);
            Assert.All(viewModel.Findings, static finding => Assert.True(finding.IsSelected));
            Assert.Equal(2, viewModel.SelectedFindings.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static PathFinding NewFinding(string path, long size, DateTime? modifiedUtc = null) => new()
    {
        FullPath = path,
        Kind = PathFindingKind.File,
        Reason = "Test finding",
        Suggestion = "Review this item.",
        SizeBytes = size,
        ModifiedUtc = modifiedUtc ?? new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc),
    };

    private static SimilarityGroup NewGroup(
        string id,
        string referencePath,
        string candidatePath,
        long candidateSize = 50,
        DateTime? modifiedUtc = null,
        long referenceSize = 10)
    {
        SimilarityItem reference = NewSimilarityItem(referencePath, 100, referenceSize, modifiedUtc);
        return new SimilarityGroup
        {
            Id = id,
            ReferenceItem = reference,
            Items = [reference, NewSimilarityItem(candidatePath, 88, candidateSize, modifiedUtc)],
        };
    }

    private static SimilarityItem NewSimilarityItem(
        string path,
        double similarity,
        long size,
        DateTime? modifiedUtc = null) => new()
        {
            FullPath = path,
            SizeBytes = size,
            ModifiedUtc = modifiedUtc ?? new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc),
            SimilarityPercent = similarity,
        };

    private static string ResultPath(object item) => item switch
    {
        PathFindingViewModel finding => finding.FullPath,
        SimilarityGroupViewModel group => group.FullPath,
        _ => throw new InvalidOperationException(),
    };
}
