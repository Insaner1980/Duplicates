using System.Runtime.InteropServices;
using System.Reflection;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Tests;

public sealed class SimilarVideoAnalyzerTests : IDisposable
{
    private static readonly DateTime FixedModifiedUtc = new(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates.Engine.Tests",
        Guid.NewGuid().ToString("N"));

    public SimilarVideoAnalyzerTests()
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonpositiveMaximumConcurrency(int maximumConcurrency)
    {
        TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() =>
            Activator.CreateInstance(
                typeof(SimilarVideoAnalyzer),
                new FakeVideoSampleProvider(),
                maximumConcurrency));

        var error = Assert.IsType<ArgumentOutOfRangeException>(failure.InnerException);
        Assert.Equal(nameof(maximumConcurrency), error.ParamName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task AnalyzeAsync_UsesConfiguredBoundedConcurrency(int maximumConcurrency)
    {
        InventoryFile[] files = Enumerable.Range(0, maximumConcurrency + 2)
            .Select(index => WriteInventoryFile($"bound-{index:00}.mp4", 1))
            .ToArray();
        var provider = new BlockingVideoSampleProvider(files.Select(static file => file.FullPath));
        Task<AnalysisResult> run = new SimilarVideoAnalyzer(provider, maximumConcurrency).AnalyzeAsync(
            NewInventory(files),
            new SimilarVideoOptions(9),
            CancellationToken.None);

        Exception? entryFailure = await Record.ExceptionAsync(() =>
            provider.WaitForEntriesAsync(maximumConcurrency).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        provider.CompleteAll(Sample());
        AnalysisResult result = await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Null(entryFailure);
        Assert.Equal(maximumConcurrency, provider.MaximumObserved);
        Assert.Empty(result.SkippedPaths);
    }

    [Fact]
    public async Task AnalyzeAsync_ParallelCancellationPropagatesInsteadOfBecomingSkips()
    {
        InventoryFile[] files = Enumerable.Range(0, 3)
            .Select(index => WriteInventoryFile($"cancel-parallel-{index:00}.mp4", 1))
            .ToArray();
        var provider = new BlockingVideoSampleProvider(files.Select(static file => file.FullPath));
        using var cancellation = new CancellationTokenSource();
        Task<AnalysisResult> run = new SimilarVideoAnalyzer(provider, 2).AnalyzeAsync(
            NewInventory(files),
            new SimilarVideoOptions(9),
            cancellation.Token);

        Exception? entryFailure = await Record.ExceptionAsync(() =>
            provider.WaitForEntriesAsync(2).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Null(entryFailure);
        Assert.Equal(2, provider.CancellationCount);
    }

    [Fact]
    public async Task AnalyzeAsync_ParallelCompletionOrderDoesNotChangeSkipOrder()
    {
        InventoryFile[] files = Enumerable.Range(0, 4)
            .Select(index => WriteInventoryFile($"ordered-{index:00}.mp4", 1))
            .Reverse()
            .ToArray();
        var provider = new BlockingVideoSampleProvider(files.Select(static file => file.FullPath));
        Task<AnalysisResult> run = new SimilarVideoAnalyzer(provider, 4).AnalyzeAsync(
            NewInventory(files),
            new SimilarVideoOptions(9),
            CancellationToken.None);

        Exception? entryFailure = await Record.ExceptionAsync(() =>
            provider.WaitForEntriesAsync(4).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        foreach (InventoryFile file in files.OrderByDescending(static file => file.FullPath, StringComparer.Ordinal))
        {
            provider.Complete(file.FullPath, InvalidSample("width"));
        }

        AnalysisResult result = await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Null(entryFailure);
        Assert.Equal(
            files.Select(static file => file.FullPath).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase),
            result.SkippedPaths.Select(static skip => skip.Path));
    }

    [Fact]
    public async Task AnalyzeAsync_GroupsReencodedVideoAndUsesStableVideoInventoryOrder()
    {
        InventoryFile source = WriteInventoryFile("source.mp4", 30);
        InventoryFile transcode = WriteInventoryFile("transcode.mkv", 20);
        InventoryFile unrelated = WriteInventoryFile("unrelated.webm", 10);
        var provider = new FakeVideoSampleProvider();
        provider.Cached[source.FullPath] = (_, _) => Task.FromResult(Sample(
            frames: Frames(Filled(90)),
            width: 1280,
            height: 720,
            bitrate: 2_000_000));
        provider.Cached[transcode.FullPath] = (_, _) => Task.FromResult(Sample(
            frames: Frames(Filled(90)),
            width: 1920,
            height: 1080,
            bitrate: 1_000_000));
        provider.Cached[unrelated.FullPath] = (_, _) => Task.FromResult(Sample(
            frames: Frames(Checkerboard()),
            width: 640,
            height: 360));

        AnalysisResult result = await new SimilarVideoAnalyzer(provider).AnalyzeAsync(
            NewInventory([unrelated, transcode, source]),
            new SimilarVideoOptions(9),
            CancellationToken.None);

        SimilarityGroup group = Assert.Single(result.Groups);
        Assert.Equal("video-0001", group.Id);
        Assert.Equal(transcode.FullPath, group.ReferenceItem.FullPath);
        Assert.Same(group.Items[0], group.ReferenceItem);
        Assert.Equal([transcode.FullPath, source.FullPath], group.Items.Select(static item => item.FullPath));
        Assert.Empty(result.Findings);
        Assert.Empty(result.SkippedPaths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.Equal([source.FullPath, transcode.FullPath, unrelated.FullPath], provider.CachedPaths);
    }

    [Fact]
    public async Task AnalyzeAsync_FiltersOrdinaryVideoFilesAndPreservesInventorySkipsFirst()
    {
        InventoryFile video = WriteInventoryFile("clip.MP4", 4);
        InventoryFile image = WriteInventoryFile("photo.png", 4);
        InventoryFile linked = NewInventoryFile(
            Path.Combine(_root, "linked.mkv"),
            4,
            FileAttributes.ReparsePoint);
        InventoryFile directory = NewInventoryFile(
            Path.Combine(_root, "folder.avi"),
            0,
            FileAttributes.Directory);
        var provider = new FakeVideoSampleProvider();
        provider.Cached[video.FullPath] = (_, _) => Task.FromException<VideoSample>(new IOException("decode"));
        var inventorySkip = new SkippedPath { Path = "inventory", Reason = "Existing" };

        AnalysisResult result = await new SimilarVideoAnalyzer(provider).AnalyzeAsync(
            NewInventory([image, linked, directory, video], [inventorySkip]),
            new SimilarVideoOptions(9),
            CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Equal(2, result.SkippedPaths.Count);
        Assert.Same(inventorySkip, result.SkippedPaths[0]);
        Assert.Equal(video.FullPath, result.SkippedPaths[1].Path);
        Assert.Equal("Could not decode video.", result.SkippedPaths[1].Reason);
        Assert.Equal([video.FullPath], provider.CachedPaths);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("duration")]
    [InlineData("aspect-zero")]
    [InlineData("aspect-negative")]
    [InlineData("aspect-nan")]
    [InlineData("aspect-positive-infinity")]
    [InlineData("aspect-negative-infinity")]
    [InlineData("fps-zero")]
    [InlineData("fps-negative")]
    [InlineData("fps-nan")]
    [InlineData("fps-positive-infinity")]
    [InlineData("fps-negative-infinity")]
    [InlineData("codec-empty")]
    [InlineData("codec-whitespace")]
    [InlineData("four-frames")]
    [InlineData("null-frame")]
    [InlineData("short-frame")]
    [InlineData("aliased-frames")]
    public async Task AnalyzeAsync_SkipsEveryStructurallyInvalidVideoSample(string invalidPart)
    {
        InventoryFile file = WriteInventoryFile($"{invalidPart}.mp4", 1);
        var provider = new FakeVideoSampleProvider();
        provider.Cached[file.FullPath] = (_, _) => Task.FromResult(InvalidSample(invalidPart));

        AnalysisResult result = await new SimilarVideoAnalyzer(provider).AnalyzeAsync(
            NewInventory([file]),
            new SimilarVideoOptions(9),
            CancellationToken.None);

        SkippedPath skip = Assert.Single(result.SkippedPaths);
        Assert.Equal(file.FullPath, skip.Path);
        Assert.Equal("Could not decode video.", skip.Reason);
    }

    [Fact]
    public async Task AnalyzeAsync_AcceptsZeroBitrateAsMetadata()
    {
        InventoryFile first = WriteInventoryFile("zero-a.mp4", 2);
        InventoryFile second = WriteInventoryFile("zero-b.mp4", 1);
        var provider = new FakeVideoSampleProvider();
        provider.Cached[first.FullPath] = (_, _) => Task.FromResult(Sample(bitrate: 0));
        provider.Cached[second.FullPath] = (_, _) => Task.FromResult(Sample(bitrate: 0));

        AnalysisResult result = await new SimilarVideoAnalyzer(provider).AnalyzeAsync(
            NewInventory([second, first]),
            new SimilarVideoOptions(9),
            CancellationToken.None);

        SimilarityGroup group = Assert.Single(result.Groups);
        Assert.Empty(result.SkippedPaths);
        Assert.All(group.Items, static item => Assert.Equal("0", item.Metadata["Bitrate"]));
    }

    [Fact]
    public async Task AnalyzeAsync_MapsProviderFailuresAndContinues()
    {
        InventoryFile io = WriteInventoryFile("a.mp4", 1);
        InventoryFile com = WriteInventoryFile("b.mkv", 1);
        InventoryFile valid = WriteInventoryFile("c.avi", 1);
        var provider = new FakeVideoSampleProvider();
        provider.Cached[io.FullPath] = (_, _) => Task.FromException<VideoSample>(new IOException("io"));
        provider.Cached[com.FullPath] = (_, _) => Task.FromException<VideoSample>(new COMException("com"));
        provider.Cached[valid.FullPath] = (_, _) => Task.FromResult(Sample());

        AnalysisResult result = await new SimilarVideoAnalyzer(provider).AnalyzeAsync(
            NewInventory([valid, com, io]),
            new SimilarVideoOptions(9),
            CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Equal([io.FullPath, com.FullPath], result.SkippedPaths.Select(static skip => skip.Path));
        Assert.All(result.SkippedPaths, static skip => Assert.Equal("Could not decode video.", skip.Reason));
        Assert.Equal([io.FullPath, com.FullPath, valid.FullPath], provider.CachedPaths);
    }

    [Fact]
    public async Task AnalyzeAsync_ChangedBeforeProviderCallWinsWithoutSampling()
    {
        InventoryFile file = WriteInventoryFile("changed-before.mp4", 2);
        File.WriteAllBytes(file.FullPath, [1, 2, 3]);
        var provider = new FakeVideoSampleProvider();

        AnalysisResult result = await new SimilarVideoAnalyzer(provider).AnalyzeAsync(
            NewInventory([file]),
            new SimilarVideoOptions(9),
            CancellationToken.None);

        SkippedPath skipped = Assert.Single(result.SkippedPaths);
        Assert.Equal("File changed since scan.", skipped.Reason);
        Assert.Empty(provider.CachedPaths);
    }

    [Fact]
    public async Task AnalyzeAsync_ChangedDuringFailedProviderCallWinsOverDecodeFailure()
    {
        InventoryFile file = WriteInventoryFile("changed-during.mp4", 2);
        var provider = new FakeVideoSampleProvider();
        provider.Cached[file.FullPath] = (path, _) =>
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            return Task.FromException<VideoSample>(new IOException("decode"));
        };

        AnalysisResult result = await new SimilarVideoAnalyzer(provider).AnalyzeAsync(
            NewInventory([file]),
            new SimilarVideoOptions(9),
            CancellationToken.None);

        SkippedPath skipped = Assert.Single(result.SkippedPaths);
        Assert.Equal("File changed since scan.", skipped.Reason);
    }

    [Fact]
    public async Task AnalyzeAsync_PropagatesCancellationBeforeAndAfterProviderCall()
    {
        InventoryFile file = WriteInventoryFile("cancel.mp4", 1);
        using var before = new CancellationTokenSource();
        before.Cancel();
        var unused = new FakeVideoSampleProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SimilarVideoAnalyzer(unused).AnalyzeAsync(
                NewInventory([file]),
                new SimilarVideoOptions(9),
                before.Token));
        Assert.Empty(unused.CachedPaths);

        using var after = new CancellationTokenSource();
        var provider = new FakeVideoSampleProvider();
        provider.Cached[file.FullPath] = (_, _) =>
        {
            after.Cancel();
            return Task.FromResult(Sample());
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SimilarVideoAnalyzer(provider).AnalyzeAsync(
                NewInventory([file]),
                new SimilarVideoOptions(9),
                after.Token));
        Assert.Single(provider.CachedPaths);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(14)]
    public async Task AnalyzeAsync_RejectsUnsupportedMeanDistanceBeforeProviderWork(int maximumDistance)
    {
        InventoryFile file = WriteInventoryFile("distance.mp4", 1);
        var provider = new FakeVideoSampleProvider();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new SimilarVideoAnalyzer(provider).AnalyzeAsync(
                NewInventory([file]),
                new SimilarVideoOptions(maximumDistance),
                CancellationToken.None));

        Assert.Empty(provider.CachedPaths);
    }

    [Theory]
    [InlineData(5, 5, true)]
    [InlineData(5, 6, false)]
    [InlineData(9, 9, true)]
    [InlineData(9, 10, false)]
    [InlineData(13, 13, true)]
    [InlineData(13, 14, false)]
    public void Regroup_UsesEveryPresetInclusiveBoundary(int maximumDistance, int actualMean, bool grouped)
    {
        IReadOnlyList<SimilarityGroup> groups = SimilarVideoAnalyzer.Regroup(
            [EvidenceItem(@"C:\videos\a.mp4", hashes: Hashes(0)), EvidenceItem(@"C:\videos\b.mp4", hashes: Hashes(LowBits(actualMean)))],
            new SimilarVideoOptions(maximumDistance));

        Assert.Equal(grouped, groups.Count == 1);
    }

    [Fact]
    public void Regroup_UsesIntegerStableTwoSecondAndTwoPercentDurationGates()
    {
        SimilarityItem oneSecond = EvidenceItem(@"C:\videos\one.mp4", duration: TimeSpan.FromSeconds(1));

        Assert.Single(SimilarVideoAnalyzer.Regroup(
            [oneSecond, EvidenceItem(@"C:\videos\three.mp4", duration: TimeSpan.FromSeconds(3))],
            new SimilarVideoOptions(0)));
        Assert.Empty(SimilarVideoAnalyzer.Regroup(
            [oneSecond, EvidenceItem(@"C:\videos\beyond-two.mp4", duration: TimeSpan.FromSeconds(3) + TimeSpan.FromTicks(1))],
            new SimilarVideoOptions(0)));

        SimilarityItem shorter = EvidenceItem(@"C:\videos\shorter.mp4", duration: TimeSpan.FromSeconds(196));
        Assert.Single(SimilarVideoAnalyzer.Regroup(
            [shorter, EvidenceItem(@"C:\videos\longer.mp4", duration: TimeSpan.FromSeconds(200))],
            new SimilarVideoOptions(0)));
        Assert.Empty(SimilarVideoAnalyzer.Regroup(
            [shorter, EvidenceItem(@"C:\videos\beyond-percent.mp4", duration: TimeSpan.FromSeconds(200) + TimeSpan.FromTicks(1))],
            new SimilarVideoOptions(0)));
    }

    [Fact]
    public void Regroup_UsesDisplayAspectRatioIncludingExactBoundaryInsteadOfOrientedPixelRatio()
    {
        SimilarityItem anamorphic = EvidenceItem(
            @"C:\videos\anamorphic.mp4",
            width: 720,
            height: 480,
            displayAspectRatio: 4d / 3d);

        Assert.Single(SimilarVideoAnalyzer.Regroup(
            [anamorphic, EvidenceItem(@"C:\videos\square-pixels.mp4", width: 640, height: 480, displayAspectRatio: 4d / 3d)],
            new SimilarVideoOptions(0)));
        Assert.Single(SimilarVideoAnalyzer.Regroup(
            [EvidenceItem(@"C:\videos\boundary-a.mp4", displayAspectRatio: 19), EvidenceItem(@"C:\videos\boundary-b.mp4", displayAspectRatio: 20)],
            new SimilarVideoOptions(0)));
        Assert.Empty(SimilarVideoAnalyzer.Regroup(
            [EvidenceItem(@"C:\videos\beyond-a.mp4", displayAspectRatio: 18.999), EvidenceItem(@"C:\videos\beyond-b.mp4", displayAspectRatio: 20)],
            new SimilarVideoOptions(0)));
    }

    [Fact]
    public void Regroup_UsesOnlyAlignedFramesAndDisplaysTheirExactMean()
    {
        SimilarityItem reference = EvidenceItem(@"C:\videos\reference.mp4", hashes: Hashes(0));
        SimilarityItem candidate = EvidenceItem(
            @"C:\videos\candidate.mp4",
            hashes: [LowBits(1), LowBits(2), LowBits(3), LowBits(4), LowBits(5)]);

        SimilarityGroup group = Assert.Single(SimilarVideoAnalyzer.Regroup(
            [candidate, reference],
            new SimilarVideoOptions(3)));

        Assert.Equal("3", group.Items[1].Metadata["MeanFrameDistance"]);
        Assert.Equal(95.3125d, group.Items[1].SimilarityPercent);

        SimilarityItem ordered = EvidenceItem(
            @"C:\videos\ordered.mp4",
            hashes: [1, 2, 4, 8, 16]);
        SimilarityItem shifted = EvidenceItem(
            @"C:\videos\shifted.mp4",
            hashes: [2, 4, 8, 16, 1]);
        Assert.Empty(SimilarVideoAnalyzer.Regroup([ordered, shifted], new SimilarVideoOptions(0)));
    }

    [Fact]
    public void RegroupPrehashed_FindsInclusiveMeanThirteenCompletenessBoundary()
    {
        int scored = 0;
        IReadOnlyList<SimilarityGroup> groups = SimilarVideoAnalyzer.RegroupPrehashed(
            [EvidenceItem(@"C:\videos\a.mp4", hashes: Hashes(0)), EvidenceItem(@"C:\videos\b.mp4", hashes: Hashes(OneBitInEachBandExceptLast()))],
            new SimilarVideoOptions(13),
            (_, _) => scored++, TestContext.Current.CancellationToken);

        Assert.Single(groups);
        Assert.Equal(1, scored);
    }

    [Fact]
    public void RegroupPrehashed_NeverScoresPairWithoutASharedAlignedBand()
    {
        int scored = 0;
        IReadOnlyList<SimilarityGroup> groups = SimilarVideoAnalyzer.RegroupPrehashed(
            [EvidenceItem(@"C:\videos\a.mp4", hashes: Hashes(0)), EvidenceItem(@"C:\videos\b.mp4", hashes: Hashes(OneBitInEveryBand()))],
            new SimilarVideoOptions(13),
            (_, _) => scored++, TestContext.Current.CancellationToken);

        Assert.Empty(groups);
        Assert.Equal(0, scored);
    }

    [Fact]
    public void RegroupPrehashed_ScoresPairFoundThroughManyBandsExactlyOnce()
    {
        int scored = 0;
        IReadOnlyList<SimilarityGroup> groups = SimilarVideoAnalyzer.RegroupPrehashed(
            [EvidenceItem(@"C:\videos\a.mp4"), EvidenceItem(@"C:\videos\b.mp4")],
            new SimilarVideoOptions(0),
            (_, _) => scored++, TestContext.Current.CancellationToken);

        Assert.Single(groups);
        Assert.Equal(1, scored);
    }

    [Fact]
    public void RegroupPrehashed_PropagatesCancellationDuringCandidateScoring()
    {
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => SimilarVideoAnalyzer.RegroupPrehashed(
            [EvidenceItem(@"C:\videos\a.mp4"), EvidenceItem(@"C:\videos\b.mp4")],
            new SimilarVideoOptions(0),
            (_, _) => cancellation.Cancel(),
            cancellation.Token));
    }

    [Fact]
    public void RegroupPrehashed_DurationWindowDropsPairsBeforeScoring()
    {
        int scored = 0;
        IReadOnlyList<SimilarityGroup> groups = SimilarVideoAnalyzer.RegroupPrehashed(
            [EvidenceItem(@"C:\videos\short.mp4", duration: TimeSpan.FromSeconds(1)), EvidenceItem(@"C:\videos\long.mp4", duration: TimeSpan.FromSeconds(10))],
            new SimilarVideoOptions(13),
            (_, _) => scored++, TestContext.Current.CancellationToken);

        Assert.Empty(groups);
        Assert.Equal(0, scored);
    }

    [Fact]
    public void Regroup_KeepsConnectedEndpointThatFailsDirectReferenceGates()
    {
        SimilarityItem reference = EvidenceItem(
            @"C:\videos\a.mp4",
            hashes: Hashes(0),
            width: 400,
            height: 400,
            displayAspectRatio: 1,
            duration: TimeSpan.FromSeconds(100));
        SimilarityItem bridge = EvidenceItem(
            @"C:\videos\b.mp4",
            hashes: Hashes(LowBits(9)),
            width: 300,
            height: 300,
            displayAspectRatio: 1.05,
            duration: TimeSpan.FromSeconds(102));
        SimilarityItem tail = EvidenceItem(
            @"C:\videos\c.mp4",
            hashes: Hashes(LowBits(18)),
            width: 200,
            height: 200,
            displayAspectRatio: 1.1025,
            duration: TimeSpan.FromSeconds(104));

        SimilarityGroup group = Assert.Single(SimilarVideoAnalyzer.Regroup(
            [tail, reference, bridge],
            new SimilarVideoOptions(9)));

        Assert.Equal([reference.FullPath, bridge.FullPath, tail.FullPath], group.Items.Select(static item => item.FullPath));
        Assert.Equal("18", group.Items[2].Metadata["MeanFrameDistance"]);
        Assert.Equal(71.875d, group.Items[2].SimilarityPercent);
    }

    [Fact]
    public void Regroup_RebuildsDeterministicReferencesOrderIdsMetadataScoresAndEvidence()
    {
        SimilarityItem zLowPixels = EvidenceItem(
            @"C:\z\low-pixels.mp4",
            width: 100,
            height: 100,
            bitrate: 9_000_000,
            size: 900);
        SimilarityItem zLowBitrate = EvidenceItem(
            @"C:\z\low-bitrate.mp4",
            hashes: Hashes(1UL << 7),
            width: 200,
            height: 100,
            bitrate: 100,
            size: 900);
        SimilarityItem zLowSize = EvidenceItem(
            @"C:\z\low-size.mp4",
            hashes: Hashes(3),
            width: 200,
            height: 100,
            bitrate: 200,
            size: 100);
        SimilarityItem zReference = EvidenceItem(
            @"C:\z\A.mp4",
            hashes: Hashes(1),
            width: 200,
            height: 100,
            bitrate: 200,
            size: 200,
            codec: "H265");
        SimilarityItem zPathTie = EvidenceItem(
            @"C:\z\a.mp4",
            hashes: Hashes(5),
            width: 200,
            height: 100,
            bitrate: 200,
            size: 200);
        SimilarityItem aReference = EvidenceItem(
            @"C:\a\reference.mp4",
            hashes: Hashes(ulong.MaxValue),
            width: 300,
            height: 200,
            bitrate: 300,
            size: 300);
        SimilarityItem aCandidate = EvidenceItem(
            @"C:\a\candidate.mp4",
            hashes: Hashes(ulong.MaxValue ^ 1),
            width: 150,
            height: 100,
            bitrate: 100,
            size: 100);

        IReadOnlyList<SimilarityGroup> groups = SimilarVideoAnalyzer.Regroup(
            [zLowPixels, aCandidate, zPathTie, zLowSize, zReference, aReference, zLowBitrate],
            new SimilarVideoOptions(5));

        Assert.Equal(2, groups.Count);
        Assert.Equal(["video-0001", "video-0002"], groups.Select(static group => group.Id));
        Assert.Equal(aReference.FullPath, groups[0].ReferenceItem.FullPath);
        SimilarityGroup zGroup = groups[1];
        Assert.Equal(zReference.FullPath, zGroup.ReferenceItem.FullPath);
        Assert.Same(zGroup.Items[0], zGroup.ReferenceItem);
        Assert.Equal(
            [zReference.FullPath, zPathTie.FullPath, zLowPixels.FullPath, zLowSize.FullPath, zLowBitrate.FullPath],
            zGroup.Items.Select(static item => item.FullPath));
        Assert.Equal(100d, zGroup.ReferenceItem.SimilarityPercent);
        Assert.Equal("Video", zGroup.Metadata["Type"]);
        Assert.Equal("5", zGroup.Metadata["MaximumMeanFrameDistance"]);
        Assert.Equal(
            ["Width", "Height", "Duration", "Bitrate", "FramesPerSecond", "Codec", "MeanFrameDistance"],
            zGroup.ReferenceItem.Metadata.Keys);
        Assert.Equal("200", zGroup.ReferenceItem.Metadata["Width"]);
        Assert.Equal("100", zGroup.ReferenceItem.Metadata["Height"]);
        Assert.Equal("00:00:10", zGroup.ReferenceItem.Metadata["Duration"]);
        Assert.Equal("200", zGroup.ReferenceItem.Metadata["Bitrate"]);
        Assert.Equal("30", zGroup.ReferenceItem.Metadata["FramesPerSecond"]);
        Assert.Equal("H265", zGroup.ReferenceItem.Metadata["Codec"]);
        Assert.Equal("0", zGroup.ReferenceItem.Metadata["MeanFrameDistance"]);
        Assert.Equal(zReference.Evidence, zGroup.ReferenceItem.Evidence);
    }

    [Fact]
    public void Regroup_SplitsAChainAndChoosesAReplacementReference()
    {
        SimilarityItem oldReference = EvidenceItem(@"C:\videos\a.mp4", hashes: Hashes(0), width: 300, height: 300);
        SimilarityItem bridge = EvidenceItem(@"C:\videos\b.mp4", hashes: Hashes(LowBits(9)), width: 200, height: 200, bitrate: 30);
        SimilarityItem tail = EvidenceItem(@"C:\videos\c.mp4", hashes: Hashes(LowBits(18)), width: 100, height: 100, bitrate: 40);

        Assert.Empty(SimilarVideoAnalyzer.Regroup([oldReference, tail], new SimilarVideoOptions(9)));
        SimilarityGroup regrouped = Assert.Single(SimilarVideoAnalyzer.Regroup([bridge, tail], new SimilarVideoOptions(9)));

        Assert.Equal(bridge.FullPath, regrouped.ReferenceItem.FullPath);
        Assert.Equal("0", regrouped.ReferenceItem.Metadata["MeanFrameDistance"]);
        Assert.Equal(100d, regrouped.ReferenceItem.SimilarityPercent);
    }

    [Fact]
    public async Task RevalidateAsync_UsesFreshSamplingOnlyAndRequiresExactEvidenceEquality()
    {
        var provider = new FakeVideoSampleProvider();
        VideoSample original = Sample();
        SimilarityItem item = EvidenceItemFromSample(@"C:\videos\clip.mp4", original);
        provider.Fresh[item.FullPath] = (_, _) => Task.FromResult(original);

        Assert.True(await new SimilarVideoAnalyzer(provider).RevalidateAsync(item, CancellationToken.None));

        provider.Fresh[item.FullPath] = (_, _) => Task.FromResult(original with { Bitrate = original.Bitrate + 1 });
        Assert.False(await new SimilarVideoAnalyzer(provider).RevalidateAsync(item, CancellationToken.None));
        Assert.Empty(provider.CachedPaths);
        Assert.Equal([item.FullPath, item.FullPath], provider.FreshPaths);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsInvalidEvidenceOrFreshShapeAndPropagatesFailureAndCancellation()
    {
        var provider = new FakeVideoSampleProvider();
        SimilarityItem image = new()
        {
            FullPath = @"C:\videos\image.jpg",
            SizeBytes = 1,
            ModifiedUtc = FixedModifiedUtc,
            SimilarityPercent = 100,
            Evidence = new ImageSimilarityEvidence(0, 10, 10, "JPEG"),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SimilarVideoAnalyzer(provider).RevalidateAsync(image, CancellationToken.None));

        SimilarityItem item = EvidenceItem(@"C:\videos\clip.mp4");
        var invalid = new FakeVideoSampleProvider();
        invalid.Fresh[item.FullPath] = (_, _) => Task.FromResult(InvalidSample("aliased-frames"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SimilarVideoAnalyzer(invalid).RevalidateAsync(item, CancellationToken.None));

        var failed = new FakeVideoSampleProvider();
        failed.Fresh[item.FullPath] = (_, _) => Task.FromException<VideoSample>(new IOException("decode"));
        await Assert.ThrowsAsync<IOException>(() =>
            new SimilarVideoAnalyzer(failed).RevalidateAsync(item, CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new FakeVideoSampleProvider();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SimilarVideoAnalyzer(cancelled).RevalidateAsync(item, cancellation.Token));
        Assert.Empty(cancelled.FreshPaths);
    }

    private InventoryFile WriteInventoryFile(string name, int size)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(static value => (byte)value).ToArray());
        File.SetLastWriteTimeUtc(path, FixedModifiedUtc);
        return NewInventoryFile(path, size, FileAttributes.Normal);
    }

    private static InventoryFile NewInventoryFile(string path, long size, FileAttributes attributes)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return new InventoryFile(
            path,
            Path.GetFileName(path),
            Path.GetExtension(path).ToLowerInvariant(),
            Path.GetDirectoryName(path) ?? string.Empty,
            size,
            info.Exists ? info.CreationTimeUtc : FixedModifiedUtc,
            info.Exists ? info.LastWriteTimeUtc : FixedModifiedUtc,
            attributes);
    }

    private static FileInventory NewInventory(
        IReadOnlyList<InventoryFile> files,
        IReadOnlyList<SkippedPath>? skips = null) => new(files, [], [], [], skips ?? []);

    private static VideoSample Sample(
        int width = 1920,
        int height = 1080,
        double displayAspectRatio = 16d / 9d,
        TimeSpan? duration = null,
        uint bitrate = 1_000_000,
        double framesPerSecond = 30,
        string codec = "H264",
        IReadOnlyList<byte[]>? frames = null) => new(
            width,
            height,
            displayAspectRatio,
            duration ?? TimeSpan.FromSeconds(10),
            bitrate,
            framesPerSecond,
            codec,
            frames ?? Frames(Filled(0)));

    private static VideoSample InvalidSample(string invalidPart)
    {
        VideoSample valid = Sample();
        return invalidPart switch
        {
            "null" => null!,
            "width" => valid with { Width = 0 },
            "height" => valid with { Height = 0 },
            "duration" => valid with { Duration = TimeSpan.Zero },
            "aspect-zero" => valid with { DisplayAspectRatio = 0 },
            "aspect-negative" => valid with { DisplayAspectRatio = -1 },
            "aspect-nan" => valid with { DisplayAspectRatio = double.NaN },
            "aspect-positive-infinity" => valid with { DisplayAspectRatio = double.PositiveInfinity },
            "aspect-negative-infinity" => valid with { DisplayAspectRatio = double.NegativeInfinity },
            "fps-zero" => valid with { FramesPerSecond = 0 },
            "fps-negative" => valid with { FramesPerSecond = -1 },
            "fps-nan" => valid with { FramesPerSecond = double.NaN },
            "fps-positive-infinity" => valid with { FramesPerSecond = double.PositiveInfinity },
            "fps-negative-infinity" => valid with { FramesPerSecond = double.NegativeInfinity },
            "codec-empty" => valid with { Codec = string.Empty },
            "codec-whitespace" => valid with { Codec = " \t" },
            "four-frames" => valid with { LuminanceFrames32x32 = Frames(Filled(0))[..4] },
            "null-frame" => valid with { LuminanceFrames32x32 = [Filled(0), Filled(1), null!, Filled(3), Filled(4)] },
            "short-frame" => valid with { LuminanceFrames32x32 = [Filled(0), Filled(1), new byte[1023], Filled(3), Filled(4)] },
            "aliased-frames" => valid with { LuminanceFrames32x32 = Enumerable.Repeat(Filled(0), 5).ToArray() },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidPart)),
        };
    }

