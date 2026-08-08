using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Models;
using Duplicates.Services;
using Xunit.Sdk;

namespace Duplicates.App.Tests;

public sealed class AnalysisServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates.App.Tests",
        Guid.NewGuid().ToString("N"));

    public AnalysisServiceTests()
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
    public void CompletionBoundaryRejectsCancellationBeforeStoppingOrReportingDone()
    {
        System.Reflection.MethodInfo? completion = typeof(AnalysisService).GetMethod(
            "CompleteSuccessfulRun",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(completion);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reports = new List<AnalysisProgress>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new AnalysisResult
        {
            Findings = [],
            Groups = [],
            SkippedPaths = [],
            Elapsed = TimeSpan.Zero,
        };

        var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() => completion.Invoke(
            null,
            [
                result,
                stopwatch,
                1,
                10L,
                new RecordingProgress(reports),
                cancellation.Token,
            ]));

        Assert.IsType<OperationCanceledException>(failure.InnerException);
        Assert.True(stopwatch.IsRunning);
        Assert.DoesNotContain(reports, report => report.Phase == AnalysisPhase.Done);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    public async Task RunAsync_InvalidMediaConcurrency_FailsBeforeInventory(int maximumConcurrency)
    {
        var reports = new List<AnalysisProgress>();

        ArgumentOutOfRangeException exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new AnalysisService().RunAsync(
                ToolKind.BigFiles,
                new AnalysisScope { IncludedFolders = [Path.Combine(_root, "missing")] },
                new LargeFileToolOptions(1),
                new AnalysisRunOptions(maximumConcurrency, UseMediaFingerprintCache: true),
                new RecordingProgress(reports),
                CancellationToken.None));

        Assert.Equal("runOptions", exception.ParamName);
        Assert.Empty(reports);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SimilarImages_RunSnapshotSelectsCacheAndRevalidationAlwaysUsesFresh(bool useCache)
    {
        string path = Path.Combine(_root, "image.jpg");
        await File.WriteAllBytesAsync(path, [1]);
        var provider = new FakeImageSampleProvider();
        var service = new AnalysisService(fileFormatProbe: null, imageSampleProvider: provider);

        await service.RunAsync(
            ToolKind.SimilarImages,
            new AnalysisScope { IncludedFiles = [path] },
            new SimilarImageToolOptions(8),
            new AnalysisRunOptions(1, useCache),
            progress: null,
            CancellationToken.None);
        await service.RevalidateSimilarityItemAsync(
            ToolKind.SimilarImages,
            new SimilarityItem
            {
                FullPath = path,
                SizeBytes = 1,
                ModifiedUtc = File.GetLastWriteTimeUtc(path),
                SimilarityPercent = 100,
                Evidence = new ImageSimilarityEvidence(0, 100, 100, "JPEG"),
            },
            CancellationToken.None);

        Assert.Equal(useCache ? [path] : [], provider.CachedPaths);
        Assert.Equal(useCache ? [path] : [path, path], provider.FreshPaths);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SimilarVideos_RunSnapshotSelectsCache(bool useCache)
    {
        string path = Path.Combine(_root, "video.mp4");
        await File.WriteAllBytesAsync(path, [1]);
        var provider = new FakeVideoSampleProvider();
        var service = new AnalysisService(
            fileFormatProbe: null,
            imageSampleProvider: null,
            videoSampleProvider: provider);

        await service.RunAsync(
            ToolKind.SimilarVideos,
            new AnalysisScope { IncludedFiles = [path] },
            new SimilarVideoToolOptions(9),
            new AnalysisRunOptions(1, useCache),
            progress: null,
            CancellationToken.None);

        Assert.Equal(useCache ? [path] : [], provider.CachedPaths);
        Assert.Equal(useCache ? [] : [path], provider.FreshPaths);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task SimilarImages_RunSnapshotBoundsAnalyzerConcurrency(int maximumConcurrency)
    {
        string[] paths = Enumerable.Range(0, 4)
            .Select(index => Path.Combine(_root, $"image-{index}.jpg"))
            .ToArray();
        foreach (string path in paths)
        {
            await File.WriteAllBytesAsync(path, [1]);
        }

        var provider = new DelayedImageSampleProvider();

        await new AnalysisService(fileFormatProbe: null, imageSampleProvider: provider).RunAsync(
            ToolKind.SimilarImages,
            new AnalysisScope { IncludedFiles = paths },
            new SimilarImageToolOptions(8),
            new AnalysisRunOptions(maximumConcurrency, UseMediaFingerprintCache: true),
            progress: null,
            CancellationToken.None);

        Assert.Equal(maximumConcurrency, provider.PeakConcurrency);
    }

    [Fact]
    public async Task SimilarVideos_RunSnapshotBoundsAnalyzerConcurrency()
    {
        string[] paths = Enumerable.Range(0, 4)
            .Select(index => Path.Combine(_root, $"video-{index}.mp4"))
            .ToArray();
        foreach (string path in paths)
        {
            await File.WriteAllBytesAsync(path, [1]);
        }

        var provider = new DelayedVideoSampleProvider();
        await new AnalysisService(null, null, provider).RunAsync(
            ToolKind.SimilarVideos,
            new AnalysisScope { IncludedFiles = paths },
            new SimilarVideoToolOptions(9),
            new AnalysisRunOptions(2, UseMediaFingerprintCache: true),
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, provider.PeakConcurrency);
    }

    [Fact]
    public async Task MusicDuplicates_RunSnapshotBoundsAnalyzerConcurrency()
    {
        string[] paths = Enumerable.Range(0, 4)
            .Select(index => Path.Combine(_root, $"song-{index}.mp3"))
            .ToArray();
        foreach (string path in paths)
        {
            await File.WriteAllBytesAsync(path, [1]);
        }

        var provider = new DelayedMusicMetadataProvider();
        await new AnalysisService(null, null, null, provider).RunAsync(
            ToolKind.MusicDuplicates,
            new AnalysisScope { IncludedFiles = paths },
            new MusicDuplicateToolOptions(TimeSpan.FromSeconds(2)),
            new AnalysisRunOptions(2, UseMediaFingerprintCache: true),
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, provider.PeakConcurrency);
    }

    [Fact]
    public async Task RunAsync_HidesInventoryDoneUntilAnalyzerCompletesAndEmitsOneFinalDone()
    {
        string path = Path.Combine(_root, "image.jpg");
        await File.WriteAllBytesAsync(path, [1]);
        var provider = new BlockingImageSampleProvider();
        var progress = new SynchronizedProgress();
        Task<AnalysisResult> run = new AnalysisService(
            fileFormatProbe: null,
            imageSampleProvider: provider).RunAsync(
                ToolKind.SimilarImages,
                new AnalysisScope { IncludedFiles = [path] },
                new SimilarImageToolOptions(8),
                new AnalysisRunOptions(1, UseMediaFingerprintCache: true),
                progress,
                CancellationToken.None);

        await provider.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.DoesNotContain(progress.Snapshot(), report => report.Phase == AnalysisPhase.Done);
        }
        finally
        {
            provider.Release();
            await run;
        }

        AnalysisProgress[] reports = progress.Snapshot();
        Assert.Contains(reports, report => report.Phase == AnalysisPhase.Inspecting);
        Assert.Equal(1, reports.Count(report => report.Phase == AnalysisPhase.Done));
        Assert.Equal(AnalysisPhase.Done, reports[^1].Phase);
    }

    [Fact]
    public async Task RunAsync_ParallelMediaProgressIsSerializedAndMonotonic()
    {
        string[] paths = Enumerable.Range(1, 4)
            .Select(index => Path.Combine(_root, $"image-{index}.jpg"))
            .ToArray();
        for (int index = 0; index < paths.Length; index++)
        {
            await File.WriteAllBytesAsync(paths[index], new byte[index + 1]);
        }

        var progress = new SynchronizedProgress();
        await new AnalysisService(
            fileFormatProbe: null,
            imageSampleProvider: new OutOfOrderImageSampleProvider()).RunAsync(
                ToolKind.SimilarImages,
                new AnalysisScope { IncludedFiles = paths },
                new SimilarImageToolOptions(8),
                new AnalysisRunOptions(4, UseMediaFingerprintCache: true),
                progress,
                CancellationToken.None);

        AnalysisProgress[] inspecting = progress.Snapshot()
            .Where(report => report.Phase == AnalysisPhase.Inspecting)
            .ToArray();
        Assert.Equal([0L, 1L, 2L, 3L, 4L], inspecting.Select(static report => report.ItemsProcessed));
        Assert.All(inspecting, report => Assert.Equal(4, report.ItemsDiscovered));
        Assert.Equal(10, inspecting[^1].BytesProcessed);
        Assert.Equal(10, inspecting[^1].TotalBytes);
        Assert.True(inspecting.Zip(inspecting.Skip(1), static (left, right) =>
            right.ItemsProcessed >= left.ItemsProcessed && right.BytesProcessed >= left.BytesProcessed).All(static value => value));
    }

    [Fact]
    public async Task InvalidLinksWithNoOptions_UsesInventoryAndIncludesTotalElapsed()
    {
        string missingTarget = Path.Combine(_root, "missing-target");
        string link = Path.Combine(_root, "broken-link");
        CreateFileSymbolicLinkOrSkip(link, missingTarget);
        string missingExclusion = Path.Combine(_root, "missing-exclusion");
        var delay = TimeSpan.FromMilliseconds(80);
        var progress = new DelayingProgress(delay);

        AnalysisResult result = await new AnalysisService().RunAsync(
            ToolKind.InvalidLinks,
            new AnalysisScope
            {
                IncludedFolders = [_root],
                ExcludedPaths = [missingExclusion],
            },
            new NoToolOptions(),
            progress,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(link, finding.FullPath);
        Assert.Equal("Link target is missing.", finding.Reason);
        Assert.Contains(result.SkippedPaths, skipped => skipped.Path == missingExclusion);
        Assert.True(progress.Delayed);
        Assert.True(result.Elapsed >= delay);
    }

    [Fact]
    public async Task BadExtensionsWithNoOptions_UsesInventoryAndIncludesTotalElapsed()
    {
        string path = Path.Combine(_root, "photo.txt");
        await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        string missingExclusion = Path.Combine(_root, "missing-exclusion");
        var delay = TimeSpan.FromMilliseconds(80);
        var progress = new DelayingProgress(delay);

        AnalysisResult result = await new AnalysisService().RunAsync(
            ToolKind.BadExtensions,
            new AnalysisScope
            {
                IncludedFolders = [_root],
                ExcludedPaths = [missingExclusion],
            },
            new NoToolOptions(),
            progress,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(path, finding.FullPath);
        Assert.Equal("photo.png", finding.Suggestion);
        Assert.Contains(result.SkippedPaths, skipped => skipped.Path == missingExclusion);
        Assert.True(progress.Delayed);
        Assert.True(result.Elapsed >= delay);
    }

    [Fact]
    public async Task BadNamesWithNoOptions_UsesInventoryAndIncludesTotalElapsed()
    {
        string path = Path.Combine(_root, " report.txt");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        string missingExclusion = Path.Combine(_root, "missing-exclusion");
        var delay = TimeSpan.FromMilliseconds(80);
        var progress = new DelayingProgress(delay);

        AnalysisResult result = await new AnalysisService().RunAsync(
            ToolKind.BadNames,
            new AnalysisScope
            {
                IncludedFolders = [_root],
                ExcludedPaths = [missingExclusion],
            },
            new NoToolOptions(),
            progress,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(path, finding.FullPath);
        Assert.Equal("report.txt", finding.Suggestion);
        Assert.Contains(result.SkippedPaths, skipped => skipped.Path == missingExclusion);
        Assert.True(progress.Delayed);
        Assert.True(result.Elapsed >= delay);
    }

    [Fact]
    public async Task BrokenFilesWithInjectedProbe_UsesInventoryAndIncludesTotalElapsed()
    {
        string path = Path.Combine(_root, "broken.png");
        await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        string missingExclusion = Path.Combine(_root, "missing-exclusion");
        var delay = TimeSpan.FromMilliseconds(80);
        var progress = new DelayingProgress(delay);
        var probe = new FakeFileFormatProbe
        {
            Handler = (_, detected, _) => Task.FromResult(
                detected?.Name == "PNG"
                    ? new FileProbeResult(FileProbeStatus.Invalid, "ImageDecodeFailure", null)
                    : new FileProbeResult(FileProbeStatus.Valid, null, null)),
        };

        AnalysisResult result = await new AnalysisService(probe).RunAsync(
            ToolKind.BrokenFiles,
            new AnalysisScope
            {
                IncludedFolders = [_root],
                ExcludedPaths = [missingExclusion],
            },
            new NoToolOptions(),
            progress,
            CancellationToken.None);

        PathFinding finding = Assert.Single(result.Findings);
        Assert.Equal(path, finding.FullPath);
        Assert.Equal("Image", finding.Metadata["Validator"]);
        Assert.Contains(result.SkippedPaths, skipped => skipped.Path == missingExclusion);
        Assert.True(progress.Delayed);
        Assert.True(result.Elapsed >= delay);
    }

    [Fact]
    public async Task InvalidLinksRejectsToolOptionsThatDoNotMatch()
    {
        var service = new AnalysisService();

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => service.RunAsync(
            ToolKind.InvalidLinks,
            new AnalysisScope { IncludedFolders = [_root] },
            new LargeFileToolOptions(1),
            progress: null,
            CancellationToken.None));

        Assert.Equal("toolOptions", exception.ParamName);
    }

    [Fact]
    public async Task BrokenFilesWithoutProbe_FailsBeforeInventory()
    {
        var service = new AnalysisService();
        var reports = new List<AnalysisProgress>();

        await Assert.ThrowsAsync<NotSupportedException>(() => service.RunAsync(
            ToolKind.BrokenFiles,
            new AnalysisScope { IncludedFolders = [Path.Combine(_root, "missing")] },
            new NoToolOptions(),
            new RecordingProgress(reports),
            CancellationToken.None));

        Assert.Empty(reports);
    }

    [Fact]
    public async Task BrokenFilesRejectsMismatchedOptionsBeforeInventory()
    {
        var reports = new List<AnalysisProgress>();

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            new AnalysisService(new FakeFileFormatProbe()).RunAsync(
                ToolKind.BrokenFiles,
                new AnalysisScope { IncludedFolders = [Path.Combine(_root, "missing")] },
                new LargeFileToolOptions(1),
                new RecordingProgress(reports),
                CancellationToken.None));

        Assert.Equal("toolOptions", exception.ParamName);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task SimilarImages_UsesInjectedProviderAndExposesFreshRevalidationAndPureRegroupArms()
    {
        string first = Path.Combine(_root, "first.jpg");
        string second = Path.Combine(_root, "second.png");
        await File.WriteAllBytesAsync(first, [1]);
        await File.WriteAllBytesAsync(second, [2]);
        var provider = new FakeImageSampleProvider();
        var service = new AnalysisService(fileFormatProbe: null, imageSampleProvider: provider);
        var options = new SimilarImageToolOptions(8);

        AnalysisResult result = await service.RunAsync(
            ToolKind.SimilarImages,
            new AnalysisScope { IncludedFiles = [second, first] },
            options,
            progress: null,
            CancellationToken.None);

        SimilarityGroup group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Items.Count);
        Assert.Equal([first, second], provider.CachedPaths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);

        Assert.True(await service.RevalidateSimilarityItemAsync(
            ToolKind.SimilarImages,
            group.Items[0],
            CancellationToken.None));
        Assert.Equal([group.Items[0].FullPath], provider.FreshPaths);

        IReadOnlyList<SimilarityGroup> regrouped = service.RegroupSimilarityItems(
            ToolKind.SimilarImages,
            options,
            group.Items);
        Assert.Equal(group.Items.Select(static item => item.FullPath), Assert.Single(regrouped).Items.Select(static item => item.FullPath));
    }

    [Fact]
    public async Task SimilarVideos_UsesInjectedProviderAndExposesFreshRevalidationAndPureRegroupArms()
    {
        string first = Path.Combine(_root, "first.mp4");
        string second = Path.Combine(_root, "second.mkv");
        await File.WriteAllBytesAsync(first, [1]);
        await File.WriteAllBytesAsync(second, [2]);
        var provider = new FakeVideoSampleProvider();
        var service = new AnalysisService(
            fileFormatProbe: null,
            imageSampleProvider: null,
            videoSampleProvider: provider);
        var options = new SimilarVideoToolOptions(9);

        AnalysisResult result = await service.RunAsync(
            ToolKind.SimilarVideos,
            new AnalysisScope { IncludedFiles = [second, first] },
            options,
            progress: null,
            CancellationToken.None);

        SimilarityGroup group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Items.Count);
        Assert.Equal([first, second], provider.CachedPaths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);

        Assert.True(await service.RevalidateSimilarityItemAsync(
            ToolKind.SimilarVideos,
            group.Items[0],
            CancellationToken.None));
        Assert.Equal([group.Items[0].FullPath], provider.FreshPaths);

        IReadOnlyList<SimilarityGroup> regrouped = service.RegroupSimilarityItems(
            ToolKind.SimilarVideos,
            options,
            group.Items);
        Assert.Equal(
            group.Items.Select(static item => item.FullPath),
            Assert.Single(regrouped).Items.Select(static item => item.FullPath));
    }

    [Fact]
    public async Task MusicDuplicates_UsesInjectedProviderAndExposesFreshRevalidationAndPureRegroupArms()
    {
        string first = Path.Combine(_root, "first.mp3");
        string second = Path.Combine(_root, "second.wma");
        await File.WriteAllBytesAsync(first, [1]);
        await File.WriteAllBytesAsync(second, [2]);
        var provider = new FakeMusicMetadataProvider();
        var service = new AnalysisService(
            fileFormatProbe: null,
            imageSampleProvider: null,
            videoSampleProvider: null,
            musicMetadataProvider: provider);
        var options = new MusicDuplicateToolOptions(TimeSpan.FromSeconds(2));

        AnalysisResult result = await service.RunAsync(
            ToolKind.MusicDuplicates,
            new AnalysisScope { IncludedFiles = [second, first] },
            options,
            progress: null,
            CancellationToken.None);

        SimilarityGroup group = Assert.Single(result.Groups);
        Assert.Equal([first, second], provider.Paths);
        Assert.True(result.Elapsed >= TimeSpan.Zero);

        Assert.True(await service.RevalidateSimilarityItemAsync(
            ToolKind.MusicDuplicates,
            group.Items[0],
            CancellationToken.None));
        Assert.Equal(group.Items[0].FullPath, provider.Paths[^1]);

        IReadOnlyList<SimilarityGroup> regrouped = service.RegroupSimilarityItems(
            ToolKind.MusicDuplicates,
            options,
            group.Items);
        SimilarityGroup rebuilt = Assert.Single(regrouped);
        Assert.Equal(group.Items.Select(static item => item.FullPath), rebuilt.Items.Select(static item => item.FullPath));
        Assert.Same(rebuilt.Items[0], rebuilt.ReferenceItem);
    }

    [Fact]
    public async Task MusicServiceArms_RejectMissingProviderAndNegativeOptionsBeforeInventory()
    {
        var reports = new List<AnalysisProgress>();
        var missing = new AnalysisService();

        await Assert.ThrowsAsync<NotSupportedException>(() => missing.RunAsync(
            ToolKind.MusicDuplicates,
            new AnalysisScope { IncludedFolders = [Path.Combine(_root, "missing")] },
            new MusicDuplicateToolOptions(TimeSpan.FromSeconds(2)),
            new RecordingProgress(reports),
            CancellationToken.None));

        var configured = new AnalysisService(null, null, null, new FakeMusicMetadataProvider());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => configured.RunAsync(
            ToolKind.MusicDuplicates,
            new AnalysisScope { IncludedFolders = [Path.Combine(_root, "missing")] },
            new MusicDuplicateToolOptions(TimeSpan.FromTicks(-1)),
            new RecordingProgress(reports),
            CancellationToken.None));

        SimilarityItem item = new()
        {
            FullPath = Path.Combine(_root, "item.mp3"),
            SizeBytes = 1,
            ModifiedUtc = DateTime.UnixEpoch,
            SimilarityPercent = 100,
            Evidence = new MusicSimilarityEvidence(
                "song",
                "artist",
                "Song",
                "Artist",
                "Album Artist",
                "Album",
                1,
                2025,
                ["Rock"],
                192_000,
                TimeSpan.FromSeconds(100)),
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => missing.RevalidateSimilarityItemAsync(
            ToolKind.MusicDuplicates,
            item,
            CancellationToken.None));
        Assert.Throws<NotSupportedException>(() => missing.RegroupSimilarityItems(
            ToolKind.MusicDuplicates,
            new MusicDuplicateToolOptions(TimeSpan.FromSeconds(2)),
            [item]));
        Assert.Empty(reports);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(14)]
    public async Task SimilarVideos_RejectsMissingProviderAndInvalidOptionsBeforeInventory(int invalidDistance)
    {
        var reports = new List<AnalysisProgress>();
        var missing = new AnalysisService();
        await Assert.ThrowsAsync<NotSupportedException>(() => missing.RunAsync(
            ToolKind.SimilarVideos,
            new AnalysisScope { IncludedFolders = [Path.Combine(_root, "missing")] },
            new SimilarVideoToolOptions(9),
            new RecordingProgress(reports),
            CancellationToken.None));

        var configured = new AnalysisService(null, null, new FakeVideoSampleProvider());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => configured.RunAsync(
            ToolKind.SimilarVideos,
            new AnalysisScope { IncludedFolders = [Path.Combine(_root, "missing")] },
            new SimilarVideoToolOptions(invalidDistance),
            new RecordingProgress(reports),
            CancellationToken.None));

        Assert.Empty(reports);
    }

    [Fact]
    public async Task SimilarImageServiceArms_FailClosedForMissingProviderAndWrongToolOptionPairs()
    {
        var missing = new AnalysisService();
        await Assert.ThrowsAsync<NotSupportedException>(() => missing.RunAsync(
            ToolKind.SimilarImages,
            new AnalysisScope { IncludedFolders = [Path.Combine(_root, "missing")] },
            new SimilarImageToolOptions(8),
            progress: null,
            CancellationToken.None));

        SimilarityItem item = new()
        {
            FullPath = Path.Combine(_root, "item.jpg"),
            SizeBytes = 1,
            ModifiedUtc = DateTime.UnixEpoch,
            SimilarityPercent = 100,
            Evidence = new ImageSimilarityEvidence(0, 100, 100, "JPEG"),
        };
        var configured = new AnalysisService(null, new FakeImageSampleProvider());
        await Assert.ThrowsAsync<NotSupportedException>(() => configured.RevalidateSimilarityItemAsync(
            ToolKind.SimilarVideos,
            item,
            CancellationToken.None));
        Assert.Throws<NotSupportedException>(() => configured.RegroupSimilarityItems(
            ToolKind.SimilarImages,
            new NoToolOptions(),
            [item]));
    }

    [Fact]
    public void AppServices_ComposesOneProbeIntoAnalysisAndActionRevalidation()
    {
        var services = new AppServices();

        Assert.IsType<WindowsFileFormatProbe>(services.FileFormatProbe);
        Assert.Same(
            services.FileFormatProbe,
            typeof(AnalysisService)
                .GetField("_fileFormatProbe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(services.AnalysisService));
        Assert.Same(
            services.FileFormatProbe,
            typeof(Duplicates.ViewModels.AnalysisResultsViewModel)
                .GetField("_fileFormatProbe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(services.AnalysisResultsViewModel));
        Assert.Same(
            services.ImageSampleProvider,
            typeof(AnalysisService)
                .GetField("_imageSampleProvider", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(services.AnalysisService));
        Assert.Same(
            services.VideoSampleProvider,
            typeof(AnalysisService)
                .GetField("_videoSampleProvider", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(services.AnalysisService));
        Assert.Same(
            services.MusicMetadataProvider,
            typeof(AnalysisService)
                .GetField("_musicMetadataProvider", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(services.AnalysisService));
        Assert.Same(
            services.AnalysisService,
            typeof(Duplicates.ViewModels.AnalysisResultsViewModel)
                .GetField("_analysisService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(services.AnalysisResultsViewModel));
        Assert.IsType<WindowsMediaPreviewLoader>(services.MediaPreviewLoader);
    }

    private static void CreateFileSymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (IsLinkCapabilityFailure(ex))
        {
            throw SkipException.ForSkip($"A file symbolic-link fixture cannot be created: {ex.Message}");
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

    private sealed class DelayingProgress(TimeSpan delay) : IProgress<AnalysisProgress>
    {
        public bool Delayed { get; private set; }

        public void Report(AnalysisProgress value)
        {
            if (Delayed)
            {
                return;
            }

            Delayed = true;
            Thread.Sleep(delay);
        }
    }

    private sealed class RecordingProgress(List<AnalysisProgress> reports) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => reports.Add(value);
    }

    private sealed class DelayedImageSampleProvider : IImageSampleProvider
    {
        private int _active;
        private int _peakConcurrency;

        public int PeakConcurrency => Volatile.Read(ref _peakConcurrency);

        public Task<ImageSample> GetSampleAsync(string path, CancellationToken cancellationToken) =>
            GetAsync(cancellationToken);

        public Task<ImageSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken) =>
            GetAsync(cancellationToken);

        private async Task<ImageSample> GetAsync(CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            int peak;
            while (active > (peak = Volatile.Read(ref _peakConcurrency)) &&
                   Interlocked.CompareExchange(ref _peakConcurrency, active, peak) != peak)
            {
            }

            try
            {
                await Task.Delay(100, cancellationToken);
                return new ImageSample(100, 100, new byte[1024], "JPEG");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class DelayedVideoSampleProvider : IVideoSampleProvider
    {
        private readonly ConcurrencyCounter _counter = new();

        public int PeakConcurrency => _counter.PeakConcurrency;

        public Task<VideoSample> GetSampleAsync(string path, CancellationToken cancellationToken) =>
            GetAsync(cancellationToken);

        public Task<VideoSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken) =>
            GetAsync(cancellationToken);

        private async Task<VideoSample> GetAsync(CancellationToken cancellationToken)
        {
            await _counter.DelayAsync(cancellationToken);
            return FakeVideoSampleProvider.Video();
        }
    }

    private sealed class DelayedMusicMetadataProvider : IMusicMetadataProvider
    {
        private readonly ConcurrencyCounter _counter = new();

        public int PeakConcurrency => _counter.PeakConcurrency;

        public async Task<MusicMetadata> GetMetadataAsync(string path, CancellationToken cancellationToken)
        {
            await _counter.DelayAsync(cancellationToken);
            return FakeMusicMetadataProvider.Music();
        }
    }

    private sealed class ConcurrencyCounter
    {
        private int _active;
        private int _peakConcurrency;

        public int PeakConcurrency => Volatile.Read(ref _peakConcurrency);

        public async Task DelayAsync(CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            int peak;
            while (active > (peak = Volatile.Read(ref _peakConcurrency)) &&
                   Interlocked.CompareExchange(ref _peakConcurrency, active, peak) != peak)
            {
            }

            try
            {
                await Task.Delay(100, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class BlockingImageSampleProvider : IImageSampleProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public Task<ImageSample> GetSampleAsync(string path, CancellationToken cancellationToken) =>
            GetAsync(cancellationToken);

        public Task<ImageSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken) =>
            GetAsync(cancellationToken);

        public void Release() => _release.TrySetResult();

        private async Task<ImageSample> GetAsync(CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new ImageSample(100, 100, new byte[1024], "JPEG");
        }
    }

    private sealed class OutOfOrderImageSampleProvider : IImageSampleProvider
    {
        public Task<ImageSample> GetSampleAsync(string path, CancellationToken cancellationToken) =>
            GetAsync(path, cancellationToken);

        public Task<ImageSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken) =>
            GetAsync(path, cancellationToken);

        private static async Task<ImageSample> GetAsync(string path, CancellationToken cancellationToken)
        {
            int ordinal = int.Parse(Path.GetFileNameWithoutExtension(path).AsSpan("image-".Length));
            await Task.Delay(TimeSpan.FromMilliseconds((5 - ordinal) * 30), cancellationToken);
            return new ImageSample(100, 100, new byte[1024], "JPEG");
        }
    }

    private sealed class SynchronizedProgress : IProgress<AnalysisProgress>
    {
        private readonly Lock _gate = new();
        private readonly List<AnalysisProgress> _reports = [];

        public void Report(AnalysisProgress value)
        {
            lock (_gate)
            {
                _reports.Add(value);
            }
        }

        public AnalysisProgress[] Snapshot()
        {
            lock (_gate)
            {
                return [.. _reports];
            }
        }
    }
}
