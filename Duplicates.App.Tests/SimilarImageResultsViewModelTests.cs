using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;

namespace Duplicates.App.Tests;

public sealed class SimilarImageResultsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates.App.Tests",
        Guid.NewGuid().ToString("N"));

    public SimilarImageResultsViewModelTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ManualReview_AllowsReferenceAndAllMemberDeleteWithFreshValidationAndExactTargets()
    {
        SimilarityItem reference = WriteItem("reference.jpg", 0, width: 200, height: 200, size: 3);
        SimilarityItem candidate = WriteItem("candidate.png", 0, width: 100, height: 100, size: 2, format: "PNG");
        SimilarityGroup initial = Assert.Single(Regroup([candidate, reference]));
        var analysis = new FakeSimilarityAnalysisService();
        var fileActions = new FakeFileActionService();
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, fileActions, analysis);
        store.SetCompleted(
            ToolKind.SimilarImages,
            new AnalysisScope { IncludedFolders = [_root] },
            new SimilarImageToolOptions(8),
            Result([initial]));

        SimilarityGroupViewModel group = Assert.Single(viewModel.Groups);
        Assert.Equal("Reference: reference.jpg, 1 candidate", group.SummaryText);
        Assert.Equal("200 × 200, JPEG", group.ReferenceItem.MediaDetailsText);
        Assert.All(group.Items, static item => Assert.False(item.IsSelected));
        group.ReferenceItem.IsSelected = true;
        Assert.True(viewModel.CanActOnSelection);
        Assert.False(viewModel.CanRenameSelection);

        IReadOnlyList<FileActionTarget>? firstTargets = null;
        fileActions.OnDelete = (targets, _) => firstTargets = targets;
        fileActions.NextSummary = new DeleteSummary(1, reference.SizeBytes, [], [reference.FullPath]);

        await viewModel.DeleteSelectedAsync(CancellationToken.None);

        FileActionTarget firstTarget = Assert.Single(firstTargets!);
        Assert.Equal(reference.FullPath, firstTarget.FullPath);
        Assert.Equal(reference.SizeBytes, firstTarget.SizeBytes);
        Assert.Equal(FileActionTargetKind.File, firstTarget.Kind);
        Assert.Equal(reference.ModifiedUtc, firstTarget.ExpectedModifiedUtc);
        Assert.Empty(viewModel.Groups);

        store.SetCompleted(
            ToolKind.SimilarImages,
            new AnalysisScope { IncludedFolders = [_root] },
            new SimilarImageToolOptions(8),
            Result([initial]));
        foreach (SimilarityItemViewModel item in viewModel.Groups[0].Items)
        {
            item.IsSelected = true;
        }

        IReadOnlyList<FileActionTarget>? allTargets = null;
        fileActions.OnDelete = (targets, _) => allTargets = targets;
        fileActions.NextSummary = new DeleteSummary(
            2,
            reference.SizeBytes + candidate.SizeBytes,
            [],
            [reference.FullPath, candidate.FullPath]);

        await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Equal(2, allTargets!.Count);
        Assert.Equal(3, analysis.RevalidatedPaths.Count);
        Assert.Empty(viewModel.Groups);
    }

    [Fact]
    public async Task Move_IncludesHiddenCanonicalSelectionAndKeepsRenameFailClosed()
    {
        SimilarityItem reference = WriteItem("reference.jpg", 0, width: 200, height: 200);
        SimilarityItem candidate = WriteItem("candidate.jpg", 0, width: 100, height: 100);
        var analysis = new FakeSimilarityAnalysisService();
        var fileActions = new FakeFileActionService();
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, fileActions, analysis);
        store.SetCompleted(
            ToolKind.SimilarImages,
            new AnalysisScope(),
            new SimilarImageToolOptions(8),
            Result(Regroup([reference, candidate])));
        SimilarityItemViewModel selected = viewModel.Groups[0].Items.Single(item => item.FullPath == candidate.FullPath);
        selected.IsSelected = true;
        viewModel.SearchText = "no visible result has this text";
        Assert.Empty(viewModel.ResultItems);
        Assert.Equal(1, viewModel.SelectedItemCount);
        Assert.False(viewModel.CanRenameSelection);

        IReadOnlyList<FileActionTarget>? targets = null;
        fileActions.OnMove = (mapped, _, _, _) => targets = mapped;
        fileActions.NextMoveSummary = new FileOperationSummary(
            [new FileOperationResult(candidate.FullPath, Path.Combine(_root, "moved.jpg"), null)],
            candidate.SizeBytes);

        await viewModel.MoveSelectedAsync(_root, MoveCollisionBehavior.Skip, CancellationToken.None);

        Assert.Equal(candidate.FullPath, Assert.Single(targets!).FullPath);
        Assert.Empty(viewModel.Groups);
        Assert.Equal("no visible result has this text", viewModel.SearchText);
    }

    [Fact]
    public async Task Delete_PreSnapshotChangeSkipsFreshProviderAndEvidenceMismatchStaysSelected()
    {
        SimilarityItem reference = WriteItem("reference.jpg", 0, width: 200, height: 200);
        SimilarityItem candidate = WriteItem("candidate.jpg", 0, width: 100, height: 100);
        var analysis = new FakeSimilarityAnalysisService();
        var fileActions = new FakeFileActionService();
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, fileActions, analysis);
        store.SetCompleted(ToolKind.SimilarImages, new AnalysisScope(), new SimilarImageToolOptions(8), Result(Regroup([reference, candidate])));
        SimilarityItemViewModel selected = viewModel.Groups[0].Items.Single(item => item.FullPath == candidate.FullPath);
        selected.IsSelected = true;
        await File.AppendAllBytesAsync(candidate.FullPath, [9]);

        DeleteSummary changed = await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Equal("File changed since scan.", Assert.Single(changed.Failures).Reason);
        Assert.Empty(analysis.RevalidatedPaths);
        Assert.Equal(0, fileActions.DeleteCallCount);
        Assert.True(selected.IsSelected);

        candidate = SnapshotItem(candidate.FullPath, 0, 100, 100);
        store.SetCompleted(ToolKind.SimilarImages, new AnalysisScope(), new SimilarImageToolOptions(8), Result(Regroup([reference, candidate])));
        selected = viewModel.Groups[0].Items.Single(item => item.FullPath == candidate.FullPath);
        selected.IsSelected = true;
        analysis.RevalidateHandler = (_, _, _) => Task.FromResult(false);

        DeleteSummary mismatch = await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Equal("File changed since scan.", Assert.Single(mismatch.Failures).Reason);
        Assert.Equal(0, fileActions.DeleteCallCount);
        Assert.True(selected.IsSelected);
    }

    [Theory]
    [InlineData(false, "Could not decode image.")]
    [InlineData(true, "File changed since scan.")]
    public async Task Delete_PostProviderSnapshotWinsOverUnchangedDecodeFailure(bool mutateDuringProvider, string expectedReason)
    {
        SimilarityItem reference = WriteItem("reference.jpg", 0, width: 200, height: 200);
        SimilarityItem candidate = WriteItem("candidate.jpg", 0, width: 100, height: 100);
        var analysis = new FakeSimilarityAnalysisService();
        var fileActions = new FakeFileActionService();
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, fileActions, analysis);
        store.SetCompleted(ToolKind.SimilarImages, new AnalysisScope(), new SimilarImageToolOptions(8), Result(Regroup([reference, candidate])));
        SimilarityItemViewModel selected = viewModel.Groups[0].Items.Single(item => item.FullPath == candidate.FullPath);
        selected.IsSelected = true;
        analysis.RevalidateHandler = (_, item, _) =>
        {
            Assert.True(viewModel.IsActionRunning);
            if (mutateDuringProvider)
            {
                File.AppendAllBytes(item.FullPath, [7]);
            }

            return Task.FromException<bool>(new IOException("decode"));
        };

        DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Equal(expectedReason, Assert.Single(summary.Failures).Reason);
        Assert.Equal(0, fileActions.DeleteCallCount);
        Assert.False(viewModel.IsActionRunning);
    }

    [Fact]
    public async Task Delete_CancellationAndSessionSwapAbortBeforeDispatchAndPreserveReplacementSession()
    {
        SimilarityItem oldReference = WriteItem("old-reference.jpg", 0, width: 200, height: 200);
        SimilarityItem oldCandidate = WriteItem("old-candidate.jpg", 0, width: 100, height: 100);
        SimilarityItem replacementReference = WriteItem("replacement-reference.jpg", ulong.MaxValue, width: 200, height: 200);
        SimilarityItem replacementCandidate = WriteItem("replacement-candidate.jpg", ulong.MaxValue, width: 100, height: 100);
        var analysis = new FakeSimilarityAnalysisService();
        var fileActions = new FakeFileActionService();
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, fileActions, analysis);
        store.SetCompleted(ToolKind.SimilarImages, new AnalysisScope(), new SimilarImageToolOptions(8), Result(Regroup([oldReference, oldCandidate])));
        viewModel.Groups[0].Items[1].IsSelected = true;
        using var cancellation = new CancellationTokenSource();
        analysis.RevalidateHandler = (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(true);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            viewModel.DeleteSelectedAsync(cancellation.Token));
        Assert.Equal(0, fileActions.DeleteCallCount);
        Assert.False(viewModel.IsActionRunning);

        store.SetCompleted(ToolKind.SimilarImages, new AnalysisScope(), new SimilarImageToolOptions(8), Result(Regroup([oldReference, oldCandidate])));
        viewModel.Groups[0].Items[1].IsSelected = true;
        AnalysisResult replacement = Result(Regroup([replacementReference, replacementCandidate]));
        analysis.RevalidateHandler = (_, _, _) =>
        {
            store.SetCompleted(ToolKind.SimilarImages, new AnalysisScope(), new SimilarImageToolOptions(8), replacement);
            return Task.FromResult(true);
        };

        DeleteSummary swapped = await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Equal("File changed since scan.", Assert.Single(swapped.Failures).Reason);
        Assert.Equal(0, fileActions.DeleteCallCount);
        Assert.Equal(replacementReference.FullPath, Assert.Single(viewModel.Groups).ReferenceItem.FullPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimilarityAction_SessionSwapAfterSelectionValidationRejectsWholeBatchBeforeDispatch(bool move)
    {
        SimilarityItem oldReference = WriteItem("old-reference-race.jpg", 0, width: 200, height: 200);
        SimilarityItem oldCandidate = WriteItem("old-candidate-race.jpg", 0, width: 100, height: 100);
        SimilarityItem replacementReference = WriteItem(
            "replacement-reference-race.jpg",
            ulong.MaxValue,
            width: 200,
            height: 200);
        SimilarityItem replacementCandidate = WriteItem(
            "replacement-candidate-race.jpg",
            ulong.MaxValue,
            width: 100,
            height: 100);
        var analysis = new FakeSimilarityAnalysisService();
        var fileActions = new FakeFileActionService();
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, fileActions, analysis);
        store.SetCompleted(
            ToolKind.SimilarImages,
            new AnalysisScope(),
            new SimilarImageToolOptions(8),
            Result(Regroup([oldReference, oldCandidate])));
        foreach (SimilarityItemViewModel item in viewModel.Groups[0].Items)
        {
            item.IsSelected = true;
        }

        AnalysisResult replacement = Result(Regroup([replacementReference, replacementCandidate]));
        viewModel.SimilaritySelectionValidated = () => store.SetCompleted(
            ToolKind.SimilarImages,
            new AnalysisScope(),
            new SimilarImageToolOptions(8),
            replacement);

        IReadOnlyList<FileActionFailure> failures;
        if (move)
        {
            FileOperationSummary summary = await viewModel.MoveSelectedAsync(
                _root,
                MoveCollisionBehavior.Skip,
                CancellationToken.None);
            failures = summary.Results.Select(static result => result.Failure!).ToArray();
        }
        else
        {
            DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);
            failures = summary.Failures;
        }

        Assert.Equal(0, fileActions.DeleteCallCount);
        Assert.Equal(0, fileActions.MoveCallCount);
        Assert.Equal(2, failures.Count);
        Assert.All(failures, static failure => Assert.Equal("File changed since scan.", failure.Reason));
        Assert.Equal(
            [oldReference.FullPath, oldCandidate.FullPath],
            failures.Select(static failure => failure.Path));
        Assert.Same(replacement, store.CurrentSession!.Result);
        SimilarityGroupViewModel replacementGroup = Assert.Single(viewModel.Groups);
        Assert.Equal(replacementReference.FullPath, replacementGroup.ReferenceItem.FullPath);
        Assert.All(replacementGroup.Items, static item => Assert.False(item.IsSelected));
    }

    [Fact]
    public async Task PartialMove_GloballyRegroupsAndPreservesFailuresPreviewSkippedFilterSortAndExport()
    {
        SimilarityItem firstReference = WriteItem("first-reference.jpg", 0, width: 300, height: 300);
        SimilarityItem bridge = WriteItem("first-bridge.jpg", LowBits(8), width: 200, height: 200);
        SimilarityItem tail = WriteItem("first-tail.jpg", LowBits(16), width: 100, height: 100);
        SimilarityItem secondReference = WriteItem("second-reference.jpg", 0xFFFF000000000000, width: 300, height: 300);
        SimilarityItem secondCandidate = WriteItem("second-candidate.jpg", 0xFFFF000000000001, width: 100, height: 100);
        var skipped = new SkippedPath { Path = Path.Combine(_root, "locked.jpg"), Reason = "Access denied" };
        var analysis = new FakeSimilarityAnalysisService();
        var fileActions = new FakeFileActionService();
        var exporter = new FakeResultExportService();
        var previewLoader = new FakeMediaPreviewLoader();
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, fileActions, analysis, exporter, previewLoader);
        IReadOnlyList<SimilarityGroup> initialGroups = Regroup(
            [tail, secondCandidate, bridge, firstReference, secondReference]);
        store.SetCompleted(
            ToolKind.SimilarImages,
            new AnalysisScope { IncludedFolders = [_root] },
            new SimilarImageToolOptions(8),
            Result(initialGroups, [skipped]));
        SimilarityItemViewModel bridgeViewModel = viewModel.Groups.SelectMany(static group => group.Items)
            .Single(item => item.FullPath == bridge.FullPath);
        SimilarityItemViewModel failedViewModel = viewModel.Groups.SelectMany(static group => group.Items)
            .Single(item => item.FullPath == secondReference.FullPath);
        bridgeViewModel.IsSelected = true;
        failedViewModel.IsSelected = true;
        await viewModel.SelectSimilarityPreviewItemCommand.ExecuteAsync(failedViewModel);
        viewModel.SearchText = "second-";
        viewModel.SelectedSortIndex = 2;
        fileActions.NextMoveSummary = new FileOperationSummary(
            [
                new FileOperationResult(bridge.FullPath, Path.Combine(_root, "moved.jpg"), null),
                new FileOperationResult(secondReference.FullPath, null, new FileActionFailure(secondReference.FullPath, "Move failed.")),
            ],
            bridge.SizeBytes);

        FileOperationSummary summary = await viewModel.MoveSelectedAsync(
            Path.Combine(_root, "destination"),
            MoveCollisionBehavior.Skip,
            CancellationToken.None);

        Assert.Equal(1, summary.Results.Count(static result => result.Succeeded));
        SimilarityGroupViewModel remaining = Assert.Single(viewModel.Groups);
        Assert.Equal("image-0001", remaining.Id);
        Assert.Equal(secondReference.FullPath, remaining.ReferenceItem.FullPath);
        Assert.Equal("Reference: second-reference.jpg, 1 candidate", remaining.SummaryText);
        Assert.Equal("0", remaining.ReferenceItem.Source.Metadata["HammingDistance"]);
        Assert.True(remaining.ReferenceItem.IsSelected);
        Assert.Equal(secondReference.FullPath, viewModel.SelectedSimilarityPreviewItem?.FullPath);
        Assert.Equal(secondReference.FullPath, viewModel.PreviewPath);
        Assert.Equal("second-", viewModel.SearchText);
        Assert.Equal(2, viewModel.SelectedSortIndex);
        Assert.Single(viewModel.ResultItems);
        Assert.Contains("Access denied", viewModel.SkippedPathsDetailsText, StringComparison.Ordinal);

        await viewModel.ExportAsync(ResultExportFormat.Json, Path.Combine(_root, "results.json"), CancellationToken.None);

        Assert.Equal(2, exporter.Snapshot!.Items.Count);
        Assert.Single(exporter.Snapshot.SkippedPaths);
        Assert.All(exporter.Snapshot.Items, item =>
        {
            Assert.False(item.Metadata.ContainsKey("PerceptualHash"));
            Assert.DoesNotContain(item.Metadata.Values, value => value.Contains("Bgra", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Preview_GroupDefaultsToReferenceAndFailureKeepsSelectionWithUnavailableState()
    {
        SimilarityItem reference = WriteItem("reference.jpg", 0, width: 200, height: 200);
        SimilarityItem candidate = WriteItem("candidate.jpg", 0, width: 100, height: 100);
        var loader = new FakeMediaPreviewLoader();
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, new FakeFileActionService(), new FakeSimilarityAnalysisService(), previewLoader: loader);
        store.SetCompleted(ToolKind.SimilarImages, new AnalysisScope(), new SimilarImageToolOptions(8), Result(Regroup([reference, candidate])));
        SimilarityGroupViewModel group = viewModel.Groups[0];

        viewModel.SelectedResult = group;
        Assert.True(SpinWait.SpinUntil(() => viewModel.SimilarityPreview is not null, TimeSpan.FromSeconds(1)));
        Assert.Equal(group.ReferenceItem.FullPath, viewModel.SelectedSimilarityPreviewItem?.FullPath);
        Assert.Equal(group.ReferenceItem.FullPath, viewModel.PreviewPath);
        Assert.Equal(Visibility.Visible, viewModel.SimilarityPreviewVisibility);

        loader.Handler = (_, _, _) => Task.FromException<MediaPreviewData>(new IOException("preview"));
        SimilarityItemViewModel failed = group.Items.Single(item => !item.IsReference);
        await viewModel.SelectSimilarityPreviewItemCommand.ExecuteAsync(failed);

        Assert.Same(failed, viewModel.SelectedSimilarityPreviewItem);
        Assert.Null(viewModel.SimilarityPreview);
        Assert.Equal("Preview unavailable", viewModel.SimilarityPreviewStatusText);
        Assert.Equal(Visibility.Visible, viewModel.SimilarityPreviewStatusVisibility);
        Assert.False(failed.IsSelected);
    }

    [Fact]
    public async Task Preview_IgnoresStaleCompletionAfterItemChanges()
    {
        SimilarityItem reference = WriteItem("reference.jpg", 0, width: 200, height: 200);
        SimilarityItem candidate = WriteItem("candidate.jpg", 0, width: 100, height: 100);
        var firstCompletion = new TaskCompletionSource<MediaPreviewData>(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPreviewData secondPreview = Preview(2);
        var loader = new FakeMediaPreviewLoader
        {
            Handler = (_, item, _) => item.FullPath == reference.FullPath
                ? firstCompletion.Task
                : Task.FromResult(secondPreview),
        };
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, new FakeFileActionService(), new FakeSimilarityAnalysisService(), previewLoader: loader);
        store.SetCompleted(ToolKind.SimilarImages, new AnalysisScope(), new SimilarImageToolOptions(8), Result(Regroup([reference, candidate])));
        SimilarityGroupViewModel group = viewModel.Groups[0];

        Task firstLoad = viewModel.SelectSimilarityPreviewItemCommand.ExecuteAsync(group.ReferenceItem);
        Assert.True(SpinWait.SpinUntil(() => loader.Calls.Count == 1, TimeSpan.FromSeconds(1)));
        SimilarityItemViewModel secondItem = group.Items.Single(item => !item.IsReference);
        Task secondLoad = viewModel.SelectSimilarityPreviewItemCommand.ExecuteAsync(secondItem);
        await secondLoad;
        firstCompletion.SetResult(Preview(1));
        await firstLoad;

        Assert.Same(secondItem, viewModel.SelectedSimilarityPreviewItem);
        Assert.Same(secondPreview, viewModel.SimilarityPreview);
        Assert.Equal(2, viewModel.SimilarityPreview!.Bgra8[0]);
    }

    private AnalysisResultsViewModel NewViewModel(
        AnalysisSessionStore store,
        IFileActionService fileActions,
        IAnalysisService analysisService,
        IResultExportService? exporter = null,
        IMediaPreviewLoader? previewLoader = null) => new(
            store,
            fileActions,
            exporter,
            analysisService,
            previewLoader ?? new FakeMediaPreviewLoader());

    private SimilarityItem WriteItem(
        string name,
        ulong hash,
        int width,
        int height,
        int size = 1,
        string format = "JPEG")
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(static value => (byte)value).ToArray());
        DateTime modifiedUtc = new(2026, 8, 8, 11, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return SnapshotItem(path, hash, width, height, format);
    }

    private static SimilarityItem SnapshotItem(
        string path,
        ulong hash,
        int width,
        int height,
        string format = "JPEG")
    {
        var file = new FileInfo(path);
        file.Refresh();
        return new SimilarityItem
        {
            FullPath = path,
            SizeBytes = file.Length,
            ModifiedUtc = file.LastWriteTimeUtc,
            SimilarityPercent = 0,
            Evidence = new ImageSimilarityEvidence(hash, width, height, format),
        };
    }

    private static IReadOnlyList<SimilarityGroup> Regroup(IReadOnlyList<SimilarityItem> items) =>
        new SimilarImageAnalyzer(new FakeImageSampleProvider()).Regroup(items, new SimilarImageOptions(8));

    private static AnalysisResult Result(
        IReadOnlyList<SimilarityGroup> groups,
        IReadOnlyList<SkippedPath>? skipped = null) => new()
        {
            Findings = [],
            Groups = groups,
            SkippedPaths = skipped ?? [],
            Elapsed = TimeSpan.Zero,
        };

    private static ulong LowBits(int count) => (1UL << count) - 1;

    private static MediaPreviewData Preview(byte value) => new(1, 1, [value, value, value, 255]);

    private sealed class FakeSimilarityAnalysisService : IAnalysisService
    {
        public Func<ToolKind, SimilarityItem, CancellationToken, Task<bool>> RevalidateHandler { get; set; } =
            (_, _, _) => Task.FromResult(true);

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
            RevalidatedPaths.Add(item.FullPath);
            return RevalidateHandler(tool, item, cancellationToken);
        }

        public IReadOnlyList<SimilarityGroup> RegroupSimilarityItems(
            ToolKind tool,
            ToolOptions options,
            IReadOnlyList<SimilarityItem> items)
        {
            if (tool != ToolKind.SimilarImages || options is not SimilarImageToolOptions imageOptions)
            {
                throw new NotSupportedException();
            }

            return new SimilarImageAnalyzer(new FakeImageSampleProvider()).Regroup(
                items,
                new SimilarImageOptions(imageOptions.MaximumHammingDistance));
        }
    }

    private sealed class FakeMediaPreviewLoader : IMediaPreviewLoader
    {
        public Func<ToolKind, SimilarityItem, CancellationToken, Task<MediaPreviewData>> Handler { get; set; } =
            (_, _, _) => Task.FromResult(Preview(1));

        public List<(ToolKind Tool, SimilarityItem Item)> Calls { get; } = [];

        public Task<MediaPreviewData> LoadAsync(
            ToolKind tool,
            SimilarityItem item,
            CancellationToken cancellationToken)
        {
            Calls.Add((tool, item));
            return Handler(tool, item, cancellationToken);
        }
    }
}