    private static SimilarityItem EvidenceItemFromSample(string path, VideoSample sample)
    {
        ulong[] hashes = sample.LuminanceFrames32x32.Select(static frame => PerceptualHash.Compute(frame)).ToArray();
        return EvidenceItem(
            path,
            hashes,
            sample.Width,
            sample.Height,
            sample.DisplayAspectRatio,
            sample.Duration,
            sample.Bitrate,
            framesPerSecond: sample.FramesPerSecond,
            codec: sample.Codec);
    }

    private static SimilarityItem EvidenceItem(
        string path,
        IReadOnlyList<ulong>? hashes = null,
        int width = 100,
        int height = 100,
        double displayAspectRatio = 1,
        TimeSpan? duration = null,
        uint bitrate = 100,
        long size = 10,
        double framesPerSecond = 30,
        string codec = "H264")
    {
        IReadOnlyList<ulong> values = hashes ?? Hashes(0);
        return new SimilarityItem
        {
            FullPath = path,
            SizeBytes = size,
            ModifiedUtc = FixedModifiedUtc,
            SimilarityPercent = -1,
            Metadata = new Dictionary<string, string> { ["Stale"] = "discard" },
            Evidence = new VideoSimilarityEvidence(
                values[0],
                values[1],
                values[2],
                values[3],
                values[4],
                width,
                height,
                displayAspectRatio,
                duration ?? TimeSpan.FromSeconds(10),
                bitrate,
                framesPerSecond,
                codec),
        };
    }

