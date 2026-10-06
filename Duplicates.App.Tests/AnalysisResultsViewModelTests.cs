using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;

namespace Duplicates.App.Tests;

public sealed class AnalysisResultsViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryFileActionRejectsFreshTimestampAtServiceBoundary(bool move)
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates.Temporary.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "cache.tmp");
        File.WriteAllBytes(path, [1]);
        DateTime utcNow = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, utcNow.AddDays(-7));
        try
        {
            int nativeCalls = 0;
            var native = new FileActionService(new FakeSettingsService(), (_, _) => nativeCalls++, (_, _) => nativeCalls++);
            var actions = new FakeFileActionService
            {
                DeleteHandler = (targets, token) =>
                {
                    File.SetLastWriteTimeUtc(path, utcNow);
                    return native.DeleteAsync(targets, null, token);
                },
                MoveHandler = (targets, destination, collision, token) =>
                {
                    File.SetLastWriteTimeUtc(path, utcNow);
                    return native.MoveAsync(targets, destination, collision, null, token);
                },
            };
            var store = new AnalysisSessionStore();
            var viewModel = new AnalysisResultsViewModel(store, actions, null);
            store.SetCompleted(ToolKind.TemporaryFiles, new AnalysisScope(),
                new TemporaryFileToolOptions(TimeSpan.FromDays(7), utcNow), NewResult([NewFinding(path, 1)]));
            viewModel.Findings[0].IsSelected = true;

            if (move)
            {
                FileOperationSummary summary = await viewModel.MoveSelectedAsync(root, MoveCollisionBehavior.KeepBoth, CancellationToken.None);
                Assert.NotNull(Assert.Single(summary.Results).Failure);
            }
            else
            {
                DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);
                Assert.Single(summary.Failures);
            }

            Assert.Equal(0, nativeCalls);
            Assert.True(File.Exists(path));
            Assert.True(Assert.Single(viewModel.Findings).IsSelected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationWithoutItemSummarySetsStatusOnlyForInitiatingSession(bool move, bool replaceSession)
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates.Cancellation.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "file.bin");
        File.WriteAllBytes(path, [1]);
        try
        {
            var store = new AnalysisSessionStore();
            var actions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, actions, null);
            void Cancel()
            {
                if (replaceSession)
                {
                    store.SetCompleted(ToolKind.EmptyFiles, new AnalysisScope(), NewResult());
                    viewModel.ActionStatusMessage = "Replacement status";
                }

                throw new OperationCanceledException();
            }

            actions.DeleteHandler = (_, _) => { Cancel(); return Task.FromResult(new DeleteSummary(0, 0, [])); };
            actions.MoveHandler = (_, _, _, _) => { Cancel(); return Task.FromResult(new FileOperationSummary([], 0)); };
            store.SetCompleted(ToolKind.BigFiles, new AnalysisScope(), new LargeFileToolOptions(0),
                NewResult([NewFinding(path, 1)]));
            viewModel.Findings[0].IsSelected = true;

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                if (move)
                {
                    await viewModel.MoveSelectedAsync(root, MoveCollisionBehavior.Skip, CancellationToken.None);
                }
                else
                {
                    await viewModel.DeleteSelectedAsync(CancellationToken.None);
                }
            });

            Assert.Equal(replaceSession ? "Replacement status" : "Action cancelled.", viewModel.ActionStatusMessage);
            Assert.False(viewModel.IsActionRunning);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("File renamed.")]
    [InlineData("Results exported.")]
    [InlineData("The operation failed.")]
    public void DelayedActionStatusTargetsOnlyItsCapturedSession(string status)
    {
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store);
        store.SetCompleted(ToolKind.BadNames, new AnalysisScope(), NewResult());
        AnalysisSession initiating = store.CurrentSession!;
        viewModel.SetActionStatusForSession(initiating, status);
        Assert.Equal(status, viewModel.ActionStatusMessage);

        store.SetCompleted(ToolKind.BigFiles, new AnalysisScope(), NewResult());
        viewModel.ActionStatusMessage = "Replacement status";
        viewModel.SetActionStatusForSession(initiating, status);
        Assert.Equal("Replacement status", viewModel.ActionStatusMessage);
    }

    public static TheoryData<Type> HeaderReadFailureTypes => new()
    {
        typeof(IOException),
        typeof(UnauthorizedAccessException),
        typeof(System.Security.SecurityException),
        typeof(ArgumentException),
        typeof(NotSupportedException),
    };

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
    public async Task SimilarVideos_PreviewAndActionSelectionStayIndependentAndExposeVideoDetails()
    {
        var store = new AnalysisSessionStore();
        var previewLoader = new RecordingPreviewLoader();
        var viewModel = new AnalysisResultsViewModel(
            store,
            new FakeFileActionService(),
            null,
            new VideoSimilarityAnalysisService(),
            previewLoader);
        SimilarityGroup source = NewVideoGroup(
            NewVideoItem(@"C:\videos\reference.mp4", 0, width: 1920, height: 1080, bitrate: 2_000_000),
            NewVideoItem(@"C:\videos\candidate.mp4", LowBits(4), width: 1280, height: 720, bitrate: 1_000_000));
        store.SetCompleted(
            ToolKind.SimilarVideos,
            new AnalysisScope(),
            new SimilarVideoToolOptions(9),
            NewResult(groups: [source]));
        SimilarityGroupViewModel group = Assert.Single(viewModel.Groups);
        SimilarityItemViewModel candidate = group.Items.Single(item => !item.IsReference);

        Assert.All(group.Items, static item => Assert.False(item.IsSelected));
        Assert.Contains("1920 × 1080", group.ReferenceItem.MediaDetailsText);
        Assert.Contains("bps", group.ReferenceItem.MediaDetailsText);
        Assert.Contains("30 FPS", group.ReferenceItem.MediaDetailsText);
        Assert.Contains("H264", group.ReferenceItem.MediaDetailsText);

        viewModel.SelectedResult = group;
        Assert.True(SpinWait.SpinUntil(() => previewLoader.Calls.Count == 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(ToolKind.SimilarVideos, previewLoader.Calls[0].Tool);
        Assert.Same(group.ReferenceItem.Source, previewLoader.Calls[0].Item);

        candidate.IsSelected = true;
        await viewModel.SelectSimilarityPreviewItemCommand.ExecuteAsync(candidate);

        Assert.True(candidate.IsSelected);
        Assert.False(group.ReferenceItem.IsSelected);
        Assert.Same(candidate, viewModel.SelectedSimilarityPreviewItem);
        Assert.Equal(1, viewModel.SelectedItemCount);
        Assert.True(viewModel.CanActOnSelection);
        Assert.False(viewModel.CanRenameSelection);
    }

    [Fact]
    public async Task SimilarVideos_ExportIncludesDisplayMetadataWithoutVideoEvidenceHashes()
    {
        var exporter = new FakeResultExportService();
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store, null, exporter);
        SimilarityGroup group = NewVideoGroup(
            NewVideoItem(@"C:\videos\reference.mp4", 0, width: 1920, height: 1080),
            NewVideoItem(@"C:\videos\candidate.mp4", LowBits(4), width: 1280, height: 720));
        store.SetCompleted(
            ToolKind.SimilarVideos,
            new AnalysisScope(),
            new SimilarVideoToolOptions(9),
            NewResult(groups: [group]));

        await viewModel.ExportAsync(ResultExportFormat.Json, @"C:\exports\videos.json", CancellationToken.None);

        ResultExportSnapshot snapshot = Assert.IsType<ResultExportSnapshot>(exporter.Snapshot);
        Assert.All(snapshot.Items, item =>
        {
            Assert.Equal("Video", item.Metadata["Type"]);
            Assert.True(item.Metadata.ContainsKey("MeanFrameDistance"));
            Assert.DoesNotContain(item.Metadata.Keys, key => key.StartsWith("FrameHash", StringComparison.Ordinal));
        });
        Assert.False(exporter.OverwriteExisting);

        System.Reflection.MethodInfo? pickerExport = typeof(AnalysisResultsViewModel).GetMethod(
            nameof(AnalysisResultsViewModel.ExportAsync),
            [typeof(ResultExportFormat), typeof(string), typeof(CancellationToken), typeof(bool)]);
        Assert.NotNull(pickerExport);
        await Assert.IsAssignableFrom<Task>(pickerExport.Invoke(
            viewModel,
            [ResultExportFormat.Csv, @"C:\exports\picked.csv", CancellationToken.None, true]));
        Assert.True(exporter.OverwriteExisting);
    }

    [Fact]
    public async Task SimilarVideos_DeleteReferenceRegroupsGloballyWithNewReferenceAndReboundPreview()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            SimilarityItem reference = WriteVideoItem(root, "a.mp4", 0, width: 300, height: 300);
            SimilarityItem bridge = WriteVideoItem(root, "b.mp4", LowBits(9), width: 200, height: 200, bitrate: 30);
            SimilarityItem tail = WriteVideoItem(root, "c.mp4", LowBits(18), width: 100, height: 100, bitrate: 40);
            var analysis = new VideoSimilarityAnalysisService();
            var actions = new FakeFileActionService
            {
                NextSummary = new DeleteSummary(1, reference.SizeBytes, [], [reference.FullPath]),
            };
            var loader = new RecordingPreviewLoader();
            var store = new AnalysisSessionStore();
            var viewModel = new AnalysisResultsViewModel(store, actions, null, analysis, loader);
            store.SetCompleted(
                ToolKind.SimilarVideos,
                new AnalysisScope(),
                new SimilarVideoToolOptions(9),
                NewResult(groups: [NewVideoGroup(reference, bridge, tail)]));
            SimilarityGroupViewModel initial = viewModel.Groups[0];
            SimilarityItemViewModel referenceViewModel = initial.ReferenceItem;
            SimilarityItemViewModel tailViewModel = initial.Items.Single(item => item.FullPath == tail.FullPath);
            referenceViewModel.IsSelected = true;
            await viewModel.SelectSimilarityPreviewItemCommand.ExecuteAsync(tailViewModel);

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(1, summary.DeletedCount);
            Assert.Equal([reference.FullPath], analysis.RevalidatedPaths);
            SimilarityGroupViewModel regrouped = Assert.Single(viewModel.Groups);
            Assert.Equal(bridge.FullPath, regrouped.ReferenceItem.FullPath);
            Assert.Equal("0", regrouped.ReferenceItem.Source.Metadata["MeanFrameDistance"]);
            Assert.Equal(100d, regrouped.ReferenceItem.Source.SimilarityPercent);
            Assert.Equal("9", regrouped.Items.Single(item => item.FullPath == tail.FullPath).Source.Metadata["MeanFrameDistance"]);
            Assert.Equal(tail.FullPath, viewModel.SelectedSimilarityPreviewItem?.FullPath);
            Assert.Equal(tail.FullPath, viewModel.PreviewPath);
            Assert.All(regrouped.Items, static item => Assert.False(item.IsSelected));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SimilarVideos_WholeGroupMayBeDeletedWithoutASurvivorRule()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            SimilarityItem reference = WriteVideoItem(root, "reference.mp4", 0, width: 200, height: 200);
            SimilarityItem candidate = WriteVideoItem(root, "candidate.mp4", LowBits(4));
            var analysis = new VideoSimilarityAnalysisService();
            var actions = new FakeFileActionService
            {
                NextSummary = new DeleteSummary(
                    2,
                    reference.SizeBytes + candidate.SizeBytes,
                    [],
                    [reference.FullPath, candidate.FullPath]),
            };
            IReadOnlyList<FileActionTarget>? dispatched = null;
            actions.OnDelete = (targets, _) => dispatched = targets;
            var store = new AnalysisSessionStore();
            var viewModel = new AnalysisResultsViewModel(store, actions, null, analysis, new RecordingPreviewLoader());
            store.SetCompleted(
                ToolKind.SimilarVideos,
                new AnalysisScope(),
                new SimilarVideoToolOptions(9),
                NewResult(groups: [NewVideoGroup(reference, candidate)]));
            foreach (SimilarityItemViewModel item in viewModel.Groups[0].Items)
            {
                item.IsSelected = true;
            }

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(2, summary.DeletedCount);
            Assert.Equal(
                [reference.FullPath, candidate.FullPath],
                Assert.IsAssignableFrom<IReadOnlyList<FileActionTarget>>(dispatched).Select(static target => target.FullPath));
            Assert.Empty(viewModel.Groups);
            Assert.Equal(2, analysis.RevalidatedPaths.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SimilarVideos_PartialMoveRegroupsArticulationAndPreservesDecodeFailureSelection()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            SimilarityItem first = WriteVideoItem(root, "a.mp4", 0, width: 300, height: 300);
            SimilarityItem articulation = WriteVideoItem(root, "b.mp4", LowBits(9), width: 200, height: 200);
            SimilarityItem tail = WriteVideoItem(root, "c.mp4", LowBits(18), width: 100, height: 100);
            SimilarityItem otherReference = WriteVideoItem(root, "x.mp4", ulong.MaxValue, width: 200, height: 200);
            SimilarityItem failed = WriteVideoItem(root, "y.mp4", ulong.MaxValue ^ 1, width: 100, height: 100);
            var analysis = new VideoSimilarityAnalysisService
            {
                Revalidate = item => string.Equals(item.FullPath, failed.FullPath, StringComparison.OrdinalIgnoreCase)
                    ? Task.FromException<bool>(new IOException("decode"))
                    : Task.FromResult(true),
            };
            string destination = Path.Combine(root, "moved");
            var actions = new FakeFileActionService
            {
                NextMoveSummary = new FileOperationSummary(
                    [new FileOperationResult(articulation.FullPath, Path.Combine(destination, "b.mp4"), null)],
                    articulation.SizeBytes),
            };
            var store = new AnalysisSessionStore();
            var viewModel = new AnalysisResultsViewModel(store, actions, null, analysis, new RecordingPreviewLoader());
            var skip = new SkippedPath { Path = Path.Combine(root, "skipped.mp4"), Reason = "Could not decode video." };
            store.SetCompleted(
                ToolKind.SimilarVideos,
                new AnalysisScope(),
                new SimilarVideoToolOptions(9),
                NewResult(
                    groups: [NewVideoGroup(first, articulation, tail), NewVideoGroup(otherReference, failed)],
                    skippedPaths: [skip]));
            SimilarityItemViewModel articulationViewModel = viewModel.Groups
                .SelectMany(static group => group.Items)
                .Single(item => item.FullPath == articulation.FullPath);
            SimilarityItemViewModel failedViewModel = viewModel.Groups
                .SelectMany(static group => group.Items)
                .Single(item => item.FullPath == failed.FullPath);
            articulationViewModel.IsSelected = true;
            failedViewModel.IsSelected = true;
            viewModel.SearchText = "y.mp4";
            viewModel.SelectedSortIndex = 1;

            FileOperationSummary summary = await viewModel.MoveSelectedAsync(
                destination,
                MoveCollisionBehavior.Skip,
                CancellationToken.None);

            Assert.Single(summary.Results, static result => result.Succeeded);
            FileOperationResult failedResult = Assert.Single(summary.Results, static result => !result.Succeeded);
            Assert.Equal(failed.FullPath, failedResult.SourcePath);
            Assert.Equal("Could not decode video.", failedResult.Failure?.Reason);
            SimilarityGroupViewModel remaining = Assert.Single(viewModel.Groups);
            Assert.Equal(otherReference.FullPath, remaining.ReferenceItem.FullPath);
            Assert.True(remaining.Items.Single(item => item.FullPath == failed.FullPath).IsSelected);
            Assert.Equal(1, viewModel.SelectedItemCount);
            Assert.Equal("y.mp4", viewModel.SearchText);
            Assert.Equal(1, viewModel.SelectedSortIndex);
            Assert.Same(skip, store.CurrentSession!.Result.SkippedPaths[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResetCapturedAnalysisClearsSessionFiltersSelectionsAndPreview()
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

        Assert.True(viewModel.TryResetSession(store.CurrentSession!));

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
        await File.WriteAllBytesAsync(path, new byte[12], TestContext.Current.CancellationToken);

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
        await File.WriteAllBytesAsync(path, new byte[9], TestContext.Current.CancellationToken);

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
        await File.WriteAllBytesAsync(changedPath, [1], TestContext.Current.CancellationToken);

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
        await File.WriteAllTextAsync(Path.Combine(nonEmpty, "added.txt"), "changed", TestContext.Current.CancellationToken);

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
        await File.WriteAllBytesAsync(path, new byte[12], TestContext.Current.CancellationToken);
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
            Assert.Equal([new FileActionTarget(path, 12, FileActionTargetKind.File,
                ExpectedModifiedUtc: utcNow.AddDays(-7))], requestedTargets);
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
        await File.WriteAllBytesAsync(changedName, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(fresh, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(active, [1], TestContext.Current.CancellationToken);
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
        await File.WriteAllBytesAsync(success, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(serviceFailure, [2], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(localFailure, [3], TestContext.Current.CancellationToken);
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
        await File.WriteAllTextAsync(noLongerLink, "replacement", TestContext.Current.CancellationToken);
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
        await File.WriteAllTextAsync(target, "target", TestContext.Current.CancellationToken);
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
    public async Task BadNamesDeleteAndMove_FailClosedWithoutServiceDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, " bad.txt");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadNames,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult([NewBadNameFinding(path)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;

            Assert.False(viewModel.CanActOnSelection);
            Assert.True(viewModel.CanRenameSelection);
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
    public async Task BadNamesRename_RevalidatesSafeDistinctNameAndRemovesCanonicalFinding()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, " bad.txt");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadNames,
                new AnalysisScope { IncludedFolders = [root] },
                new NoToolOptions(),
                NewResult([NewBadNameFinding(path)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;

            FileOperationResult result = await viewModel.RenameFindingAsync(
                finding,
                "bad.txt",
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(1, fileActions.RenameCallCount);
            Assert.Equal(path, fileActions.LastRenameTarget?.FullPath);
            Assert.Equal(FileActionTargetKind.File, fileActions.LastRenameTarget?.Kind);
            Assert.Equal(new FileInfo(path).Length, fileActions.LastRenameTarget?.SizeBytes);
            Assert.Null(fileActions.LastRenameTarget?.ExpectedBadExtensionContent);
            Assert.Equal("bad.txt", fileActions.LastRenameName);
            Assert.Empty(viewModel.Findings);
            Assert.False(viewModel.CanRenameSelection);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BadNamesRenameCandidateValidation_RequiresSafeDistinctCollisionFreeLeaf()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, " bad.txt");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(root, "taken.txt"), [2], TestContext.Current.CancellationToken);

        try
        {
            var store = new AnalysisSessionStore();
            var viewModel = new AnalysisResultsViewModel(store);
            store.SetCompleted(
                ToolKind.BadNames,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadNameFinding(path)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);

            Assert.False(viewModel.TryValidateRenameCandidate(finding, "CON.txt", out _, out _));
            Assert.False(viewModel.TryValidateRenameCandidate(finding, " bad.txt", out _, out _));
            Assert.False(viewModel.TryValidateRenameCandidate(finding, @"folder\bad.txt", out _, out _));
            Assert.False(viewModel.TryValidateRenameCandidate(finding, "TAKEN.TXT", out _, out string collision));
            Assert.Contains("already exists", collision, StringComparison.OrdinalIgnoreCase);
            Assert.True(viewModel.TryValidateRenameCandidate(
                finding,
                "bad.txt",
                out string destination,
                out string message));
            Assert.Equal(Path.Combine(root, "bad.txt"), destination);
            Assert.Equal(string.Empty, message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BadNamesRename_RejectsStaleReplacedUnsafeAndCollidingNamesWithoutDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            string changedLength = Path.Combine(root, " length.txt");
            await File.WriteAllBytesAsync(changedLength, [1], TestContext.Current.CancellationToken);
            PathFinding changedLengthFinding = NewBadNameFinding(changedLength);
            await File.AppendAllBytesAsync(changedLength, [2], TestContext.Current.CancellationToken);

            string changedTime = Path.Combine(root, " time.txt");
            await File.WriteAllBytesAsync(changedTime, [1], TestContext.Current.CancellationToken);
            PathFinding changedTimeFinding = NewBadNameFinding(changedTime);
            File.SetLastWriteTimeUtc(changedTime, changedTimeFinding.ModifiedUtc!.Value.AddSeconds(2));

            string replacedName = Path.Combine(root, " replaced.txt");
            await File.WriteAllBytesAsync(replacedName, [1], TestContext.Current.CancellationToken);
            PathFinding replacedNameFinding = NewBadNameFinding(replacedName) with
            {
                Metadata = new Dictionary<string, string> { ["CurrentName"] = "different.txt" },
            };

            string noLongerBad = Path.Combine(root, "good.txt");
            await File.WriteAllBytesAsync(noLongerBad, [1], TestContext.Current.CancellationToken);
            PathFinding noLongerBadFinding = NewBadNameFinding(noLongerBad);

            string unsafeRequest = Path.Combine(root, " unsafe.txt");
            await File.WriteAllBytesAsync(unsafeRequest, [1], TestContext.Current.CancellationToken);
            PathFinding unsafeRequestFinding = NewBadNameFinding(unsafeRequest);

            string collision = Path.Combine(root, " collision.txt");
            await File.WriteAllBytesAsync(collision, [1], TestContext.Current.CancellationToken);
            PathFinding collisionFinding = NewBadNameFinding(collision);
            await File.WriteAllBytesAsync(Path.Combine(root, "COLLISION.TXT"), [9], TestContext.Current.CancellationToken);

            await AssertBadNameRenameRejectedAsync(changedLengthFinding, "length.txt");
            await AssertBadNameRenameRejectedAsync(changedTimeFinding, "time.txt");
            await AssertBadNameRenameRejectedAsync(replacedNameFinding, "replaced.txt");
            await AssertBadNameRenameRejectedAsync(noLongerBadFinding, "renamed.txt");
            await AssertBadNameRenameRejectedAsync(unsafeRequestFinding, "CON.txt");
            await AssertBadNameRenameRejectedAsync(unsafeRequestFinding, " unsafe.txt");
            await AssertBadNameRenameRejectedAsync(unsafeRequestFinding, @"folder\unsafe.txt");
            await AssertBadNameRenameRejectedAsync(collisionFinding, "collision.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static async Task AssertBadNameRenameRejectedAsync(PathFinding source, string requestedName)
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadNames,
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
    public async Task BadNamesRename_RejectsReparsePointWithoutDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, " target.txt");
        await File.WriteAllBytesAsync(target, [1], TestContext.Current.CancellationToken);
        string link = Path.Combine(root, " link.txt");
        CreateFileSymbolicLinkOrSkip(link, target);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadNames,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadNameFinding(link)]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);

            FileOperationResult result = await viewModel.RenameFindingAsync(
                finding,
                "link.txt",
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("File changed since scan.", result.Failure?.Reason);
            Assert.Equal(0, fileActions.RenameCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BadNamesRename_OldFindingAfterSessionReplacementRejectsWithoutDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, " bad.txt");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);

        try
        {
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadNames,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadNameFinding(path)]));
            PathFindingViewModel oldFinding = Assert.Single(viewModel.Findings);

            store.SetCompleted(
                ToolKind.BadNames,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadNameFinding(path)]));
            PathFindingViewModel replacementFinding = Assert.Single(viewModel.Findings);
            replacementFinding.IsSelected = true;

            FileOperationResult result = await viewModel.RenameFindingAsync(
                oldFinding,
                "bad.txt",
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("File changed since scan.", result.Failure?.Reason);
            Assert.Equal(0, fileActions.RenameCallCount);
            Assert.Same(replacementFinding, Assert.Single(viewModel.Findings));
            Assert.True(replacementFinding.IsSelected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BadNamesRename_SessionChangedDuringServiceAwaitDoesNotRemoveReplacementFinding()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, " bad.txt");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);

        try
        {
            var renameStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var continueRename = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService
            {
                RenameHandler = async (target, newName, cancellationToken) =>
                {
                    renameStarted.SetResult();
                    await continueRename.Task.WaitAsync(cancellationToken);
                    return new FileOperationResult(
                        target.FullPath,
                        Path.Combine(Path.GetDirectoryName(target.FullPath)!, newName),
                        null);
                },
            };
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadNames,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadNameFinding(path)]));
            PathFindingViewModel originalFinding = Assert.Single(viewModel.Findings);

            Task<FileOperationResult> rename = viewModel.RenameFindingAsync(
                originalFinding,
                "bad.txt",
                CancellationToken.None);
            await renameStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            store.SetCompleted(
                ToolKind.BadNames,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadNameFinding(path)]));
            PathFindingViewModel replacementFinding = Assert.Single(viewModel.Findings);
            replacementFinding.IsSelected = true;
            continueRename.SetResult();

            FileOperationResult result = await rename;

            Assert.True(result.Succeeded);
            Assert.Equal(1, fileActions.RenameCallCount);
            Assert.Same(replacementFinding, Assert.Single(viewModel.Findings));
            Assert.True(replacementFinding.IsSelected);
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
            BadExtensionContentConstraint constraint = Assert.IsType<BadExtensionContentConstraint>(
                fileActions.LastRenameTarget?.ExpectedBadExtensionContent);
            Assert.Equal(finding.Source.ModifiedUtc, constraint.ModifiedUtc);
            Assert.Equal("PNG", constraint.DetectedType);
            Assert.Equal(".png", constraint.RecommendedExtension);
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
            await File.AppendAllBytesAsync(changedLength, [0x00], TestContext.Current.CancellationToken);

            string changedTime = await WritePngAsync(Path.Combine(root, "changed-time.txt"));
            PathFinding changedTimeFinding = NewBadExtensionFinding(changedTime);
            File.SetLastWriteTimeUtc(changedTime, changedTimeFinding.ModifiedUtc!.Value.AddSeconds(2));

            string changedSignature = await WritePngAsync(Path.Combine(root, "changed-signature.txt"));
            PathFinding changedSignatureFinding = NewBadExtensionFinding(changedSignature);
            await File.WriteAllBytesAsync(changedSignature, [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80], TestContext.Current.CancellationToken);
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
    public async Task BadExtensionsRename_SessionChangedDuringSignatureReadRejectsWithoutDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = await WritePngAsync(Path.Combine(root, "photo.txt"));

        try
        {
            var detectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var detectionResult = new TaskCompletionSource<DetectedFileType?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Func<string, CancellationToken, ValueTask<DetectedFileType?>> detectAsync = (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                detectionStarted.SetResult();
                return new ValueTask<DetectedFileType?>(detectionResult.Task);
            };
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService();
            var viewModel = new AnalysisResultsViewModel(
                store,
                fileActions,
                new FakeResultExportService(),
                detectAsync);
            store.SetCompleted(
                ToolKind.BadExtensions,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadExtensionFinding(path)]));
            PathFindingViewModel originalFinding = Assert.Single(viewModel.Findings);

            Task<FileOperationResult> rename = viewModel.RenameFindingAsync(
                originalFinding,
                "photo.png",
                CancellationToken.None);
            await detectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            store.SetCompleted(
                ToolKind.BadExtensions,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadExtensionFinding(path)]));
            PathFindingViewModel replacementFinding = Assert.Single(viewModel.Findings);
            replacementFinding.IsSelected = true;
            detectionResult.SetResult(new DetectedFileType(
                "PNG",
                new HashSet<string>([".png"], StringComparer.OrdinalIgnoreCase),
                ".png"));

            FileOperationResult result = await rename;

            Assert.False(result.Succeeded);
            Assert.Equal("File changed since scan.", result.Failure?.Reason);
            Assert.Equal(0, fileActions.RenameCallCount);
            Assert.Same(replacementFinding, Assert.Single(viewModel.Findings));
            Assert.True(replacementFinding.IsSelected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BadExtensionsRename_SessionChangedDuringServiceAwaitDoesNotRemoveReplacementFinding()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = await WritePngAsync(Path.Combine(root, "photo.txt"));

        try
        {
            var renameStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var continueRename = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var store = new AnalysisSessionStore();
            var fileActions = new FakeFileActionService
            {
                RenameHandler = async (target, newName, cancellationToken) =>
                {
                    renameStarted.SetResult();
                    await continueRename.Task.WaitAsync(cancellationToken);
                    return new FileOperationResult(
                        target.FullPath,
                        Path.Combine(Path.GetDirectoryName(target.FullPath)!, newName),
                        null);
                },
            };
            var viewModel = new AnalysisResultsViewModel(store, fileActions, new FakeResultExportService());
            store.SetCompleted(
                ToolKind.BadExtensions,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadExtensionFinding(path)]));
            PathFindingViewModel originalFinding = Assert.Single(viewModel.Findings);

            Task<FileOperationResult> rename = viewModel.RenameFindingAsync(
                originalFinding,
                "photo.png",
                CancellationToken.None);
            await renameStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            store.SetCompleted(
                ToolKind.BadExtensions,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([NewBadExtensionFinding(path)]));
            PathFindingViewModel replacementFinding = Assert.Single(viewModel.Findings);
            replacementFinding.IsSelected = true;
            continueRename.SetResult();

            FileOperationResult result = await rename;

            Assert.True(result.Succeeded);
            Assert.Equal(1, fileActions.RenameCallCount);
            Assert.Same(replacementFinding, Assert.Single(viewModel.Findings));
            Assert.True(replacementFinding.IsSelected);
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
        await File.WriteAllBytesAsync(first, [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(failed, [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(unattempted, [], TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task BrokenFilesDelete_ReprobesExactFindingAndRemovesCanonicalSuccess()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = await WritePngAsync(Path.Combine(root, "broken.png"));
            PathFinding source = NewBrokenFinding(path, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            var probe = BrokenProbe(FileProbeStatus.Invalid, "ImageDecodeFailure");
            var fileActions = new FakeFileActionService
            {
                NextSummary = new DeleteSummary(1, source.SizeBytes!.Value, [], [path]),
            };
            IReadOnlyList<FileActionTarget>? dispatched = null;
            fileActions.OnDelete = (targets, _) => dispatched = targets;
            var store = new AnalysisSessionStore();
            var viewModel = NewBrokenResultsViewModel(store, fileActions, probe);
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([source]));
            viewModel.Findings[0].IsSelected = true;

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(1, summary.DeletedCount);
            FileActionTarget target = Assert.Single(dispatched!);
            Assert.Equal(path, target.FullPath);
            Assert.Equal(source.ModifiedUtc, target.ExpectedModifiedUtc);
            Assert.Equal("PNG", Assert.Single(probe.Calls).DetectedType?.Name);
            Assert.Empty(viewModel.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrokenFilesDelete_HeaderReadFailureRevalidationDispatchesLockedFileWithoutProbe()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "locked.bin");
            await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
            PathFinding source = NewBrokenFinding(
                path,
                FileProbeStatus.Invalid,
                "HeaderReadFailure",
                detectedType: null,
                validator: "Header");
            var probe = BrokenProbe(FileProbeStatus.Valid, null);
            var fileActions = new FakeFileActionService
            {
                NextSummary = new DeleteSummary(1, source.SizeBytes!.Value, [], [path]),
            };
            IReadOnlyList<FileActionTarget>? dispatched = null;
            fileActions.OnDelete = (targets, _) => dispatched = targets;
            var store = new AnalysisSessionStore();
            var viewModel = NewBrokenResultsViewModel(store, fileActions, probe);
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([source]));
            viewModel.Findings[0].IsSelected = true;
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(1, summary.DeletedCount);
            FileActionTarget target = Assert.Single(dispatched!);
            Assert.Equal(path, target.FullPath);
            Assert.Equal(source.ModifiedUtc, target.ExpectedModifiedUtc);
            Assert.Empty(probe.Calls);
            Assert.Empty(viewModel.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(HeaderReadFailureTypes))]
    public async Task BrokenFilesDelete_HeaderReadFailureRevalidationDispatchesForEverySourceFailure(
        Type exceptionType)
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "unreadable.bin");
            await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
            PathFinding source = NewBrokenFinding(
                path,
                FileProbeStatus.Invalid,
                "HeaderReadFailure",
                detectedType: null,
                validator: "Header");
            Exception sourceFailure = Assert.IsAssignableFrom<Exception>(Activator.CreateInstance(exceptionType));
            Func<string, CancellationToken, ValueTask<DetectedFileType?>> detectAsync = (_, _) =>
                new(Task.FromException<DetectedFileType?>(sourceFailure));
            var probe = BrokenProbe(FileProbeStatus.Valid, null);
            var fileActions = new FakeFileActionService
            {
                NextSummary = new DeleteSummary(1, source.SizeBytes!.Value, [], [path]),
            };
            IReadOnlyList<FileActionTarget>? dispatched = null;
            fileActions.OnDelete = (targets, _) => dispatched = targets;
            var store = new AnalysisSessionStore();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, null, detectAsync, probe);
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([source]));
            viewModel.Findings[0].IsSelected = true;

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(1, summary.DeletedCount);
            Assert.Equal(path, Assert.Single(dispatched!).FullPath);
            Assert.Empty(probe.Calls);
            Assert.Empty(viewModel.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrokenFilesDelete_HeaderReadFailureAfterMutationRejectsChangedFile()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "changed.bin");
            await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
            PathFinding source = NewBrokenFinding(
                path,
                FileProbeStatus.Invalid,
                "HeaderReadFailure",
                detectedType: null,
                validator: "Header");
            Func<string, CancellationToken, ValueTask<DetectedFileType?>> detectAsync = (candidate, _) =>
            {
                File.AppendAllBytes(candidate, [4]);
                return new ValueTask<DetectedFileType?>(Task.FromException<DetectedFileType?>(
                    new System.Security.SecurityException("Header read failed.")));
            };
            var probe = BrokenProbe(FileProbeStatus.Valid, null);
            var fileActions = new FakeFileActionService();
            var store = new AnalysisSessionStore();
            var viewModel = new AnalysisResultsViewModel(store, fileActions, null, detectAsync, probe);
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([source]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.Equal("File changed since scan.", Assert.Single(summary.Failures).Reason);
            Assert.Empty(probe.Calls);
            Assert.Same(finding, Assert.Single(viewModel.Findings));
            Assert.True(finding.IsSelected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrokenFilesMove_ReprobesAndReconcilesCanonicalSuccess()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = await WritePngAsync(Path.Combine(root, "broken.png"));
            string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            PathFinding source = NewBrokenFinding(path, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            var probe = BrokenProbe(FileProbeStatus.Invalid, "ImageDecodeFailure");
            var fileActions = new FakeFileActionService
            {
                NextMoveSummary = new FileOperationSummary(
                    [new FileOperationResult(path, Path.Combine(destination, "broken.png"), null)],
                    source.SizeBytes!.Value),
            };
            IReadOnlyList<FileActionTarget>? dispatched = null;
            fileActions.OnMove = (targets, _, _, _) => dispatched = targets;
            var store = new AnalysisSessionStore();
            var viewModel = NewBrokenResultsViewModel(store, fileActions, probe);
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([source]));
            viewModel.Findings[0].IsSelected = true;

            FileOperationSummary summary = await viewModel.MoveSelectedAsync(
                destination,
                MoveCollisionBehavior.Skip,
                CancellationToken.None);

            Assert.True(Assert.Single(summary.Results).Succeeded);
            Assert.Equal(source.ModifiedUtc, Assert.Single(dispatched!).ExpectedModifiedUtc);
            Assert.Empty(viewModel.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrokenFilesDelete_RejectsRepairedStatusChangedAndStaleFindingsWithoutDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string repaired = await WritePngAsync(Path.Combine(root, "repaired.png"));
            string changedStatus = await WritePngAsync(Path.Combine(root, "changed-status.png"));
            string stale = await WritePngAsync(Path.Combine(root, "stale.png"));
            PathFinding repairedFinding = NewBrokenFinding(repaired, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            PathFinding statusFinding = NewBrokenFinding(changedStatus, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            PathFinding staleFinding = NewBrokenFinding(stale, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            File.SetLastWriteTimeUtc(stale, staleFinding.ModifiedUtc!.Value.AddSeconds(5));
            var probe = new FakeFileFormatProbe
            {
                Handler = (path, _, _) => Task.FromResult(path == repaired
                    ? new FileProbeResult(FileProbeStatus.Valid, null, null)
                    : new FileProbeResult(FileProbeStatus.UnsupportedOrProtected, "CodecUnavailable", null)),
            };
            var fileActions = new FakeFileActionService();
            var store = new AnalysisSessionStore();
            var viewModel = NewBrokenResultsViewModel(store, fileActions, probe);
            store.SetCompleted(
                ToolKind.BrokenFiles,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([repairedFinding, statusFinding, staleFinding]));
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.Equal(3, summary.Failures.Count);
            Assert.All(summary.Failures, static failure => Assert.Equal("File changed since scan.", failure.Reason));
            Assert.Equal(3, viewModel.SelectedFindings.Count);
            Assert.Equal(3, viewModel.Findings.Count);
            Assert.Equal(2, probe.Calls.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("ErrorType")]
    [InlineData("Validator")]
    [InlineData("DetectedType")]
    public async Task BrokenFilesDelete_RejectsEachExactRevalidationFieldMismatch(string mismatchedField)
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = await WritePngAsync(Path.Combine(root, "broken.png"));
            PathFinding source = NewBrokenFinding(
                path,
                FileProbeStatus.Invalid,
                "ImageDecodeFailure",
                "PNG",
                "Image");
            var metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal);
            var probe = BrokenProbe(
                FileProbeStatus.Invalid,
                mismatchedField == "ErrorType" ? "MediaOpenFailure" : "ImageDecodeFailure");
            if (mismatchedField == "Validator")
            {
                metadata["Validator"] = "Media";
            }
            else if (mismatchedField == "DetectedType")
            {
                metadata["DetectedType"] = "ZIP";
            }

            source = source with { Metadata = metadata };
            var fileActions = new FakeFileActionService();
            var store = new AnalysisSessionStore();
            var viewModel = NewBrokenResultsViewModel(store, fileActions, probe);
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([source]));
            viewModel.Findings[0].IsSelected = true;

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.Equal("File changed since scan.", Assert.Single(summary.Failures).Reason);
            Assert.Single(viewModel.Findings);
            Assert.True(viewModel.Findings[0].IsSelected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrokenFilesDelete_SessionReplacementDuringProbeRejectsOldFindingAndKeepsReplacement()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = await WritePngAsync(Path.Combine(root, "broken.png"));
            PathFinding oldFinding = NewBrokenFinding(path, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            PathFinding replacement = NewBrokenFinding(path, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var continueProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var probe = new FakeFileFormatProbe
            {
                Handler = async (_, _, cancellationToken) =>
                {
                    probeStarted.SetResult();
                    await continueProbe.Task.WaitAsync(cancellationToken);
                    return new FileProbeResult(FileProbeStatus.Invalid, "ImageDecodeFailure", null);
                },
            };
            var fileActions = new FakeFileActionService();
            var store = new AnalysisSessionStore();
            var viewModel = NewBrokenResultsViewModel(store, fileActions, probe);
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([oldFinding]));
            viewModel.Findings[0].IsSelected = true;

            Task<DeleteSummary> deleting = viewModel.DeleteSelectedAsync(CancellationToken.None);
            await probeStarted.Task;
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([replacement]));
            continueProbe.SetResult();
            DeleteSummary summary = await deleting;

            Assert.Equal(0, fileActions.DeleteCallCount);
            Assert.Equal("File changed since scan.", Assert.Single(summary.Failures).Reason);
            Assert.Same(replacement, Assert.Single(viewModel.Findings).Source);
            Assert.Equal(string.Empty, viewModel.ActionStatusMessage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrokenFilesMove_SessionReplacementDuringProbeDoesNotSetReplacementStatus()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = await WritePngAsync(Path.Combine(root, "broken.png"));
            PathFinding oldFinding = NewBrokenFinding(path, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            PathFinding replacement = NewBrokenFinding(path, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var continueProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var probe = new FakeFileFormatProbe
            {
                Handler = async (_, _, cancellationToken) =>
                {
                    probeStarted.SetResult();
                    await continueProbe.Task.WaitAsync(cancellationToken);
                    return new FileProbeResult(FileProbeStatus.Invalid, "ImageDecodeFailure", null);
                },
            };
            var fileActions = new FakeFileActionService();
            var store = new AnalysisSessionStore();
            var viewModel = NewBrokenResultsViewModel(store, fileActions, probe);
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([oldFinding]));
            viewModel.Findings[0].IsSelected = true;

            Task<FileOperationSummary> moving = viewModel.MoveSelectedAsync(
                Path.Combine(root, "moved"),
                MoveCollisionBehavior.Skip,
                CancellationToken.None);
            await probeStarted.Task;
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([replacement]));
            continueProbe.SetResult();
            FileOperationSummary summary = await moving;

            Assert.Equal(0, fileActions.MoveCallCount);
            Assert.Equal("File changed since scan.", Assert.Single(summary.Results).Failure!.Reason);
            Assert.Same(replacement, Assert.Single(viewModel.Findings).Source);
            Assert.Equal(string.Empty, viewModel.ActionStatusMessage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrokenFilesDelete_SessionReplacementDuringServiceAwaitDoesNotRemoveSamePathReplacement()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = await WritePngAsync(Path.Combine(root, "broken.png"));
            PathFinding oldFinding = NewBrokenFinding(path, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            PathFinding replacement = NewBrokenFinding(path, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var continueDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fileActions = new FakeFileActionService
            {
                DeleteHandler = async (targets, cancellationToken) =>
                {
                    deleteStarted.SetResult();
                    await continueDelete.Task.WaitAsync(cancellationToken);
                    FileActionTarget target = Assert.Single(targets);
                    return new DeleteSummary(1, target.SizeBytes, [], [target.FullPath]);
                },
            };
            var store = new AnalysisSessionStore();
            var viewModel = NewBrokenResultsViewModel(
                store,
                fileActions,
                BrokenProbe(FileProbeStatus.Invalid, "ImageDecodeFailure"));
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([oldFinding]));
            viewModel.Findings[0].IsSelected = true;

            Task<DeleteSummary> deleting = viewModel.DeleteSelectedAsync(CancellationToken.None);
            await deleteStarted.Task;
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([replacement]));
            continueDelete.SetResult();
            DeleteSummary summary = await deleting;

            Assert.Equal(1, summary.DeletedCount);
            Assert.Same(replacement, Assert.Single(viewModel.Findings).Source);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrokenFilesDelete_ServiceFailureKeepsCanonicalSelectionAndRenameFailsClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = await WritePngAsync(Path.Combine(root, "broken.png"));
            PathFinding source = NewBrokenFinding(path, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            var fileActions = new FakeFileActionService
            {
                NextSummary = new DeleteSummary(
                    0,
                    0,
                    [new FileActionFailure(path, "boundary rejection")],
                    []),
            };
            var store = new AnalysisSessionStore();
            var viewModel = NewBrokenResultsViewModel(
                store,
                fileActions,
                BrokenProbe(FileProbeStatus.Invalid, "ImageDecodeFailure"));
            store.SetCompleted(ToolKind.BrokenFiles, new AnalysisScope(), new NoToolOptions(), NewResult([source]));
            PathFindingViewModel finding = Assert.Single(viewModel.Findings);
            finding.IsSelected = true;

            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

            Assert.Single(summary.Failures);
            Assert.Single(viewModel.Findings);
            Assert.True(finding.IsSelected);
            Assert.False(viewModel.CanRenameSelection);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                viewModel.RenameFindingAsync(finding, "renamed.png", CancellationToken.None));
            Assert.Equal(0, fileActions.RenameCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrokenFilesDelete_TypedCancellationReconcilesCompletedTargetOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string first = await WritePngAsync(Path.Combine(root, "first.png"));
            string second = await WritePngAsync(Path.Combine(root, "second.png"));
            PathFinding firstFinding = NewBrokenFinding(first, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            PathFinding secondFinding = NewBrokenFinding(second, FileProbeStatus.Invalid, "ImageDecodeFailure", "PNG", "Image");
            using var cancellationSource = new CancellationTokenSource();
            var fileActions = new FakeFileActionService
            {
                NextDeleteCancellation = new DeleteOperationCanceledException(
                    new DeleteSummary(1, firstFinding.SizeBytes!.Value, [], [first]),
                    cancellationSource.Token),
            };
            var store = new AnalysisSessionStore();
            var viewModel = NewBrokenResultsViewModel(
                store,
                fileActions,
                BrokenProbe(FileProbeStatus.Invalid, "ImageDecodeFailure"));
            store.SetCompleted(
                ToolKind.BrokenFiles,
                new AnalysisScope(),
                new NoToolOptions(),
                NewResult([firstFinding, secondFinding]));
            foreach (PathFindingViewModel finding in viewModel.Findings)
            {
                finding.IsSelected = true;
            }

            await Assert.ThrowsAsync<DeleteOperationCanceledException>(() =>
                viewModel.DeleteSelectedAsync(CancellationToken.None));

            Assert.DoesNotContain(viewModel.Findings, finding => finding.FullPath == first);
            Assert.Contains(viewModel.Findings, finding => finding.FullPath == second && finding.IsSelected);
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

    private static AnalysisResultsViewModel NewBrokenResultsViewModel(
        AnalysisSessionStore store,
        IFileActionService fileActions,
        IFileFormatProbe probe) => new(
            store,
            fileActions,
            null,
            FileSignatureDetector.DetectFileAsync,
            probe);

    private static FakeFileFormatProbe BrokenProbe(FileProbeStatus status, string? errorType) => new()
    {
        Handler = (_, _, _) => Task.FromResult(new FileProbeResult(status, errorType, null)),
    };

    private static PathFinding NewBrokenFinding(
        string path,
        FileProbeStatus status,
        string errorType,
        string? detectedType,
        string validator)
    {
        var file = new FileInfo(path);
        var metadata = new Dictionary<string, string>
        {
            ["Validator"] = validator,
            ["ErrorType"] = errorType,
        };
        if (detectedType is not null)
        {
            metadata["DetectedType"] = detectedType;
        }

        return new PathFinding
        {
            FullPath = path,
            Kind = PathFindingKind.File,
            Reason = status == FileProbeStatus.Invalid
                ? "Unreadable or malformed file."
                : "Unsupported or protected.",
            SizeBytes = file.Length,
            CreatedUtc = file.CreationTimeUtc,
            ModifiedUtc = file.LastWriteTimeUtc,
            Metadata = metadata,
        };
    }

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

    private static PathFinding NewBadNameFinding(string path)
    {
        var file = new FileInfo(path);
        return new PathFinding
        {
            FullPath = path,
            Kind = PathFindingKind.File,
            Reason = "Has leading or trailing whitespace",
            Suggestion = Path.GetFileName(path).Trim(),
            SizeBytes = file.Length,
            CreatedUtc = file.CreationTimeUtc,
            ModifiedUtc = file.LastWriteTimeUtc,
            Metadata = new Dictionary<string, string>
            {
                ["CurrentName"] = Path.GetFileName(path),
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
            Evidence = new ImageSimilarityEvidence(0, 100, 100, "JPEG"),
        };

    private static SimilarityGroup NewVideoGroup(params SimilarityItem[] items) => Assert.Single(
        new SimilarVideoAnalyzer(new FakeVideoSampleProvider()).Regroup(
            items,
            new SimilarVideoOptions(9)));

    private static SimilarityItem WriteVideoItem(
        string root,
        string name,
        ulong hash,
        int width = 100,
        int height = 100,
        uint bitrate = 100)
    {
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        DateTime modifiedUtc = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return NewVideoItem(path, hash, width, height, bitrate, new FileInfo(path).Length, modifiedUtc);
    }

    private static SimilarityItem NewVideoItem(
        string path,
        ulong hash,
        int width = 100,
        int height = 100,
        uint bitrate = 100,
        long size = 10,
        DateTime? modifiedUtc = null) => new()
        {
            FullPath = path,
            SizeBytes = size,
            ModifiedUtc = modifiedUtc ?? new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc),
            SimilarityPercent = 0,
            Evidence = new VideoSimilarityEvidence(
                hash,
                hash,
                hash,
                hash,
                hash,
                width,
                height,
                (double)width / height,
                TimeSpan.FromSeconds(10),
                bitrate,
                30,
                "H264"),
        };

    private static ulong LowBits(int count) => (1UL << count) - 1;

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
            CancellationToken cancellationToken,
            DeletionMode? deletionMode = null) => throw new NotSupportedException();

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

    private sealed class VideoSimilarityAnalysisService : IAnalysisService
    {
        public Func<SimilarityItem, Task<bool>> Revalidate { get; init; } = _ => Task.FromResult(true);

        public List<string> RevalidatedPaths { get; } = [];

        public Task<AnalysisResult> RunAsync(
            ToolKind tool,
            AnalysisScope scope,
            ToolOptions toolOptions,
            IProgress<AnalysisProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RevalidateSimilarityItemAsync(
            ToolKind tool,
            SimilarityItem item,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ToolKind.SimilarVideos, tool);
            RevalidatedPaths.Add(item.FullPath);
            return Revalidate(item);
        }

        public IReadOnlyList<SimilarityGroup> RegroupSimilarityItems(
            ToolKind tool,
            ToolOptions options,
            IReadOnlyList<SimilarityItem> items)
        {
            Assert.Equal(ToolKind.SimilarVideos, tool);
            var videoOptions = Assert.IsType<SimilarVideoToolOptions>(options);
            return new SimilarVideoAnalyzer(new FakeVideoSampleProvider()).Regroup(
                items,
                new SimilarVideoOptions(videoOptions.MaximumMeanFrameDistance));
        }
    }

    private sealed class RecordingPreviewLoader : IMediaPreviewLoader
    {
        public List<(ToolKind Tool, SimilarityItem Item)> Calls { get; } = [];

        public Task<MediaPreviewData> LoadAsync(
            ToolKind tool,
            SimilarityItem item,
            CancellationToken cancellationToken)
        {
            Calls.Add((tool, item));
            return Task.FromResult(new MediaPreviewData(1, 1, [1, 1, 1, 255]));
        }
    }
}
