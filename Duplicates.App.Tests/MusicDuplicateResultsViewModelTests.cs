using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;

namespace Duplicates.App.Tests;

public sealed partial class MusicDuplicateResultsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates.App.Tests",
        Guid.NewGuid().ToString("N"));

    public MusicDuplicateResultsViewModelTests()
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
    public async Task ManualReview_AllowsReferenceAndAllMemberDeleteWithoutAutomaticSelectionOrRename()
    {
        SimilarityItem reference = WriteItem("reference.mp3", durationSeconds: 100, bitrate: 320_000, size: 3);
        SimilarityItem candidate = WriteItem("candidate.wma", durationSeconds: 101, bitrate: 128_000, size: 2);
        SimilarityGroup initial = Assert.Single(Regroup([candidate, reference]));
        var analysis = new FakeMusicAnalysisService();
        var fileActions = new FakeFileActionService();
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, fileActions, analysis);
        store.SetCompleted(
            ToolKind.MusicDuplicates,
            new AnalysisScope { IncludedFolders = [_root] },
            Options(),
            Result([initial]));

        SimilarityGroupViewModel group = Assert.Single(viewModel.Groups);
        Assert.Equal("2 matching tracks", group.DisplayName);
        Assert.Equal("High confidence, reference: reference.mp3, 1 candidate", group.SummaryText);
        Assert.Equal("High confidence", group.ReferenceItem.SimilarityText);
        Assert.DoesNotContain("100% similar", group.ReferenceItem.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("High confidence, Song — Artist, 00:01:40, 320000 bps", group.ReferenceItem.MediaDetailsText);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Confidence: High",
                "Title: Song",
                "Artist: Artist",
                "AlbumArtist: Album Artist",
                "Album: Album",
                "TrackNumber: 1",
                "Year: 2025",
                "Genres: Rock, Pop",
                "Bitrate: 320000",
                "Duration: 00:01:40",
                "DurationDifference: 00:00:00"),
            group.ReferenceItem.MetadataText);
        Assert.All(group.Items, static item => Assert.False(item.IsSelected));
        group.ReferenceItem.IsSelected = true;
        Assert.True(viewModel.CanActOnSelection);
        Assert.False(viewModel.CanRenameSelection);

        IReadOnlyList<FileActionTarget>? targets = null;
        fileActions.OnDelete = (mapped, _) => targets = mapped;
        fileActions.NextSummary = new DeleteSummary(1, reference.SizeBytes, [], [reference.FullPath]);

        await viewModel.DeleteSelectedAsync(CancellationToken.None);

        FileActionTarget target = Assert.Single(targets!);
        Assert.Equal(reference.FullPath, target.FullPath);
        Assert.Equal(reference.ModifiedUtc, target.ExpectedModifiedUtc);
        Assert.Empty(viewModel.Groups);

        store.SetCompleted(ToolKind.MusicDuplicates, new AnalysisScope(), Options(), Result([initial]));
        foreach (SimilarityItemViewModel item in viewModel.Groups[0].Items)
        {
            item.IsSelected = true;
        }

        fileActions.NextSummary = new DeleteSummary(
            2,
            reference.SizeBytes + candidate.SizeBytes,
            [],
            [reference.FullPath, candidate.FullPath]);
        await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Equal(3, analysis.RevalidatedPaths.Count);
        Assert.Empty(viewModel.Groups);
    }

    [Fact]
    public async Task Delete_UsesMusicThreeWayFailureMappingWithSnapshotAndEvidencePrecedence()
    {
        SimilarityItem reference = WriteItem("reference.mp3", durationSeconds: 100, bitrate: 320_000);
        SimilarityItem candidate = WriteItem("candidate.mp3", durationSeconds: 101, bitrate: 128_000);
        var analysis = new FakeMusicAnalysisService();
        var fileActions = new FakeFileActionService();
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, fileActions, analysis);
        store.SetCompleted(ToolKind.MusicDuplicates, new AnalysisScope(), Options(), Result(Regroup([reference, candidate])));
        SimilarityItemViewModel selected = viewModel.Groups[0].Items.Single(item => item.FullPath == candidate.FullPath);
        selected.IsSelected = true;
        File.AppendAllBytes(candidate.FullPath, [9]);

        DeleteSummary changedBefore = await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Equal("File changed since scan.", Assert.Single(changedBefore.Failures).Reason);
        Assert.Empty(analysis.RevalidatedPaths);
        Assert.True(selected.IsSelected);

        candidate = SnapshotItem(candidate.FullPath, durationSeconds: 101, bitrate: 128_000);
        store.SetCompleted(ToolKind.MusicDuplicates, new AnalysisScope(), Options(), Result(Regroup([reference, candidate])));
        selected = viewModel.Groups[0].Items.Single(item => item.FullPath == candidate.FullPath);
        selected.IsSelected = true;
        analysis.RevalidateHandler = (_, _, _) =>
            Task.FromException<bool>(new MissingRequiredMusicMetadataException());

        DeleteSummary missing = await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Equal("Required music metadata is missing.", Assert.Single(missing.Failures).Reason);
        Assert.True(viewModel.Groups[0].Items.Single(item => item.FullPath == candidate.FullPath).IsSelected);

        analysis.RevalidateHandler = (_, _, _) => Task.FromException<bool>(new InvalidDataException("duration"));
        DeleteSummary unreadable = await viewModel.DeleteSelectedAsync(CancellationToken.None);
        Assert.Equal("Could not read music metadata.", Assert.Single(unreadable.Failures).Reason);

        analysis.RevalidateHandler = (_, _, _) => Task.FromResult(false);
        DeleteSummary mismatch = await viewModel.DeleteSelectedAsync(CancellationToken.None);
        Assert.Equal("File changed since scan.", Assert.Single(mismatch.Failures).Reason);

        analysis.RevalidateHandler = (_, item, _) =>
        {
            File.AppendAllBytes(item.FullPath, [7]);
            return Task.FromException<bool>(new MissingRequiredMusicMetadataException());
        };
        DeleteSummary changedAfter = await viewModel.DeleteSelectedAsync(CancellationToken.None);
        Assert.Equal("File changed since scan.", Assert.Single(changedAfter.Failures).Reason);
        Assert.Equal(0, fileActions.DeleteCallCount);
    }

    [Fact]
    public async Task MusicDuplicates_MarkerDoesNotBroadenOtherSimilarityExpectedFailureBoundary()
    {
        SimilarityItem reference = WriteItem("reference.mp3", durationSeconds: 100, bitrate: 320_000);
        SimilarityItem candidate = WriteItem("candidate.mp3", durationSeconds: 101, bitrate: 128_000);
        var analysis = new FakeMusicAnalysisService
        {
            RevalidateHandler = (_, _, _) =>
                Task.FromException<bool>(new MissingRequiredMusicMetadataException()),
        };
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, new FakeFileActionService(), analysis);
        store.SetCompleted(
            ToolKind.SimilarImages,
            new AnalysisScope(),
            new SimilarImageToolOptions(8),
            Result(Regroup([reference, candidate])));
        viewModel.Groups[0].Items[1].IsSelected = true;

        await Assert.ThrowsAsync<MissingRequiredMusicMetadataException>(() =>
            viewModel.DeleteSelectedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PartialMove_GloballyReclustersChoosesNewReferenceAndPreservesStateAndSafeExport()
    {
        SimilarityItem first = WriteItem("first-100.mp3", 100, bitrate: 100_000, size: 2, album: "Needle Album");
        SimilarityItem oldReference = WriteItem("first-101.mp3", 101, bitrate: 320_000, size: 3, album: "Needle Album");
        SimilarityItem newReference = WriteItem("first-102.mp3", 102, bitrate: 256_000, size: 4, album: "Needle Album");
        SimilarityItem failedReference = WriteItem(
            "second-reference.mp3",
            200,
            bitrate: 320_000,
            size: 5,
            title: "Other",
            album: "Second Album");
        SimilarityItem failedCandidate = WriteItem(
            "second-candidate.mp3",
            201,
            bitrate: 128_000,
            size: 2,
            title: "Other",
            album: "Second Album");
        var skipped = new SkippedPath { Path = Path.Combine(_root, "locked.mp3"), Reason = "Access denied" };
        var analysis = new FakeMusicAnalysisService();
        var fileActions = new FakeFileActionService();
        var exporter = new FakeResultExportService();
        var previewLoader = new RecordingPreviewLoader();
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, fileActions, analysis, exporter, previewLoader);
        store.SetCompleted(
            ToolKind.MusicDuplicates,
            new AnalysisScope { IncludedFolders = [_root] },
            Options(),
            Result(Regroup([failedCandidate, newReference, oldReference, first, failedReference]), [skipped]));
        SimilarityItemViewModel moved = viewModel.Groups.SelectMany(static group => group.Items)
            .Single(item => item.FullPath == oldReference.FullPath);
        SimilarityItemViewModel failed = viewModel.Groups.SelectMany(static group => group.Items)
            .Single(item => item.FullPath == failedReference.FullPath);
        moved.IsSelected = true;
        failed.IsSelected = true;
        await viewModel.SelectSimilarityPreviewItemCommand.ExecuteAsync(failed);
        Assert.Empty(previewLoader.Calls);
        viewModel.SearchText = "Second Album";
        viewModel.SelectedSortIndex = 2;
        fileActions.NextMoveSummary = new FileOperationSummary(
            [
                new FileOperationResult(oldReference.FullPath, Path.Combine(_root, "moved.mp3"), null),
                new FileOperationResult(
                    failedReference.FullPath,
                    null,
                    new FileActionFailure(failedReference.FullPath, "Move failed.")),
            ],
            oldReference.SizeBytes);

        FileOperationSummary summary = await viewModel.MoveSelectedAsync(
            Path.Combine(_root, "destination"),
            MoveCollisionBehavior.Skip,
            CancellationToken.None);

        Assert.Equal(1, summary.Results.Count(static result => result.Succeeded));
        Assert.Equal("0 findings, 2 similarity groups", viewModel.SummaryText);
        Assert.Equal("Second Album", viewModel.SearchText);
        Assert.Single(viewModel.Groups);
        viewModel.SearchText = string.Empty;
        Assert.Equal(2, viewModel.Groups.Count);
        SimilarityGroupViewModel rebuiltFirst = viewModel.Groups.Single(group =>
            group.Items.Any(item => item.FullPath == first.FullPath));
        Assert.Equal(newReference.FullPath, rebuiltFirst.ReferenceItem.FullPath);
        Assert.Equal("00:00:00", rebuiltFirst.ReferenceItem.Source.Metadata["DurationDifference"]);
        SimilarityGroupViewModel rebuiltSecond = viewModel.Groups.Single(group =>
            group.Items.Any(item => item.FullPath == failedReference.FullPath));
        Assert.True(rebuiltSecond.Items.Single(item => item.FullPath == failedReference.FullPath).IsSelected);
        Assert.Equal(failedReference.FullPath, viewModel.SelectedSimilarityPreviewItem?.FullPath);
        viewModel.SearchText = "Second Album";
        Assert.Equal("Second Album", viewModel.SearchText);
        Assert.Equal(2, viewModel.SelectedSortIndex);
        Assert.Single(viewModel.ResultItems);
        Assert.Contains("Access denied", viewModel.SkippedPathsDetailsText, StringComparison.Ordinal);

        await viewModel.ExportAsync(ResultExportFormat.Json, Path.Combine(_root, "results.json"), CancellationToken.None);

        Assert.Equal(4, exporter.Snapshot!.Items.Count);
        Assert.Single(exporter.Snapshot.SkippedPaths);
        Assert.All(exporter.Snapshot.Items, item =>
        {
            Assert.Equal("Music metadata and duration match", item.Reason);
            Assert.Null(item.SimilarityPercent);
            Assert.False(item.Metadata.ContainsKey("NormalizedTitle"));
            Assert.False(item.Metadata.ContainsKey("NormalizedArtist"));
            Assert.Equal("High", item.Metadata["Confidence"]);
        });
    }

    [Fact]
    public async Task FailedItemDisappearsWhenAnotherSuccessLeavesItAsSingleton()
    {
        SimilarityItem reference = WriteItem("reference.mp3", 100, bitrate: 320_000);
        SimilarityItem failed = WriteItem("failed.mp3", 101, bitrate: 128_000);
        var analysis = new FakeMusicAnalysisService();
        var fileActions = new FakeFileActionService();
        var store = new AnalysisSessionStore();
        var viewModel = NewViewModel(store, fileActions, analysis);
        store.SetCompleted(ToolKind.MusicDuplicates, new AnalysisScope(), Options(), Result(Regroup([reference, failed])));
        foreach (SimilarityItemViewModel item in viewModel.Groups[0].Items)
        {
            item.IsSelected = true;
        }

        fileActions.NextSummary = new DeleteSummary(
            1,
            reference.SizeBytes,
            [new FileActionFailure(failed.FullPath, "Delete failed.")],
            [reference.FullPath]);

        DeleteSummary summary = await viewModel.DeleteSelectedAsync(CancellationToken.None);

        Assert.Equal("Delete failed.", Assert.Single(summary.Failures).Reason);
        Assert.Empty(viewModel.Groups);
        Assert.Equal(0, viewModel.SelectedItemCount);
    }

    private static AnalysisResultsViewModel NewViewModel(
        AnalysisSessionStore store,
        IFileActionService fileActions,
        IAnalysisService analysisService,
        IResultExportService? exporter = null,
        IMediaPreviewLoader? previewLoader = null) => new(
            store,
            fileActions,
            exporter,
            analysisService,
            previewLoader);

    private SimilarityItem WriteItem(
        string name,
        int durationSeconds,
        uint bitrate,
        int size = 1,
        string title = "Song",
        string artist = "Artist",
        string album = "Album")
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(static value => (byte)value).ToArray());
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 8, 11, 0, 0, DateTimeKind.Utc));
        return SnapshotItem(path, durationSeconds, bitrate, title, artist, album);
    }

    private static SimilarityItem SnapshotItem(
        string path,
        int durationSeconds,
        uint bitrate,
        string title = "Song",
        string artist = "Artist",
        string album = "Album")
    {
        var file = new FileInfo(path);
        file.Refresh();
        return new SimilarityItem
        {
            FullPath = path,
            SizeBytes = file.Length,
            ModifiedUtc = file.LastWriteTimeUtc,
            SimilarityPercent = 0,
            Evidence = new MusicSimilarityEvidence(
                title.ToLowerInvariant(),
                artist.ToLowerInvariant(),
                title,
                artist,
                "Album Artist",
                album,
                1,
                2025,
                ["Rock", "Pop"],
                bitrate,
                TimeSpan.FromSeconds(durationSeconds)),
        };
    }

    private static MusicDuplicateToolOptions Options() => new(TimeSpan.FromSeconds(2));

    private static IReadOnlyList<SimilarityGroup> Regroup(IReadOnlyList<SimilarityItem> items) =>
        MusicDuplicateAnalyzer.Regroup(
            items,
            new MusicDuplicateOptions(TimeSpan.FromSeconds(2)));

    private static AnalysisResult Result(
        IReadOnlyList<SimilarityGroup> groups,
        IReadOnlyList<SkippedPath>? skipped = null) => new()
        {
            Findings = [],
            Groups = groups,
            SkippedPaths = skipped ?? [],
            Elapsed = TimeSpan.Zero,
        };

    private sealed class FakeMusicAnalysisService : IAnalysisService
    {
        public Func<ToolKind, SimilarityItem, CancellationToken, Task<bool>> RevalidateHandler { get; set; } =
            (_, _, _) => Task.FromResult(true);

        public List<string> RevalidatedPaths { get; } = [];

        public Task<AnalysisResult> RunAsync(
            ToolKind tool,
            AnalysisScope scope,
            IToolOptions toolOptions,
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
            IToolOptions options,
            IReadOnlyList<SimilarityItem> items)
        {
            if (tool != ToolKind.MusicDuplicates || options is not MusicDuplicateToolOptions musicOptions)
            {
                throw new NotSupportedException();
            }

            return MusicDuplicateAnalyzer.Regroup(
                items,
                new MusicDuplicateOptions(musicOptions.MaximumDurationDifference));
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
            return Task.FromResult(new MediaPreviewData(1, 1, [0, 0, 0, 255]));
        }
    }
}