    private static ulong[] Hashes(ulong value) => [value, value, value, value, value];

    private static ulong LowBits(int count) => count == 64 ? ulong.MaxValue : (1UL << count) - 1;

    private static ulong OneBitInEachBandExceptLast()
    {
        ulong hash = 0;
        for (int band = 0; band < 13; band++)
        {
            int offset = band < 8 ? band * 5 : 40 + ((band - 8) * 4);
            hash |= 1UL << offset;
        }

        return hash;
    }

    private static ulong OneBitInEveryBand() => OneBitInEachBandExceptLast() | (1UL << 60);

    private static byte[][] Frames(byte[] template) => Enumerable.Range(0, 5)
        .Select(_ => template.ToArray())
        .ToArray();

    private static byte[] Filled(byte value)
    {
        var bytes = new byte[1024];
        Array.Fill(bytes, value);
        return bytes;
    }

    private static byte[] Checkerboard()
    {
        var bytes = new byte[1024];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                bytes[(y * 32) + x] = (byte)(10 + (((x + y) * 180) / 62));
            }
        }

        return bytes;
    }

    private sealed class FakeVideoSampleProvider : IVideoSampleProvider
    {
        public Dictionary<string, Func<string, CancellationToken, Task<VideoSample>>> Cached { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Func<string, CancellationToken, Task<VideoSample>>> Fresh { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> CachedPaths { get; } = [];

        public List<string> FreshPaths { get; } = [];

        public Task<VideoSample> GetSampleAsync(string path, CancellationToken cancellationToken)
        {
            CachedPaths.Add(path);
            return Cached.TryGetValue(path, out Func<string, CancellationToken, Task<VideoSample>>? handler)
                ? handler(path, cancellationToken)
                : Task.FromResult(Sample());
        }

        public Task<VideoSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken)
        {
            FreshPaths.Add(path);
            return Fresh.TryGetValue(path, out Func<string, CancellationToken, Task<VideoSample>>? handler)
                ? handler(path, cancellationToken)
                : Task.FromResult(Sample());
        }
    }

    private sealed class BlockingVideoSampleProvider : IVideoSampleProvider
    {
        public BlockingVideoSampleProvider(IEnumerable<string> paths)
        {
            _gate = new(paths);
        }

        private readonly BlockingProviderGate<VideoSample> _gate;

        public int MaximumObserved => _gate.MaximumObserved;

        public int CancellationCount => _gate.CancellationCount;

        public Task WaitForEntriesAsync(int count) => _gate.WaitForEntriesAsync(count);

        public void Complete(string path, VideoSample sample) => _gate.Complete(path, sample);

        public void CompleteAll(VideoSample sample) => _gate.CompleteAll(_ => sample);

        public Task<VideoSample> GetSampleAsync(string path, CancellationToken cancellationToken) =>
            _gate.GetAsync(path, cancellationToken);

        public Task<VideoSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
