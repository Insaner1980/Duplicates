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
}
