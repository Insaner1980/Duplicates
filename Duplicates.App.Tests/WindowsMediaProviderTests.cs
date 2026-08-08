using System.ComponentModel;
using System.Diagnostics;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.UI;
using Xunit.Sdk;

namespace Duplicates.App.Tests;

public sealed class WindowsMediaProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Duplicates.App.Tests", Guid.NewGuid().ToString("N"));

    public WindowsMediaProviderTests()
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
    public async Task ImageFreshSample_BypassesCacheWithoutChangingCachedSample()
    {
        string path = await WriteSourceAsync("image.png", [1]);
        string cachePath = Path.Combine(_root, "image-cache.json");
        int coreCalls = 0;
        var provider = new WindowsImageSampleProvider(
            new MediaFingerprintCache(cachePath),
            (_, _) => Task.FromResult(Image((byte)++coreCalls)));

        ImageSample first = await provider.GetSampleAsync(path, CancellationToken.None);
        ImageSample hit = await provider.GetSampleAsync(path, CancellationToken.None);
        byte[] beforeFresh = await File.ReadAllBytesAsync(cachePath);
        ImageSample fresh = await provider.GetFreshSampleAsync(path, CancellationToken.None);
        ImageSample stillCached = await provider.GetSampleAsync(path, CancellationToken.None);

        Assert.Equal(2, coreCalls);
        Assert.Equal(1, first.Luminance32x32[0]);
        Assert.Equal(1, hit.Luminance32x32[0]);
        Assert.Equal(2, fresh.Luminance32x32[0]);
        Assert.Equal(1, stillCached.Luminance32x32[0]);
        Assert.Equal(beforeFresh, await File.ReadAllBytesAsync(cachePath));
    }

    [Fact]
    public async Task VideoFreshSample_BypassesCacheWithoutChangingCachedSample()
    {
        string path = await WriteSourceAsync("video.mp4", [1]);
        string cachePath = Path.Combine(_root, "video-cache.json");
        int coreCalls = 0;
        var provider = new WindowsVideoSampleProvider(
            new MediaFingerprintCache(cachePath),
            (_, _) => Task.FromResult(Video((byte)++coreCalls)));

        VideoSample first = await provider.GetSampleAsync(path, CancellationToken.None);
        VideoSample hit = await provider.GetSampleAsync(path, CancellationToken.None);
        byte[] beforeFresh = await File.ReadAllBytesAsync(cachePath);
        VideoSample fresh = await provider.GetFreshSampleAsync(path, CancellationToken.None);
        VideoSample stillCached = await provider.GetSampleAsync(path, CancellationToken.None);

        Assert.Equal(2, coreCalls);
        Assert.Equal(1, first.LuminanceFrames32x32[0][0]);
        Assert.Equal(1, hit.LuminanceFrames32x32[0][0]);
        Assert.Equal(2, fresh.LuminanceFrames32x32[0][0]);
        Assert.Equal(1, stillCached.LuminanceFrames32x32[0][0]);
        Assert.Equal(beforeFresh, await File.ReadAllBytesAsync(cachePath));
    }

    [Fact]
    public async Task VideoFreshSample_RejectsEmptyCodec()
    {
        string path = await WriteSourceAsync("missing-codec.mp4", [1]);
        var provider = new WindowsVideoSampleProvider(
            cache: null,
            (_, _) => Task.FromResult(Video(1) with { Codec = string.Empty }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetFreshSampleAsync(path, CancellationToken.None));
    }

    [Theory]
    [InlineData("generated.png", "PNG")]
    [InlineData("generated.jpg", "JPEG")]
    public async Task ImageProvider_DecodesGeneratedImageWithStableFormatAndDimensions(
        string fileName,
        string expectedFormat)
    {
        string path = await WriteSolidImageAsync(fileName);

        ImageSample sample = await new WindowsImageSampleProvider().GetFreshSampleAsync(
            path,
            CancellationToken.None);

        Assert.Equal(2, sample.Width);
        Assert.Equal(1, sample.Height);
        Assert.Equal(expectedFormat, sample.Format);
        Assert.Equal(1024, sample.Luminance32x32.Length);
    }

    [Theory]
    [InlineData("released.png")]
    [InlineData("released.jpg")]
    public async Task ImageProvider_ReleasesSourceAfterDecode(string fileName)
    {
        string path = await WriteSolidImageAsync(fileName);
        byte[] original = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, original);
        MoveAwayAndBack(path);
        long length = new FileInfo(path).Length;

        _ = await new WindowsImageSampleProvider().GetFreshSampleAsync(path, CancellationToken.None);

        MoveAwayAndBack(path);
        await File.WriteAllBytesAsync(path, new byte[length]);
        Assert.Equal(length, new FileInfo(path).Length);
    }

    [Fact]
    public async Task MediaPreviewLoader_DecodesCanonicalImageItemCapsItAt512AndReleasesTheSource()
    {
        string path = await WriteSolidImageAsync("preview.png", width: 1024, height: 256);
        var info = new FileInfo(path);
        SimilarityItem item = new()
        {
            FullPath = path,
            SizeBytes = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
            SimilarityPercent = 100,
            Evidence = new ImageSimilarityEvidence(0, 1024, 256, "PNG"),
        };

        MediaPreviewData preview = await new WindowsMediaPreviewLoader().LoadAsync(
            ToolKind.SimilarImages,
            item,
            CancellationToken.None);

        Assert.Equal(512, preview.Width);
        Assert.Equal(128, preview.Height);
        Assert.Equal(512 * 128 * 4, preview.Bgra8.Length);
        MoveAwayAndBack(path);
        await File.WriteAllBytesAsync(path, new byte[info.Length]);
    }

    [Theory]
    [InlineData((ushort)6)]
    [InlineData((ushort)8)]
    public async Task MediaPreviewLoader_ScalesRawPixelsBeforeApplyingExifQuarterTurn(ushort orientation)
    {
        string path = await WriteExifOrientedImageAsync($"preview-{orientation}.jpg", orientation);
        var info = new FileInfo(path);
        SimilarityItem item = new()
        {
            FullPath = path,
            SizeBytes = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
            SimilarityPercent = 100,
            Evidence = new ImageSimilarityEvidence(0, 256, 1024, "JPEG"),
        };

        MediaPreviewData preview = await new WindowsMediaPreviewLoader().LoadAsync(
            ToolKind.SimilarImages,
            item,
            CancellationToken.None);

        Assert.Equal(128, preview.Width);
        Assert.Equal(512, preview.Height);
        Assert.Equal(128 * 512 * 4, preview.Bgra8.Length);
        (byte B, byte G, byte R)[] expected = orientation == 6
            ? [(255, 0, 0), (0, 0, 255), (255, 255, 255), (0, 255, 0)]
            : [(0, 255, 0), (255, 255, 255), (0, 0, 255), (255, 0, 0)];
        AssertPreviewPixel(preview, preview.Width / 4, preview.Height / 4, expected[0]);
        AssertPreviewPixel(preview, preview.Width * 3 / 4, preview.Height / 4, expected[1]);
        AssertPreviewPixel(preview, preview.Width / 4, preview.Height * 3 / 4, expected[2]);
        AssertPreviewPixel(preview, preview.Width * 3 / 4, preview.Height * 3 / 4, expected[3]);

        MoveAwayAndBack(path);
        await File.WriteAllBytesAsync(path, new byte[info.Length]);
        Assert.Equal(info.Length, new FileInfo(path).Length);
    }

    [Fact]
    public async Task MediaPreviewLoader_FailsClosedForNonImageToolOrEvidence()
    {
        SimilarityItem item = new()
        {
            FullPath = Path.Combine(_root, "preview.jpg"),
            SizeBytes = 1,
            ModifiedUtc = DateTime.UnixEpoch,
            SimilarityPercent = 100,
            Evidence = new ImageSimilarityEvidence(0, 10, 10, "JPEG"),
        };
        var loader = new WindowsMediaPreviewLoader();

        await Assert.ThrowsAsync<NotSupportedException>(() => loader.LoadAsync(
            ToolKind.MusicDuplicates,
            item,
            CancellationToken.None));
    }

    [Fact]
    public async Task VideoProvider_UsesCodedDimensionsAndDisplayRenderedPixelsForRotate90()
    {
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_root);
        StorageFile image = await WriteAsymmetricImageAsync(folder);
        StorageFile video = await folder.CreateFileAsync("rotated.mp4", CreationCollisionOption.ReplaceExisting);
        var composition = new MediaComposition();
        composition.Clips.Add(await MediaClip.CreateFromImageFileAsync(image, TimeSpan.FromSeconds(1)));
        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Wvga);
        Guid rotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1");
        profile.Video.Properties.Add(rotationKey, PropertyValue.CreateInt32(90));
        Assert.Equal(
            TranscodeFailureReason.None,
            await composition.RenderToFileAsync(video, MediaTrimmingPreference.Precise, profile));
        VideoProperties properties = await video.Properties.GetVideoPropertiesAsync();
        Assert.Equal(VideoOrientation.Rotate90, properties.Orientation);
        byte[] original = await File.ReadAllBytesAsync(video.Path);
        await File.WriteAllBytesAsync(video.Path, original);
        MoveAwayAndBack(video.Path);

        VideoSample sample = await new WindowsVideoSampleProvider().GetFreshSampleAsync(
            video.Path,
            CancellationToken.None);
        byte[] frame = sample.LuminanceFrames32x32[0];
        double leftRight = Math.Abs(Average(frame, 0, 0, 16, 32) - Average(frame, 16, 0, 16, 32));
        double topBottom = Math.Abs(Average(frame, 0, 0, 32, 16) - Average(frame, 0, 16, 32, 16));

        Assert.Equal(480, sample.Width);
        Assert.Equal(800, sample.Height);
        Assert.True(topBottom > leftRight, $"top/bottom={topBottom}, left/right={leftRight}");
        MoveAwayAndBack(video.Path);
        await File.WriteAllBytesAsync(video.Path, new byte[original.Length]);
    }

    [Theory]
    [InlineData(16d / 9d, 512, 0)]
    [InlineData(1d, 512, 0)]
    [InlineData(9d / 16d, 0, 512)]
    public void MediaPreviewLoader_VideoThumbnailUsesOnlyTheDisplayLongDimension(
        double displayAspectRatio,
        int expectedWidth,
        int expectedHeight)
    {
        var evidence = new VideoSimilarityEvidence(
            0,
            0,
            0,
            0,
            0,
            1920,
            1080,
            displayAspectRatio,
            TimeSpan.FromSeconds(10),
            1_000_000,
            30,
            "H264");

        (int width, int height) = WindowsMediaPreviewLoader.GetVideoThumbnailSize(evidence);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public async Task MediaPreviewLoader_DecodesVideoMidpointReleasesSourceAndLeavesFingerprintCacheUntouched()
    {
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_root);
        StorageFile red = await WriteColorImageAsync(folder, "preview-red.png", 255, 0, 0);
        StorageFile green = await WriteColorImageAsync(folder, "preview-green.png", 0, 255, 0);
        StorageFile blue = await WriteColorImageAsync(folder, "preview-blue.png", 0, 0, 255);
        StorageFile video = await RenderSequenceVideoAsync(
            folder,
            "preview.mp4",
            [red, green, blue],
            width: 640,
            height: 360,
            bitrate: 2_000_000);
        string cachePath = Path.Combine(_root, "preview-cache.json");
        int fingerprintCalls = 0;
        var provider = new WindowsVideoSampleProvider(
            new MediaFingerprintCache(cachePath),
            (_, _) => Task.FromResult(Video((byte)++fingerprintCalls)));
        _ = await provider.GetSampleAsync(video.Path, CancellationToken.None);
        byte[] cacheBefore = await File.ReadAllBytesAsync(cachePath);
        var info = new FileInfo(video.Path);
        SimilarityItem item = new()
        {
            FullPath = video.Path,
            SizeBytes = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
            SimilarityPercent = 100,
            Evidence = new VideoSimilarityEvidence(
                0,
                0,
                0,
                0,
                0,
                640,
                360,
                16d / 9d,
                TimeSpan.FromHours(1),
                2_000_000,
                30,
                "H264"),
        };

        MediaPreviewData preview = await new WindowsMediaPreviewLoader().LoadAsync(
            ToolKind.SimilarVideos,
            item,
            CancellationToken.None);

        Assert.Equal(512, preview.Width);
        Assert.InRange(preview.Height, 287, 289);
        AssertPreviewPixel(preview, preview.Width / 2, preview.Height / 2, (0, 255, 0));
        Assert.Equal(1, fingerprintCalls);
        Assert.Equal(cacheBefore, await File.ReadAllBytesAsync(cachePath));
        long originalLength = info.Length;
        MoveAwayAndBack(video.Path);
        await File.WriteAllBytesAsync(video.Path, new byte[originalLength]);
        Assert.Equal(originalLength, new FileInfo(video.Path).Length);
    }

    [Fact]
    public async Task SimilarVideoWindowsSmoke_GroupsSourceAndLowerBitrateTranscodeButNotUnrelated()
    {
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_root);
        var sourceImages = new List<StorageFile>();
        var unrelatedImages = new List<StorageFile>();
        for (int index = 0; index < 5; index++)
        {
            sourceImages.Add(await WritePatternImageAsync(folder, $"source-{index}.png", index, transpose: false));
            unrelatedImages.Add(await WritePatternImageAsync(folder, $"unrelated-{index}.png", index, transpose: true));
        }

        StorageFile source = await RenderSequenceVideoAsync(
            folder,
            "source.mp4",
            sourceImages,
            width: 640,
            height: 360,
            bitrate: 4_000_000);
        StorageFile unrelated = await RenderSequenceVideoAsync(
            folder,
            "unrelated.mp4",
            unrelatedImages,
            width: 640,
            height: 360,
            bitrate: 4_000_000);
        StorageFile transcode = await TranscodeVideoAsync(
            folder,
            source,
            "transcode.mp4",
            width: 640,
            height: 360,
            bitrate: 750_000);
        var files = new[] { source, transcode, unrelated }
            .Select(static file =>
            {
                var info = new FileInfo(file.Path);
                info.Refresh();
                return new InventoryFile(
                    file.Path,
                    file.Name,
                    Path.GetExtension(file.Name).ToLowerInvariant(),
                    Path.GetDirectoryName(file.Path)!,
                    info.Length,
                    info.CreationTimeUtc,
                    info.LastWriteTimeUtc,
                    System.IO.FileAttributes.Normal);
            })
            .ToArray();

        AnalysisResult result = await new SimilarVideoAnalyzer(new WindowsVideoSampleProvider()).AnalyzeAsync(
            new FileInventory(files, [], [], [], []),
            new SimilarVideoOptions(9),
            CancellationToken.None);

        SimilarityGroup group = Assert.Single(result.Groups);
        Assert.True(
            new HashSet<string>([source.Path, transcode.Path], StringComparer.OrdinalIgnoreCase)
                .SetEquals(group.Items.Select(static item => item.FullPath)));
        Assert.DoesNotContain(group.Items, item => string.Equals(item.FullPath, unrelated.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.SkippedPaths);
    }

    [Fact]
    public void ProjectedMediaCleanup_DoesNotExposeManualNativeReleaseHelper()
    {
        Assert.Null(typeof(MediaLuminanceConverter).GetMethod(
            "ReleaseNativeObject",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public async Task VideoProvider_RepeatedFreshSamplesKeepSourceReusable()
    {
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_root);
        StorageFile image = await WritePatternImageAsync(folder, "repeated-source.png", 2, transpose: false);
        StorageFile video = await RenderSequenceVideoAsync(
            folder,
            "repeated.mp4",
            [image],
            width: 640,
            height: 360,
            bitrate: 2_000_000);
        var provider = new WindowsVideoSampleProvider();

        for (int iteration = 0; iteration < 3; iteration++)
        {
            VideoSample sample = await provider.GetFreshSampleAsync(video.Path, CancellationToken.None);

            Assert.Equal(5, sample.LuminanceFrames32x32.Count);
            MoveAwayAndBack(video.Path);
        }
    }

    [Fact]
    public async Task MusicDuplicateWindowsUat_GroupsTaggedNormalizedTracksExcludesDifferentArtistAndLeavesCacheUntouched()
    {
        string first = Path.Combine(_root, "tagged-source.mp3");
        string second = Path.Combine(_root, "tagged-variant.mp3");
        string differentArtist = Path.Combine(_root, "tagged-other-artist.mp3");
        await CreateTaggedMp3Async(first, " Ｓｏｎｇ—Name ", "Artist", durationSeconds: 3, bitrate: "192k");
        await CreateTaggedMp3Async(second, "song name", "artist", durationSeconds: 4, bitrate: "128k");
        await CreateTaggedMp3Async(differentArtist, "song name", "Different Artist", durationSeconds: 3, bitrate: "128k");
        string cacheSource = await WriteSourceAsync("cache-source.png", [1]);
        string cachePath = Path.Combine(_root, "music-uat-cache.json");
        var cache = new MediaFingerprintCache(cachePath);
        _ = await cache.GetOrCreateImageAsync(
            cacheSource,
            _ => Task.FromResult(Image(7)),
            CancellationToken.None);
        byte[] cacheBefore = await File.ReadAllBytesAsync(cachePath);
        InventoryFile[] files = new[] { first, second, differentArtist }
            .Select(static path =>
            {
                var info = new FileInfo(path);
                info.Refresh();
                return new InventoryFile(
                    path,
                    info.Name,
                    info.Extension.ToLowerInvariant(),
                    info.DirectoryName!,
                    info.Length,
                    info.CreationTimeUtc,
                    info.LastWriteTimeUtc,
                    System.IO.FileAttributes.Normal);
            })
            .ToArray();

        AnalysisResult result = await new MusicDuplicateAnalyzer(new WindowsMusicMetadataProvider()).AnalyzeAsync(
            new FileInventory(files, [], [], [], []),
            new MusicDuplicateOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        SimilarityGroup group = Assert.Single(result.Groups);
        Assert.True(
            new HashSet<string>([first, second], StringComparer.OrdinalIgnoreCase)
                .SetEquals(group.Items.Select(static item => item.FullPath)),
            string.Join(
                Environment.NewLine,
                group.Items.Select(item =>
                {
                    var evidence = Assert.IsType<MusicSimilarityEvidence>(item.Evidence);
                    return $"{Path.GetFileName(item.FullPath)}: {evidence.Title} / {evidence.Artist} / " +
                        $"{evidence.AlbumArtist} / {evidence.Duration:c}";
                })));
        Assert.DoesNotContain(group.Items, item => string.Equals(
            item.FullPath,
            differentArtist,
            StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.SkippedPaths);
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisResultsViewModel(store);
        store.SetCompleted(
            ToolKind.MusicDuplicates,
            new AnalysisScope { IncludedFiles = [first, second, differentArtist] },
            new MusicDuplicateToolOptions(TimeSpan.FromSeconds(2)),
            result);
        SimilarityGroupViewModel presented = Assert.Single(viewModel.Groups);
        Assert.Equal("High confidence", presented.ReferenceItem.SimilarityText);
        Assert.DoesNotContain(
            presented.Items,
            item => item.SummaryText.Contains("100% similar", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(cacheBefore, await File.ReadAllBytesAsync(cachePath));
    }

    [Fact]
    public void ConvertBgraToLuminance_UsesChannelOrderAndExactAlphaComposite()
    {
        byte[] bgra =
        [
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
            41, 91, 173, 0,
            0, 0, 255, 128,
        ];

        byte[] luminance = MediaLuminanceConverter.ConvertBgraToLuminance(bgra);

        Assert.Equal([18, 182, 53, 255, 154], luminance);
    }

    [Theory]
    [InlineData(1920u, 1080u, 1u, 1u, true, VideoOrientation.Normal, 1920, 1080, 1.7777777777777777)]
    [InlineData(720u, 480u, 8u, 9u, true, VideoOrientation.Normal, 720, 480, 1.3333333333333333)]
    [InlineData(1920u, 1080u, 1u, 1u, true, VideoOrientation.Rotate90, 1080, 1920, 0.5625)]
    [InlineData(720u, 480u, 8u, 9u, true, VideoOrientation.Rotate270, 480, 720, 0.75)]
    [InlineData(640u, 480u, 0u, 0u, false, VideoOrientation.Normal, 640, 480, 1.3333333333333333)]
    public void CalculateDisplayGeometry_UsesParAndOrientation(
        uint codedWidth,
        uint codedHeight,
        uint parNumerator,
        uint parDenominator,
        bool hasPixelAspectRatio,
        VideoOrientation orientation,
        int expectedWidth,
        int expectedHeight,
        double expectedAspectRatio)
    {
        VideoDisplayGeometry result = WindowsVideoSampleProvider.CalculateDisplayGeometry(
            codedWidth,
            codedHeight,
            parNumerator,
            parDenominator,
            hasPixelAspectRatio,
            orientation);

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
        Assert.Equal(expectedAspectRatio, result.AspectRatio, 12);
    }

    [Theory]
    [InlineData(0u, 480u, 1u, 1u, true)]
    [InlineData(640u, 0u, 1u, 1u, true)]
    [InlineData(640u, 480u, 0u, 1u, true)]
    [InlineData(640u, 480u, 1u, 0u, true)]
    public void CalculateDisplayGeometry_RejectsStructurallyInvalidRatios(
        uint codedWidth,
        uint codedHeight,
        uint parNumerator,
        uint parDenominator,
        bool hasPixelAspectRatio)
    {
        Assert.Throws<InvalidDataException>(() => WindowsVideoSampleProvider.CalculateDisplayGeometry(
            codedWidth,
            codedHeight,
            parNumerator,
            parDenominator,
            hasPixelAspectRatio,
            VideoOrientation.Normal));
    }

    [Fact]
    public void AppServices_ComposesOneCacheAndAllMediaProviders()
    {
        var services = new AppServices();

        Assert.IsType<MediaFingerprintCache>(services.MediaFingerprintCache);
        Assert.IsType<WindowsImageSampleProvider>(services.ImageSampleProvider);
        Assert.IsType<WindowsVideoSampleProvider>(services.VideoSampleProvider);
        Assert.IsType<WindowsMusicMetadataProvider>(services.MusicMetadataProvider);
    }

    private async Task<string> WriteSourceAsync(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static async Task CreateTaggedMp3Async(
        string destinationPath,
        string title,
        string artist,
        int durationSeconds,
        string bitrate)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        string[] arguments =
        [
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-f",
            "lavfi",
            "-i",
            $"sine=frequency=440:duration={durationSeconds}",
            "-metadata",
            $"title={title}",
            "-metadata",
            $"artist={artist}",
            "-metadata",
            "album=Album",
            "-metadata",
            "track=1",
            "-metadata",
            "date=2025",
            "-metadata",
            "genre=Rock",
            "-b:a",
            bitrate,
            destinationPath,
        ];
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("FFmpeg did not start.");
        }
        catch (Win32Exception ex)
        {
            throw SkipException.ForSkip($"A tagged MP3 fixture cannot be created: {ex.Message}");
        }

        using (process)
        {
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw SkipException.ForSkip($"A tagged MP3 fixture cannot be created: {error}");
            }
        }
    }

    private async Task<string> WriteSolidImageAsync(string name, uint width = 2, uint height = 1)
    {
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_root);
        StorageFile file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        Guid encoderId = string.Equals(Path.GetExtension(name), ".png", StringComparison.OrdinalIgnoreCase)
            ? BitmapEncoder.PngEncoderId
            : BitmapEncoder.JpegEncoderId;
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        byte[] pixels = new byte[checked((int)(width * height * 4))];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset + 2] = 255;
            pixels[offset + 3] = 255;
        }

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            width,
            height,
            96,
            96,
            pixels);
        await encoder.FlushAsync();

        return file.Path;
    }

    private async Task<string> WriteExifOrientedImageAsync(string name, ushort orientation)
    {
        const uint width = 1024;
        const uint height = 256;
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_root);
        StorageFile file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        byte[] pixels = new byte[checked((int)(width * height * 4))];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * (int)width) + x) * 4;
                bool right = x >= width / 2;
                bool bottom = y >= height / 2;
                (byte blue, byte green, byte red) = (right, bottom) switch
                {
                    (false, false) => ((byte)0, (byte)0, (byte)255),
                    (true, false) => ((byte)0, (byte)255, (byte)0),
                    (false, true) => ((byte)255, (byte)0, (byte)0),
                    _ => ((byte)255, (byte)255, (byte)255),
                };
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = 255;
            }
        }

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            width,
            height,
            96,
            96,
            pixels);
        var metadata = new BitmapPropertySet
        {
            ["/app1/ifd/{ushort=274}"] = new BitmapTypedValue(orientation, PropertyType.UInt16),
        };
        await encoder.BitmapProperties.SetPropertiesAsync(metadata);
        await encoder.FlushAsync();

        return file.Path;
    }

    private static async Task<StorageFile> WriteAsymmetricImageAsync(StorageFolder folder)
    {
        StorageFile file = await folder.CreateFileAsync("asymmetric.png", CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        byte[] pixels = new byte[64 * 32 * 4];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int offset = ((y * 64) + x) * 4;
                byte value = x < 32 ? (byte)0 : (byte)255;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 64, 32, 96, 96, pixels);
        await encoder.FlushAsync();

        return file;
    }

    private static async Task<StorageFile> WriteColorImageAsync(
        StorageFolder folder,
        string name,
        byte red,
        byte green,
        byte blue)
    {
        const uint width = 320;
        const uint height = 180;
        StorageFile file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        byte[] pixels = new byte[checked((int)(width * height * 4))];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 255;
        }

        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();

        return file;
    }

    private static async Task<StorageFile> WritePatternImageAsync(
        StorageFolder folder,
        string name,
        int seed,
        bool transpose)
    {
        const uint width = 320;
        const uint height = 180;
        StorageFile file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        byte[] pixels = new byte[checked((int)(width * height * 4))];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int first = transpose ? y : x;
                int second = transpose ? x : y;
                bool bright = ((first + (seed * 37)) % 160 < 64) ^
                    (second > (seed + 1) * 24);
                byte value = bright ? (byte)255 : (byte)0;
                int offset = ((y * (int)width) + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();

        return file;
    }

    private static async Task<StorageFile> RenderSequenceVideoAsync(
        StorageFolder folder,
        string name,
        IReadOnlyList<StorageFile> images,
        uint width,
        uint height,
        uint bitrate)
    {
        StorageFile output = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        var composition = new MediaComposition();
        IList<MediaClip> clips = composition.Clips;
        MediaEncodingProfile? profile = null;
        try
        {
            foreach (StorageFile image in images)
            {
                MediaClip clip = await MediaClip.CreateFromImageFileAsync(image, TimeSpan.FromSeconds(1));
                clips.Add(clip);
            }

            profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
            profile.Video.Width = width;
            profile.Video.Height = height;
            profile.Video.Bitrate = bitrate;
            TranscodeFailureReason result = await composition.RenderToFileAsync(
                output,
                MediaTrimmingPreference.Precise,
                profile);
            Assert.True(result == TranscodeFailureReason.None, $"Video render failed: {result}.");
            return output;
        }
        finally
        {
            clips.Clear();
        }
    }

    private static async Task<StorageFile> TranscodeVideoAsync(
        StorageFolder folder,
        StorageFile source,
        string name,
        uint width,
        uint height,
        uint bitrate)
    {
        StorageFile output = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Video.Width = width;
        profile.Video.Height = height;
        profile.Video.Bitrate = bitrate;
        var transcoder = new MediaTranscoder { AlwaysReencode = true };
        PrepareTranscodeResult preparation = await transcoder.PrepareFileTranscodeAsync(source, output, profile);
        Assert.True(
            preparation.CanTranscode,
            $"Video transcode preparation failed: {preparation.FailureReason}.");
        await preparation.TranscodeAsync();
        return output;
    }

    private static double Average(byte[] frame, int x, int y, int width, int height)
    {
        long total = 0;
        for (int row = y; row < y + height; row++)
        {
            for (int column = x; column < x + width; column++)
            {
                total += frame[(row * 32) + column];
            }
        }

        return (double)total / (width * height);
    }

    private static void AssertPreviewPixel(
        MediaPreviewData preview,
        int x,
        int y,
        (byte B, byte G, byte R) expected)
    {
        int offset = ((y * preview.Width) + x) * 4;
        Assert.InRange(Math.Abs(preview.Bgra8[offset] - expected.B), 0, 40);
        Assert.InRange(Math.Abs(preview.Bgra8[offset + 1] - expected.G), 0, 40);
        Assert.InRange(Math.Abs(preview.Bgra8[offset + 2] - expected.R), 0, 40);
        Assert.Equal(255, preview.Bgra8[offset + 3]);
    }

    private static void MoveAwayAndBack(string path)
    {
        string movedPath = path + ".moved";
        File.Move(path, movedPath);
        File.Move(movedPath, path);
    }

    private static ImageSample Image(byte value) =>
        new(64, 32, Enumerable.Repeat(value, 1024).ToArray(), "PNG");

    private static VideoSample Video(byte value) => new(
        1920,
        1080,
        16d / 9d,
        TimeSpan.FromSeconds(10),
        1_000_000,
        30,
        "H264",
        Enumerable.Range(0, 5)
            .Select(index => Enumerable.Repeat((byte)(value + index), 1024).ToArray())
            .ToArray());
}
