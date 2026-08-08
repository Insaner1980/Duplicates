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

    [Fact]
    public async Task EmptyFolderDelete_RevalidatesAndDispatchesSelectedFoldersDeepestFirst()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        string shallow = Path.Combine(root, "shallow");
        string outer = Path.Combine(root, "outer");
        string deep = Path.Combine(outer, "deep");
        Directory.CreateDirectory(shallow);
        Directory.CreateDirectory(deep);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService
            {
                NextSummary = new DeleteSummary(2, 0, []),
            };
            IReadOnlyList<FileActionTarget>? requestedTargets = null;
            fileActions.OnDelete = (targets, _) => requestedTargets = targets;
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.EmptyFolders,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult(
                [
                    NewDirectoryFinding(shallow, depth: 1),
                    NewDirectoryFinding(deep, depth: 2),
                ]));
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(
                [
                    new FileActionTarget(deep, 0, FileActionTargetKind.Directory),
                    new FileActionTarget(shallow, 0, FileActionTargetKind.Directory),
                ],
                requestedTargets);
            Assert.Equal(2, summary.DeletedCount);
            Assert.Empty(summary.Failures);
            Assert.Empty(viewModel.Findings);
            Assert.DoesNotContain(requestedTargets!, target => target.FullPath == outer || target.FullPath == root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyFolderDelete_RejectsNonEmptyAndMissingFoldersWithoutCallingService()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        string nonEmpty = Path.Combine(root, "non-empty");
        string missing = Path.Combine(root, "missing");
        Directory.CreateDirectory(nonEmpty);
        await File.WriteAllTextAsync(Path.Combine(nonEmpty, "added.txt"), "changed");

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.EmptyFolders,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult([NewDirectoryFinding(nonEmpty, 1), NewDirectoryFinding(missing, 1)]));
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(2, summary.Failures.Count);
            Assert.All(
                summary.Failures,
                static failure => Assert.Equal("Folder is no longer empty.", failure.Reason));
            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.Equal(2, viewModel.Findings.Count);
            Assert.All(viewModel.Findings, static finding => Assert.True(finding.IsSelected));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryFileDelete_UsesStoredOptionsCurrentLengthAndClosesWriteCheckBeforeDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "cache.TMP");
        await File.WriteAllBytesAsync(path, new byte[12]);
        DateTime utcNow = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, utcNow.AddDays(-7));

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService
            {
                NextSummary = new DeleteSummary(1, 12, []),
            };
            IReadOnlyList<FileActionTarget>? requestedTargets = null;
            fileActions.OnDelete = (targets, _) =>
            {
                requestedTargets = targets;
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            };
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            var options = new TemporaryFileToolOptions(TimeSpan.FromDays(7), utcNow);
            store.SetCompleted(
                ToolKind.TemporaryFiles,
                new AnalysisScope { IncludedFolders = [root] },
                options,
                NewResult([NewFinding(path, 100)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Same(options, Assert.IsType<AnalysisSession>(store.CurrentSession).ToolOptions);
            Assert.Equal([new FileActionTarget(path, 12, FileActionTargetKind.File)], requestedTargets);
            Assert.Equal(1, summary.DeletedCount);
            Assert.Empty(viewModel.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryFileDelete_RejectsChangedFreshActiveAndMissingFilesWithoutCallingService()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string changedName = Path.Combine(root, "changed.txt");
        string fresh = Path.Combine(root, "fresh.tmp");
        string active = Path.Combine(root, "active.tmp");
        string missing = Path.Combine(root, "missing.tmp");
        await File.WriteAllBytesAsync(changedName, [1]);
        await File.WriteAllBytesAsync(fresh, [1]);
        await File.WriteAllBytesAsync(active, [1]);
        DateTime utcNow = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(changedName, utcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(fresh, utcNow.AddDays(-6));
        File.SetLastWriteTimeUtc(active, utcNow.AddDays(-30));

        try
        {
            using var activeHandle = new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.Read);
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.TemporaryFiles,
                new AnalysisScope { IncludedFolders = [root] },
                new TemporaryFileToolOptions(TimeSpan.FromDays(7), utcNow),
                NewResult(
                [
                    NewFinding(changedName, 1),
                    NewFinding(fresh, 1),
                    NewFinding(active, 1),
                    NewFinding(missing, 1),
                ]));
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(4, summary.Failures.Count);
            Assert.All(
                summary.Failures,
                static failure => Assert.Equal("File is active or changed.", failure.Reason));
            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.Equal(4, viewModel.Findings.Count);
            Assert.All(viewModel.Findings, static finding => Assert.True(finding.IsSelected));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryFileDelete_MergesLocalAndServiceFailuresAndRemovesOnlySuccesses()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string success = Path.Combine(root, "success.tmp");
        string serviceFailure = Path.Combine(root, "service-failure.tmp");
        string localFailure = Path.Combine(root, "changed.txt");
        await File.WriteAllBytesAsync(success, [1]);
        await File.WriteAllBytesAsync(serviceFailure, [2]);
        await File.WriteAllBytesAsync(localFailure, [3]);
        DateTime utcNow = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        foreach (string path in new[] { success, serviceFailure, localFailure })
        {
            File.SetLastWriteTimeUtc(path, utcNow.AddDays(-30));
        }

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService
            {
                NextSummary = new DeleteSummary(
                    1,
                    1,
                    [new FileActionFailure(serviceFailure, "Service failure")]),
            };
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.TemporaryFiles,
                new AnalysisScope { IncludedFolders = [root] },
                new TemporaryFileToolOptions(TimeSpan.FromDays(7), utcNow),
                NewResult(
                [
                    NewFinding(success, 1),
                    NewFinding(serviceFailure, 1),
                    NewFinding(localFailure, 1),
                ]));
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(1, fileActions.DeleteCallCount);
            Assert.Equal(1, summary.DeletedCount);
            Assert.Equal(2, summary.Failures.Count);
            Assert.Contains(summary.Failures, failure =>
                failure.Path == localFailure && failure.Reason == "File is active or changed.");
            Assert.Contains(summary.Failures, failure =>
                failure.Path == serviceFailure && failure.Reason == "Service failure");
            Assert.Equal(
                new[] { localFailure, serviceFailure }.Order(StringComparer.OrdinalIgnoreCase),
                viewModel.Findings.Select(static finding => finding.FullPath).Order(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            Assert.All(viewModel.Findings, static finding => Assert.True(finding.IsSelected));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidLinkDelete_DispatchesOnlyMissingFileLinkEntry()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "missing-target.txt");
        string link = Path.Combine(root, "broken-link");
        CreateFileSymbolicLinkOrSkip(link, target);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService
            {
                NextSummary = new DeleteSummary(1, 0, []),
            };
            IReadOnlyList<FileActionTarget>? requestedTargets = null;
            fileActions.OnDelete = (targets, _) => requestedTargets = targets;
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.InvalidLinks,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult([NewLinkFinding(link, "File", target)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;
            Assert.True(viewModel.CanActOnSelection);

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            FileActionTarget requested = Assert.Single(requestedTargets!);
            Assert.Equal(link, requested.FullPath);
            Assert.Equal(0, requested.SizeBytes);
            Assert.Equal(FileActionTargetKind.FileLink, requested.Kind);
            Assert.Equal("Link target is missing.", requested.ExpectedInvalidLinkReason);
            Assert.NotEqual(target, requested.FullPath);
            Assert.Equal(1, summary.DeletedCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidLinkMove_DispatchesOnlyMissingDirectoryLinkEntry()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "missing-target");
        string link = Path.Combine(root, "broken-link");
        CreateDirectorySymbolicLinkOrSkip(link, target);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService
            {
                NextMoveSummary = new FileOperationSummary(
                    [new FileOperationResult(link, Path.Combine(root, "destination", "broken-link"), null)],
                    0),
            };
            IReadOnlyList<FileActionTarget>? requestedTargets = null;
            fileActions.OnMove = (targets, _, _, _) => requestedTargets = targets;
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.InvalidLinks,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult([NewLinkFinding(link, "Directory", target)]));
            Assert.Single(viewModel.Findings).IsSelected = true;

            FileOperationSummary summary = await viewModel.MoveSelectedAsync(
                Path.Combine(root, "destination"),
                MoveCollisionBehavior.Skip,
                CancellationToken.None);

            FileActionTarget requested = Assert.Single(requestedTargets!);
            Assert.Equal(link, requested.FullPath);
            Assert.Equal(FileActionTargetKind.DirectoryLink, requested.Kind);
            Assert.Equal("Link target is missing.", requested.ExpectedInvalidLinkReason);
            Assert.NotEqual(target, requested.FullPath);
            Assert.True(Assert.Single(summary.Results).Succeeded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidLinkRename_DispatchesOnlyMissingLinkEntry()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "missing-target.txt");
        string link = Path.Combine(root, "broken-link");
        CreateFileSymbolicLinkOrSkip(link, target);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new RecordingFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.InvalidLinks,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult([NewLinkFinding(link, "File", target)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);

            FileOperationResult result = await viewModel.RenameFindingAsync(
                finding,
                "renamed-link",
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.NotNull(fileActions.RenameTarget);
            Assert.Equal(link, fileActions.RenameTarget.FullPath);
            Assert.Equal(FileActionTargetKind.FileLink, fileActions.RenameTarget.Kind);
            Assert.Equal("Link target is missing.", fileActions.RenameTarget.ExpectedInvalidLinkReason);
            Assert.NotEqual(target, fileActions.RenameTarget.FullPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidLinkDelete_RejectsMetadataSourceAndClassificationChangesFailClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string lowerCaseMetadata = Path.Combine(root, "lowercase-metadata");
        string wrongKind = Path.Combine(root, "wrong-kind");
        string noLongerLink = Path.Combine(root, "no-longer-link");
        string changedClassification = Path.Combine(root, "changed-classification");
        CreateFileSymbolicLinkOrSkip(lowerCaseMetadata, "missing-lowercase");
        CreateFileSymbolicLinkOrSkip(wrongKind, "missing-kind");
        CreateFileSymbolicLinkOrSkip(noLongerLink, "missing-regular");
        File.Delete(noLongerLink);
        await File.WriteAllTextAsync(noLongerLink, "replacement");
        CreateFileSymbolicLinkOrSkip(changedClassification, "changed-classification");

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.InvalidLinks,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult(
                [
                    NewLinkFinding(lowerCaseMetadata, "file", "missing-lowercase"),
                    NewLinkFinding(wrongKind, "Directory", "missing-kind"),
                    NewLinkFinding(noLongerLink, "File", "missing-regular"),
                    NewLinkFinding(changedClassification, "File", "missing-before-scan"),
                ]));
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(4, summary.Failures.Count);
            Assert.All(summary.Failures, static failure => Assert.Equal("File changed since scan.", failure.Reason));
            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.Equal(4, viewModel.Findings.Count);
            Assert.All(viewModel.Findings, static finding => Assert.True(finding.IsSelected));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidLinkDelete_RejectsLinkThatNowResolvesSuccessfully()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "target.txt");
        string link = Path.Combine(root, "link");
        await File.WriteAllTextAsync(target, "target");
        CreateFileSymbolicLinkOrSkip(link, target);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.InvalidLinks,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult([NewLinkFinding(link, "File", "missing-at-scan")]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            FileActionFailure failure = Assert.Single(summary.Failures);
            Assert.Equal(link, failure.Path);
            Assert.Equal("File changed since scan.", failure.Reason);
            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.True(finding.IsSelected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BadExtensionsDeleteAndMove_FailClosedWithoutServiceDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = await WritePngAsync(Path.Combine(root, "photo.txt"));

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadExtensions,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult([NewBadExtensionFinding(path)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;

            Assert.False(viewModel.CanActOnSelection);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                viewModel.DeleteSelectedAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                viewModel.MoveSelectedAsync(root, MoveCollisionBehavior.Skip, CancellationToken.None));
            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.Equal(0, fileActions.MoveCallCount);
            Assert.True(finding.IsSelected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BadExtensionsRename_RevalidatesAndDispatchesRecommendedNameOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = await WritePngAsync(Path.Combine(root, "photo.txt"));

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadExtensions,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult([NewBadExtensionFinding(path)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);

            FileOperationResult result = await viewModel.RenameFindingAsync(
                finding,
                "photo.png",
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(1, fileActions.RenameCallCount);
            Assert.Equal(path, fileActions.LastRenameTarget?.FullPath);
            Assert.Equal(FileActionTargetKind.File, fileActions.LastRenameTarget?.Kind);
            Assert.Equal(new FileInfo(path).Length, fileActions.LastRenameTarget?.SizeBytes);
            Assert.Equal("photo.png", fileActions.LastRenameName);
            Assert.Empty(viewModel.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BadExtensionsRename_RejectsStaleOrMismatchedFindingWithoutDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            string changedLength = await WritePngAsync(Path.Combine(root, "changed-length.txt"));
            PathFinding changedLengthFinding = NewBadExtensionFinding(changedLength);
            await File.AppendAllBytesAsync(changedLength, [0x00]);

            string changedTime = await WritePngAsync(Path.Combine(root, "changed-time.txt"));
            PathFinding changedTimeFinding = NewBadExtensionFinding(changedTime);
            File.SetLastWriteTimeUtc(changedTime, changedTimeFinding.ModifiedUtc!.Value.AddSeconds(2));

            string changedSignature = await WritePngAsync(Path.Combine(root, "changed-signature.txt"));
            PathFinding changedSignatureFinding = NewBadExtensionFinding(changedSignature);
            await File.WriteAllBytesAsync(changedSignature, [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80]);
            File.SetLastWriteTimeUtc(changedSignature, changedSignatureFinding.ModifiedUtc!.Value);

            string changedType = await WritePngAsync(Path.Combine(root, "changed-type.txt"));
            PathFinding changedTypeFinding = NewBadExtensionFinding(changedType) with
            {
                Metadata = new Dictionary<string, string>
                {
                    ["CurrentExtension"] = ".txt",
                    ["ProperExtension"] = ".png",
                    ["DetectedType"] = "JPEG",
                },
            };

            string nowAllowed = await WritePngAsync(Path.Combine(root, "already.png"));
            PathFinding nowAllowedFinding = NewBadExtensionFinding(nowAllowed, currentExtension: ".txt");

            string wrongName = await WritePngAsync(Path.Combine(root, "wrong-name.txt"));
            PathFinding wrongNameFinding = NewBadExtensionFinding(wrongName);

            await AssertRenameRejectedAsync(changedLengthFinding, "changed-length.png");
            await AssertRenameRejectedAsync(changedTimeFinding, "changed-time.png");
            await AssertRenameRejectedAsync(changedSignatureFinding, "changed-signature.png");
            await AssertRenameRejectedAsync(changedTypeFinding, "changed-type.png");
            await AssertRenameRejectedAsync(nowAllowedFinding, "already.png");
            await AssertRenameRejectedAsync(wrongNameFinding, "different.png");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static async Task AssertRenameRejectedAsync(PathFinding source, string requestedName)
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadExtensions,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([source]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;

            FileOperationResult result = await viewModel.RenameFindingAsync(
                finding,
                requestedName,
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("File changed since scan.", result.Failure?.Reason);
            Assert.Equal(0, fileActions.RenameCallCount);
            Assert.Same(finding, Assert.Single(viewModel.Findings));
            Assert.True(finding.IsSelected);
        }
    }

    [Fact]
    public async Task BadExtensionsRename_RejectsReparsePointWithoutDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string target = await WritePngAsync(Path.Combine(root, "target.txt"));
        string link = Path.Combine(root, "link.txt");
        CreateFileSymbolicLinkOrSkip(link, target);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadExtensions,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadExtensionFinding(link)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;

            FileOperationResult result = await viewModel.RenameFindingAsync(
                finding,
                "link.png",
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("File changed since scan.", result.Failure?.Reason);
            Assert.Equal(0, fileActions.RenameCallCount);
            Assert.True(finding.IsSelected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BadExtensionsRename_PreservesFindingWhenServiceRejectsCollision()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = await WritePngAsync(Path.Combine(root, "photo.txt"));

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService
            {
                NextRenameResult = new FileOperationResult(
                    path,
                    null,
                    new FileActionFailure(path, "A file with the same name already exists.")),
            };
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadExtensions,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadExtensionFinding(path)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;

            FileOperationResult result = await viewModel.RenameFindingAsync(
                finding,
                "photo.png",
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(1, fileActions.RenameCallCount);
            Assert.Same(finding, Assert.Single(viewModel.Findings));
            Assert.True(finding.IsSelected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BadExtensionsRename_PropagatesCancellationBeforeDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = await WritePngAsync(Path.Combine(root, "photo.txt"));

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadExtensions,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadExtensionFinding(path)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;
            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                viewModel.RenameFindingAsync(finding, "photo.png", cancellationSource.Token));

            Assert.Equal(0, fileActions.RenameCallCount);
            Assert.True(finding.IsSelected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteCancellation_ReconcilesSuccessfulAnalysisPathsBeforeRethrowing()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "first.txt");
        string failed = Path.Combine(root, "failed.txt");
        string unattempted = Path.Combine(root, "unattempted.txt");
        await File.WriteAllBytesAsync(first, []);
        await File.WriteAllBytesAsync(failed, []);
        await File.WriteAllBytesAsync(unattempted, []);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService
            {
                NextDeleteCancellation = new DeleteOperationCanceledException(
                    new DeleteSummary(
                        1,
                        0,
                        [new FileActionFailure(failed, "Access denied")],
                        [first]),
                    new CancellationToken(canceled: true)),
            };
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.EmptyFiles,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult(
                [
                    NewFinding(first, 0),
                    NewFinding(failed, 0),
                    NewFinding(unattempted, 0),
                ]));
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            await Assert.ThrowsAsync<DeleteOperationCanceledException>(() =>
                viewModel.DeleteSelectedAsync(CancellationToken.None));

            Assert.DoesNotContain(viewModel.Findings, finding => finding.FullPath == first);
            Assert.Contains(viewModel.Findings, finding => finding.FullPath == failed && finding.IsSelected);
            Assert.Contains(viewModel.Findings, finding => finding.FullPath == unattempted && finding.IsSelected);
            Assert.Equal(2, viewModel.SelectedFindings.Count);
            Assert.Contains("cancelled", viewModel.ActionStatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(viewModel.IsActionRunning);
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

    private static PathFinding NewBadExtensionFinding(
        string path,
        string currentExtension = ".txt")
    {
        var file = new FileInfo(path);
        return new PathFinding
        {
            FullPath = path,
            Kind = PathFindingKind.File,
            Reason = "Extension does not match detected file type.",
            Suggestion = Path.GetFileNameWithoutExtension(path) + ".png",
            SizeBytes = file.Length,
            CreatedUtc = file.CreationTimeUtc,
            ModifiedUtc = file.LastWriteTimeUtc,
            Metadata = new Dictionary<string, string>
            {
                ["CurrentExtension"] = currentExtension,
                ["ProperExtension"] = ".png",
                ["DetectedType"] = "PNG",
            },
        };
    }

    private static async Task<string> WritePngAsync(string path)
    {
        await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    private static PathFinding NewDirectoryFinding(string path, int depth) => new()
    {
        FullPath = path,
        Kind = PathFindingKind.Directory,
        Reason = "Folder is empty",
        SizeBytes = 0,
        Metadata = new Dictionary<string, string>
        {
            ["Depth"] = depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
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

    private static PathFinding NewLinkFinding(
        string path,
        string linkKind,
        string immediateTarget,
        string reason = "Link target is missing.") => new()
        {
            FullPath = path,
            Kind = PathFindingKind.Link,
            Reason = reason,
            SizeBytes = 0,
            Metadata = new Dictionary<string, string>
            {
                ["LinkKind"] = linkKind,
                ["ImmediateTarget"] = immediateTarget,
            },
        };

    private static void CreateFileSymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (IsLinkCapabilityFailure(ex))
        {
            throw Xunit.Sdk.SkipException.ForSkip($"A file symbolic-link fixture cannot be created: {ex.Message}");
        }
    }

    private static void CreateDirectorySymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (IsLinkCapabilityFailure(ex))
        {
            throw Xunit.Sdk.SkipException.ForSkip($"A directory symbolic-link fixture cannot be created: {ex.Message}");
        }
    }

    private static bool IsLinkCapabilityFailure(Exception exception)
    {
        if (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return true;
        }

        int nativeError = exception.HResult & 0xFFFF;
        return exception is IOException && nativeError is 5 or 1314;
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

    private sealed class RecordingFileActionService : IFileActionService
    {
        public FileActionTarget? RenameTarget { get; private set; }

        public Task<DeleteSummary> DeleteAsync(
            IReadOnlyList<FileActionTarget> targets,
            IProgress<DeleteProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FileOperationSummary> MoveAsync(
            IReadOnlyList<FileActionTarget> targets,
            string destinationFolder,
            MoveCollisionBehavior collisionBehavior,
            IProgress<FileOperationProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FileOperationResult> RenameAsync(
            FileActionTarget target,
            string newName,
            CancellationToken cancellationToken)
        {
            RenameTarget = target;
            return Task.FromResult(new FileOperationResult(
                target.FullPath,
                Path.Combine(Path.GetDirectoryName(target.FullPath)!, newName),
                null));
        }

        public void OpenFile(string path) => throw new NotSupportedException();

        public void RevealInExplorer(string path) => throw new NotSupportedException();
    }
}
