using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Duplicates.Engine.Analysis;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Xunit.Sdk;

namespace Duplicates.App.Tests;

public sealed partial class VideoOptimizerRealUatTests : IDisposable
{
    private const string UatCategory = "Task18RealUat";
    private static readonly TimeSpan FixtureTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FileSystemActionTimeout = TimeSpan.FromSeconds(15);

    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates-VideoOptimizer-Uat",
        Guid.NewGuid().ToString("N"));

    public VideoOptimizerRealUatTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        RunBoundedFileSystemAction(
            "Delete video optimizer UAT root",
            _root,
            () => Directory.Delete(_root, recursive: true));
    }

    [Fact]
    [Trait("Category", UatCategory)]
    public async Task SuccessfulH264AacOptimize_RealWindowsStackPublishesVerifiedMp4AndReleasesFiles()
    {
        string source = await CreateH264FixtureAsync(
            "h264-aac-1080p.mp4",
            width: 1920,
            height: 1080,
            seconds: 2,
            includeAudio: true,
            lossless: true,
            denseAudio: true);
        VideoMediaInfo sourceMedia = await new WindowsVideoMediaProbe()
            .ProbeAsync(source, CancellationToken.None);
        Assert.Equal("MP4", sourceMedia.ContainerCodec);
        Assert.Equal("H.264", sourceMedia.VideoCodec);
        Assert.Equal("AAC", sourceMedia.AudioCodec);
        byte[] sourceHash = await HashFileAsync(source);
        DateTime sourceModifiedUtc = File.GetLastWriteTimeUtc(source);
        string destination = Path.Combine(_root, "h264-aac-1080p.optimized.mp4");

        VideoOptimizationResult result = await CreateService().OptimizeAsync(
            CreateRequest(
                source,
                destination,
                keepOutput: false,
                new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, false, false)),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.Succeeded, result.Outcome);
        Assert.Equal(destination, result.OutputPath);
        Assert.NotNull(result.SourceMedia);
        VideoMediaInfo outputMedia = Assert.IsType<VideoMediaInfo>(result.OutputMedia);
        AssertVerifiedMp4(outputMedia, expectAudio: true);
        Assert.Equal(1u, outputMedia.PixelAspectRatioNumerator);
        Assert.Equal(1u, outputMedia.PixelAspectRatioDenominator);
        Assert.Equal(sourceHash, await HashFileAsync(source));
        Assert.Equal(sourceModifiedUtc, File.GetLastWriteTimeUtc(source));
        Assert.True(File.Exists(destination));
        Assert.True(new FileInfo(destination).Length < new FileInfo(source).Length);
        AssertNoTemporaryArtifacts();

        await new WindowsVideoMediaProbe().ProbeAsync(destination, CancellationToken.None);
        await new WindowsVideoMediaProbe().ProbeAsync(source, CancellationToken.None);
        string movedSource = Path.Combine(_root, "source-renamed.mp4");
        string movedOutput = Path.Combine(_root, "output-renamed.mp4");
        RunBoundedFileSystemAction(
            "Move optimized source fixture",
            $"{source} -> {movedSource}",
            () => File.Move(source, movedSource));
        RunBoundedFileSystemAction(
            "Move optimized output fixture",
            $"{destination} -> {movedOutput}",
            () => File.Move(destination, movedOutput));
        RunBoundedFileSystemAction(
            "Delete moved source fixture",
            movedSource,
            () => File.Delete(movedSource));
        RunBoundedFileSystemAction(
            "Delete moved output fixture",
            movedOutput,
            () => File.Delete(movedOutput));
        Assert.False(File.Exists(movedSource));
        Assert.False(File.Exists(movedOutput));
    }

    private static void RunBoundedFileSystemAction(string action, string path, Action operation)
    {
        try
        {
            Task.Run(operation)
                .WaitAsync(FileSystemActionTimeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"{action} timed out after {FileSystemActionTimeout.TotalSeconds:0} seconds for '{path}'.",
                ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{action} failed for '{path}'.", ex);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", UatCategory)]
    public Task HardwareAcceleration_RealWindowsStackCompletesWithRequestedMode(bool enabled) =>
        HardwareAccelerationCoreAsync(enabled);

    private async Task HardwareAccelerationCoreAsync(bool enabled)
    {
        string source = await CreateH264FixtureAsync(
            $"hardware-{enabled}.mp4",
            width: 960,
            height: 540,
            seconds: 1.25,
            includeAudio: true,
            lossless: true,
            denseAudio: true);
        await new WindowsVideoMediaProbe().ProbeAsync(source, CancellationToken.None);
        byte[] sourceHash = await HashFileAsync(source);
        DateTime sourceModifiedUtc = File.GetLastWriteTimeUtc(source);
        string destination = Path.Combine(_root, $"hardware-{enabled}.optimized.mp4");

        VideoOptimizationResult result = await CreateService().OptimizeAsync(
            CreateRequest(
                source,
                destination,
                keepOutput: true,
                new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, enabled, true)),
            null,
            CancellationToken.None);

        AssertPublished(result);
        Assert.True(File.Exists(destination));
        Assert.Equal(sourceHash, await HashFileAsync(source));
        Assert.Equal(sourceModifiedUtc, File.GetLastWriteTimeUtc(source));
        AssertNoTemporaryArtifacts();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", UatCategory)]
    public Task AlreadyEfficient_RealWindowsStackAppliesNoSpaceSavingPolicy(bool keepOutput) =>
        AlreadyEfficientCoreAsync(keepOutput);

    private async Task AlreadyEfficientCoreAsync(bool keepOutput)
    {
        string source = await CreateH264FixtureAsync(
            $"efficient-{keepOutput}.mp4",
            width: 320,
            height: 240,
            seconds: 1,
            includeAudio: false,
            lossless: false,
            efficient: true);
        await new WindowsVideoMediaProbe().ProbeAsync(source, CancellationToken.None);
        byte[] sourceHash = await HashFileAsync(source);
        DateTime sourceModifiedUtc = File.GetLastWriteTimeUtc(source);
        string destination = Path.Combine(_root, $"efficient-{keepOutput}.optimized.mp4");

        VideoOptimizationResult result = await CreateService().OptimizeAsync(
            CreateRequest(
                source,
                destination,
                keepOutput,
                new VideoOptimizationOptions(VideoOptimizationPreset.Smaller, false, keepOutput)),
            null,
            CancellationToken.None);

        VideoOptimizationOutcome expected = keepOutput
            ? VideoOptimizationOutcome.KeptWithoutSaving
            : VideoOptimizationOutcome.NoSpaceSaving;
        Assert.True(
            result.Outcome == expected,
            $"Expected {expected}, received {result.Outcome}; source={new FileInfo(source).Length}, " +
            $"output={result.OutputSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "missing"}.");
        long sourceSize = new FileInfo(source).Length;
        long outputSize = Assert.IsType<long>(result.OutputSizeBytes);
        Assert.True(outputSize >= sourceSize, $"source={sourceSize}, output={outputSize}");

        Assert.Equal(sourceHash, await HashFileAsync(source));
        Assert.Equal(sourceModifiedUtc, File.GetLastWriteTimeUtc(source));
        Assert.Equal(keepOutput, File.Exists(destination));
        AssertNoTemporaryArtifacts();
    }

    [Theory]
    [InlineData(720, 1280, 606, 1080, false)]
    [InlineData(721, 1281, 606, 1080, true)]
    [Trait("Category", UatCategory)]
    public Task PortraitAndOddDimensions_RealWindowsStackUseEvenFloorWithoutUpscale(
        int width,
        int height,
        int expectedWidth,
        int expectedHeight,
        bool requires444) => requires444
            ? RunOptionalUatAsync(() => PortraitAndOddDimensionsCoreAsync(
                width,
                height,
                expectedWidth,
                expectedHeight,
                requires444))
            : PortraitAndOddDimensionsCoreAsync(
                width,
                height,
                expectedWidth,
                expectedHeight,
                requires444);

    private async Task PortraitAndOddDimensionsCoreAsync(
        int width,
        int height,
        int expectedWidth,
        int expectedHeight,
        bool requires444)
    {
        string source = await CreateH264FixtureAsync(
            $"portrait-{width}x{height}.mp4",
            width,
            height,
            seconds: 1,
            includeAudio: false,
            lossless: true,
            pixelFormat: requires444 ? "yuv444p" : "yuv420p");
        VideoMediaInfo input = requires444
            ? await ProbeOrUnavailableAsync(source, "odd H.264 projection")
            : await new WindowsVideoMediaProbe().ProbeAsync(source, CancellationToken.None);
        if (input.Width != width || input.Height != height)
        {
            if (requires444)
            {
                Unavailable(
                    "odd H.264 projection",
                    $"Windows reported {input.Width}x{input.Height} for the {width}x{height} fixture");
            }

            Assert.Fail($"Windows reported {input.Width}x{input.Height} for the {width}x{height} fixture.");
        }

        string destination = Path.Combine(_root, $"portrait-{width}x{height}.optimized.mp4");
        var options = new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, false, true);
        VideoOptimizationResult result = requires444
            ? await OptimizeOrUnavailableAsync(
                source,
                destination,
                options,
                $"odd {width}x{height} transcode")
            : await CreateService().OptimizeAsync(
                CreateRequest(source, destination, keepOutput: true, options),
                null,
                CancellationToken.None);

        AssertPublished(result);
        VideoMediaInfo output = Assert.IsType<VideoMediaInfo>(result.OutputMedia);
        Assert.Equal(expectedWidth, output.Width);
        Assert.Equal(expectedHeight, output.Height);
        Assert.True(output.Width <= input.SquarePixelDisplayWidth);
        Assert.True(output.Height <= input.SquarePixelDisplayHeight);
        Assert.Equal(output.PixelAspectRatioNumerator, output.PixelAspectRatioDenominator);
        AssertNoTemporaryArtifacts();
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    [Trait("Category", UatCategory)]
    public Task Rotation_RealWindowsProjectionPreservesAsymmetricGeometry(int rotation) =>
        RotationCoreAsync(rotation);

    private async Task RotationCoreAsync(int rotation)
    {
        string basePath = await CreateH264FixtureAsync(
            $"rotation-{rotation}-base.mp4",
            width: 640,
            height: 360,
            seconds: 1,
            includeAudio: false,
            lossless: true);
        string source = Path.Combine(_root, $"rotation-{rotation}.mp4");
        await RunFfmpegAsync(
            $"Rotate{rotation} fixture",
            "-display_rotation", rotation.ToString(CultureInfo.InvariantCulture),
            "-i", basePath,
            "-map", "0",
            "-c", "copy",
            source);
        int? recordedRotation = await ReadDisplayRotationAsync(source);
        Assert.True(
            recordedRotation is not null && NormalizeRotation(recordedRotation.Value) == rotation,
            $"FFprobe reported display rotation {recordedRotation?.ToString(CultureInfo.InvariantCulture) ?? "missing"}.");

        VideoMediaInfo input = await new WindowsVideoMediaProbe()
            .ProbeAsync(source, CancellationToken.None);
        Assert.True(
            input.Width == 360 && input.Height == 640,
            $"Windows reported {input.Width}x{input.Height}; the projection did not expose the asymmetric rotation.");

        string destination = Path.Combine(_root, $"rotation-{rotation}.optimized.mp4");
        VideoOptimizationResult result = await CreateService().OptimizeAsync(
            CreateRequest(
                source,
                destination,
                keepOutput: true,
                new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, false, true)),
            null,
            CancellationToken.None);

        AssertPublished(result);
        VideoMediaInfo output = Assert.IsType<VideoMediaInfo>(result.OutputMedia);
        Assert.Equal(360, output.Width);
        Assert.Equal(640, output.Height);
        Assert.Equal(360d, output.SquarePixelDisplayWidth);
        Assert.Equal(640d, output.SquarePixelDisplayHeight);
        AssertNoTemporaryArtifacts();
    }

    [Fact]
    [Trait("Category", UatCategory)]
    public Task AnisotropicPar_RealWindowsStackOutputsSquarePixels() =>
        AnisotropicParCoreAsync();

    private async Task AnisotropicParCoreAsync()
    {
        string source = await CreateH264FixtureAsync(
            "sar-8-9.mp4",
            width: 720,
            height: 480,
            seconds: 1,
            includeAudio: false,
            lossless: true,
            videoFilter: "setsar=8/9");
        VideoMediaInfo input = await new WindowsVideoMediaProbe()
            .ProbeAsync(source, CancellationToken.None);
        Assert.Equal(8u, input.PixelAspectRatioNumerator);
        Assert.Equal(9u, input.PixelAspectRatioDenominator);

        Assert.Equal(640d, input.SquarePixelDisplayWidth);
        Assert.Equal(480d, input.SquarePixelDisplayHeight);
        string destination = Path.Combine(_root, "sar-8-9.optimized.mp4");

        VideoOptimizationResult result = await CreateService().OptimizeAsync(
            CreateRequest(
                source,
                destination,
                keepOutput: true,
                new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, false, true)),
            null,
            CancellationToken.None);

        AssertPublished(result);
        VideoMediaInfo output = Assert.IsType<VideoMediaInfo>(result.OutputMedia);
        Assert.Equal(640, output.Width);
        Assert.Equal(480, output.Height);
        Assert.Equal(1u, output.PixelAspectRatioNumerator);
        Assert.Equal(1u, output.PixelAspectRatioDenominator);
        Assert.Equal(640d, output.SquarePixelDisplayWidth);
        Assert.Equal(480d, output.SquarePixelDisplayHeight);
        AssertNoTemporaryArtifacts();
    }

    [Fact]
    [Trait("Category", UatCategory)]
    public Task NoAudio_RealWindowsStackDoesNotAddAudio() =>
        NoAudioCoreAsync();

    private async Task NoAudioCoreAsync()
    {
        string source = await CreateH264FixtureAsync(
            "no-audio.mp4",
            width: 640,
            height: 360,
            seconds: 1,
            includeAudio: false,
            lossless: true);
        VideoMediaInfo input = await new WindowsVideoMediaProbe()
            .ProbeAsync(source, CancellationToken.None);
        Assert.Equal(0, input.AudioTrackCount);
        Assert.Null(input.AudioCodec);
        string destination = Path.Combine(_root, "no-audio.optimized.mp4");

        VideoOptimizationResult result = await CreateService().OptimizeAsync(
            CreateRequest(
                source,
                destination,
                keepOutput: true,
                new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, false, true)),
            null,
            CancellationToken.None);

        AssertPublished(result);
        VideoMediaInfo output = Assert.IsType<VideoMediaInfo>(result.OutputMedia);
        Assert.Equal(0, output.AudioTrackCount);
        Assert.Null(output.AudioCodec);
        AssertNoTemporaryArtifacts();
    }

    [Theory]
    [InlineData("multi-audio")]
    [InlineData("timed-subtitle")]
    [Trait("Category", UatCategory)]
    public Task UnsupportedTrackLayouts_RealProbeRejectsWithoutArtifacts(string layout) =>
        RunOptionalUatAsync(() => UnsupportedTrackLayoutsCoreAsync(layout));

    private async Task UnsupportedTrackLayoutsCoreAsync(string layout)
    {
        string source = layout == "multi-audio"
            ? await CreateMultiAudioFixtureAsync()
            : await CreateTimedSubtitleFixtureAsync();
        await AssertFixtureTrackLayoutAsync(source, layout);
        var probe = new WindowsVideoMediaProbe();

        InvalidDataException? rejection = await Record.ExceptionAsync(
            () => probe.ProbeAsync(source, CancellationToken.None)) as InvalidDataException;
        if (rejection is null)
        {
            Unavailable(
                $"{layout} Windows projection",
                "the fixture contains the required extra track, but the Windows projection flattened or rejected it without exposing the optimizer track guard");
        }

        string destination = Path.Combine(_root, $"{layout}.optimized.mp4");
        VideoOptimizationResult result = await CreateService().OptimizeAsync(
            CreateRequest(source, destination, keepOutput: false),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.UnsupportedInput, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.False(File.Exists(destination));
        AssertNoTemporaryArtifacts();
    }

    [Fact]
    [Trait("Category", UatCategory)]
    public Task CodecDependentUnsupportedInput_RealWindowsStackFailsClosed() =>
        CodecDependentUnsupportedInputCoreAsync();

    private async Task CodecDependentUnsupportedInputCoreAsync()
    {
        string source = Path.Combine(_root, "codec-dependent-ffv1.mkv");
        await RunFfmpegAsync(
            "FFV1 unsupported-codec fixture",
            "-f", "lavfi",
            "-i", "testsrc2=size=640x360:rate=30",
            "-t", "1",
            "-c:v", "ffv1",
            source);
        byte[] sourceHash = await HashFileAsync(source);
        DateTime sourceModifiedUtc = File.GetLastWriteTimeUtc(source);
        string destination = Path.Combine(_root, "codec-dependent-ffv1.optimized.mp4");

        VideoOptimizationResult result = await CreateService().OptimizeAsync(
            CreateRequest(source, destination, keepOutput: false),
            null,
            CancellationToken.None);
        Assert.Contains(
            result.Outcome,
            new[]
            {
                VideoOptimizationOutcome.CodecNotFound,
                VideoOptimizationOutcome.UnsupportedInput,
                VideoOptimizationOutcome.Failed,
            });
        Assert.Null(result.OutputPath);
        Assert.False(File.Exists(destination));
        Assert.Equal(sourceHash, await HashFileAsync(source));
        Assert.Equal(sourceModifiedUtc, File.GetLastWriteTimeUtc(source));
        AssertNoTemporaryArtifacts();
    }

    [Fact]
    [Trait("Category", UatCategory)]
    public Task MidTranscodeCancellation_RealWindowsStackCleansArtifactsAndPreservesSource() =>
        MidTranscodeCancellationCoreAsync();

    private async Task MidTranscodeCancellationCoreAsync()
    {
        string source = await CreateH264FixtureAsync(
            "cancellation-1080p.mp4",
            width: 1920,
            height: 1080,
            seconds: 8,
            includeAudio: true,
            lossless: true);
        await new WindowsVideoMediaProbe().ProbeAsync(source, CancellationToken.None);
        byte[] sourceHash = await HashFileAsync(source);
        DateTime sourceModifiedUtc = File.GetLastWriteTimeUtc(source);
        string destination = Path.Combine(_root, "cancellation-1080p.optimized.mp4");
        var sourceInfo = new FileInfo(source);
        var inventory = new FileInventory(
            [new InventoryFile(
                sourceInfo.FullName,
                sourceInfo.Name,
                sourceInfo.Extension.ToLowerInvariant(),
                sourceInfo.DirectoryName!,
                sourceInfo.Length,
                sourceInfo.CreationTimeUtc,
                sourceInfo.LastWriteTimeUtc,
                sourceInfo.Attributes)],
            [],
            [sourceInfo.FullName],
            [],
            []);
        var scope = new PathScopeViewModel();
        Assert.True(scope.AddFile(source));
        var coordinator = new AppOperationCoordinator();
        bool observedBackendProgress = false;
        bool leaseBusyWhenCancellationRequested = false;
        bool sawLease = false;
        bool cleanupCompleteWhenReleased = false;
        coordinator.ActiveOperationChanged += (_, _) =>
        {
            if (coordinator.ActiveOperation is { } active)
            {
                sawLease = true;
                Assert.Equal(
                    new AppOperationDescriptor(AppOperationKind.VideoOptimization, false),
                    active);
                return;
            }

            if (sawLease)
            {
                cleanupCompleteWhenReleased =
                    !File.Exists(destination) &&
                    !Directory.EnumerateFiles(_root).Any(path =>
                        Path.GetFileName(path).Contains(
                            ".duplicates-video-",
                            StringComparison.OrdinalIgnoreCase));
            }
        };
        var viewModel = new VideoOptimizerViewModel(
            CreateService(),
            scope,
            new UnusedFileActionService(),
            coordinator,
            (_, _) => inventory,
            progressFactory: callback => new InlineProgress(value =>
            {
                callback(value);
                if (!observedBackendProgress && value > 5)
                {
                    observedBackendProgress = true;
                    leaseBusyWhenCancellationRequested = coordinator.ActiveOperation is not null;
                    coordinator.RequestCancellation();
                }
            }));

        await viewModel.OptimizeVideosCommand.ExecuteAsync(null);
        Assert.True(
            observedBackendProgress,
            "The bounded MediaTranscoder operation emitted no observable intermediate backend progress.");
        Assert.True(leaseBusyWhenCancellationRequested);
        Assert.True(cleanupCompleteWhenReleased);
        Assert.Null(coordinator.ActiveOperation);
        Assert.True(coordinator.WaitForIdleAsync().IsCompletedSuccessfully);
        Assert.False(viewModel.IsOptimizing);
        Assert.Contains("cancel", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
        Assert.Equal(sourceHash, await HashFileAsync(source));
        Assert.Equal(sourceModifiedUtc, File.GetLastWriteTimeUtc(source));
        AssertNoTemporaryArtifacts();
    }

    [Fact]
    [Trait("Category", UatCategory)]
    public Task DestinationCollision_RealWindowsStackPreservesOccupantWithoutPublication() =>
        DestinationCollisionCoreAsync();

    private async Task DestinationCollisionCoreAsync()
    {
        string source = await CreateH264FixtureAsync(
            "collision-source.mp4",
            width: 640,
            height: 360,
            seconds: 1,
            includeAudio: false,
            lossless: true);
        await new WindowsVideoMediaProbe().ProbeAsync(source, CancellationToken.None);
        byte[] sourceHash = await HashFileAsync(source);
        DateTime sourceModifiedUtc = File.GetLastWriteTimeUtc(source);
        string destination = Path.Combine(_root, "collision-source.optimized.mp4");
        byte[] occupant = [9, 7, 5, 3, 1];
        await File.WriteAllBytesAsync(destination, occupant);

        VideoOptimizationResult result = await CreateService().OptimizeAsync(
            CreateRequest(source, destination, keepOutput: false),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.DestinationCollision, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Equal(occupant, await File.ReadAllBytesAsync(destination));
        Assert.Equal(sourceHash, await HashFileAsync(source));
        Assert.Equal(sourceModifiedUtc, File.GetLastWriteTimeUtc(source));
        AssertNoTemporaryArtifacts();
    }

    private async Task<string> CreateH264FixtureAsync(
        string name,
        int width,
        int height,
        double seconds,
        bool includeAudio,
        bool lossless,
        bool efficient = false,
        string pixelFormat = "yuv420p",
        string? videoFilter = null,
        bool denseAudio = false)
    {
        string path = Path.Combine(_root, name);
        var arguments = new List<string>
        {
            "-f", "lavfi",
            "-i", efficient
                ? $"color=c=black:size={width}x{height}:rate=30"
                : $"testsrc2=size={width}x{height}:rate=30",
        };
        if (includeAudio)
        {
            arguments.AddRange([
                "-f", "lavfi", "-i",
                denseAudio
                    ? "anoisesrc=color=white:sample_rate=48000"
                    : "sine=frequency=1000:sample_rate=48000"
            ]);
        }

        arguments.AddRange(["-t", seconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        if (includeAudio)
        {
            arguments.Add("-shortest");
        }

        arguments.AddRange(["-c:v", "libx264", "-preset", efficient ? "veryslow" : "ultrafast"]);
        arguments.AddRange(lossless
            ? [
                "-crf", "10",
                "-profile:v", string.Equals(pixelFormat, "yuv444p", StringComparison.OrdinalIgnoreCase)
                    ? "high444"
                    : "high",
                "-level:v", "4.1"
            ]
            : ["-crf", efficient ? "51" : "23"]);
        arguments.AddRange(["-pix_fmt", pixelFormat]);
        if (videoFilter is not null)
        {
            arguments.AddRange(["-vf", videoFilter]);
        }

        if (includeAudio)
        {
            arguments.AddRange(["-c:a", "aac"]);
            if (denseAudio)
            {
                arguments.AddRange(["-ac", "2"]);
            }

            arguments.AddRange(["-b:a", denseAudio ? "256k" : "192k"]);
        }
        else
        {
            arguments.Add("-an");
        }

        if (efficient)
        {
            arguments.AddRange(["-map_metadata", "-1", "-fflags", "+bitexact", "-flags:v", "+bitexact"]);
        }

        arguments.AddRange(["-movflags", "+faststart", path]);
        await RunFfmpegAsync($"{width}x{height} H.264 fixture", arguments.ToArray());
        return path;
    }

    private async Task<string> CreateMultiAudioFixtureAsync()
    {
        string path = Path.Combine(_root, "multi-audio.mp4");
        await RunFfmpegAsync(
            "multi-audio fixture",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000",
            "-t", "1",
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "96k",
            path);
        return path;
    }

    private async Task<string> CreateTimedSubtitleFixtureAsync()
    {
        string subtitle = Path.Combine(_root, "timed-subtitle.srt");
        await File.WriteAllTextAsync(subtitle, "1\n00:00:00,000 --> 00:00:00,800\nTimed metadata fixture\n");
        string path = Path.Combine(_root, "timed-subtitle.mp4");
        await RunFfmpegAsync(
            "timed subtitle fixture",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
            "-f", "srt", "-i", subtitle,
            "-t", "1",
            "-map", "0:v:0", "-map", "1:s:0",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-pix_fmt", "yuv420p",
            "-c:s", "mov_text",
            path);
        return path;
    }

    private static async Task AssertFixtureTrackLayoutAsync(string path, string layout)
    {
        string json = await RunToolAsync(
            "ffprobe",
            $"{layout} fixture preflight",
            ["-v", "error", "-show_streams", "-of", "json", path]);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement streams = document.RootElement.GetProperty("streams");
        int audioCount = streams.EnumerateArray().Count(stream =>
            stream.GetProperty("codec_type").GetString() == "audio");
        int subtitleCount = streams.EnumerateArray().Count(stream =>
            stream.GetProperty("codec_type").GetString() == "subtitle");
        if (layout == "multi-audio")
        {
            Assert.Equal(2, audioCount);
        }
        else
        {
            Assert.Equal(1, subtitleCount);
        }
    }

    private static async Task<int?> ReadDisplayRotationAsync(string path)
    {
        string json = await RunToolAsync(
            "ffprobe",
            "display-rotation fixture preflight",
            [
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream_side_data=rotation:stream_tags=rotate",
                "-of", "json",
                path
            ]);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement stream = document.RootElement.GetProperty("streams")[0];
        if (stream.TryGetProperty("side_data_list", out JsonElement sideData))
        {
            foreach (JsonElement item in sideData.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out JsonElement rotation))
                {
                    return rotation.GetInt32();
                }
            }
        }

        if (stream.TryGetProperty("tags", out JsonElement tags) &&
            tags.TryGetProperty("rotate", out JsonElement tag) &&
            int.TryParse(tag.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return null;
    }

    private static Task<string> RunFfmpegAsync(string capability, params string[] arguments) =>
        RunToolAsync(
            "ffmpeg",
            capability,
            ["-hide_banner", "-loglevel", "error", "-y", .. arguments]);

    private static async Task<string> RunToolAsync(
        string executable,
        string capability,
        IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            Unavailable(capability, $"{executable} is not available: {exception.Message}");
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(FixtureTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Unavailable(capability, $"{executable} exceeded the {FixtureTimeout.TotalSeconds:0}-second fixture bound");
        }

        string standardOutput = await stdout;
        string standardError = await stderr;
        if (process.ExitCode != 0)
        {
            Unavailable(
                capability,
                $"{executable} exited {process.ExitCode}: {Condense(standardError)}");
        }

        return standardOutput;
    }

    private static async Task<VideoMediaInfo> ProbeOrUnavailableAsync(string path, string capability)
    {
        try
        {
            return await new WindowsVideoMediaProbe().ProbeAsync(path, CancellationToken.None);
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80D10002))
        {
            Unavailable(
                capability,
                $"real Windows media projection/frame decode failed: {exception}");
            throw;
        }
    }

    private static async Task<VideoOptimizationResult> OptimizeOrUnavailableAsync(
        string source,
        string destination,
        VideoOptimizationOptions options,
        string capability)
    {
        VideoOptimizationResult result = await CreateService().OptimizeAsync(
            CreateRequest(source, destination, options.KeepOutputWhenNotSmaller, options),
            null,
            CancellationToken.None);
        if (result.Outcome == VideoOptimizationOutcome.CodecNotFound)
        {
            Unavailable(
                capability,
                $"the real Windows media stack returned {result.Outcome}: {result.Detail}");
        }

        return result;
    }

    private static VideoOptimizerService CreateService() => new(
        new WindowsVideoMediaProbe(),
        new WindowsVideoTranscodeBackend(),
        new IdentityFileTransactions());

    private static VideoOptimizationRequest CreateRequest(
        string source,
        string destination,
        bool keepOutput,
        VideoOptimizationOptions? options = null)
    {
        var info = new FileInfo(source);
        info.Refresh();
        return new VideoOptimizationRequest(
            info.FullName,
            info.Length,
            info.LastWriteTimeUtc,
            destination,
            options ?? new VideoOptimizationOptions(
                VideoOptimizationPreset.Balanced,
                false,
                keepOutput));
    }

    private static void AssertPublished(VideoOptimizationResult result)
    {
        Assert.True(
            result.Outcome is VideoOptimizationOutcome.Succeeded or
                VideoOptimizationOutcome.KeptWithoutSaving,
            $"Expected a published output, but received {result.Outcome}: {result.Detail}");
        Assert.NotNull(result.OutputPath);
        Assert.NotNull(result.OutputMedia);
    }

    private static int NormalizeRotation(int value) => ((value % 360) + 360) % 360;

    private static void AssertVerifiedMp4(VideoMediaInfo media, bool expectAudio)
    {
        Assert.Equal("MP4", media.ContainerCodec);
        Assert.Equal("H.264", media.VideoCodec);
        Assert.Equal(1, media.VideoTrackCount);
        Assert.Equal(0, media.TimedMetadataTrackCount);
        if (expectAudio)
        {
            Assert.Equal(1, media.AudioTrackCount);
            Assert.Equal("AAC", media.AudioCodec);
        }
        else
        {
            Assert.Equal(0, media.AudioTrackCount);
            Assert.Null(media.AudioCodec);
        }
    }

    private void AssertNoTemporaryArtifacts()
    {
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_root),
            path => Path.GetFileName(path).Contains(".duplicates-video-", StringComparison.OrdinalIgnoreCase));
    }

    [DoesNotReturn]
    private static void Unavailable(string capability, string reason)
    {
        string message = $"UNAVAILABLE: {capability}: {Condense(reason)}";
        throw new UatUnavailableException(message);
    }

    [Fact]
    public async Task OptionalUat_ReportsUnavailableAsSkippedAndPreservesOtherFailures()
    {
        SkipException? skip = null;
        try
        {
            await RunOptionalUatAsync(
                () => Task.FromException(new UatUnavailableException("UNAVAILABLE: controlled media capability")));
        }
        catch (SkipException exception)
        {
            skip = exception;
        }

        Assert.NotNull(skip);
        Assert.Contains("controlled media capability", skip.Message, StringComparison.Ordinal);

        var failure = new InvalidOperationException("Controlled product failure.");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => RunOptionalUatAsync(
            () => Task.FromException(failure))));

        await Assert.ThrowsAsync<FileNotFoundException>(() => ProbeOrUnavailableAsync(
            Path.Combine(_root, "missing.mp4"), "controlled missing file"));
    }

    private async Task RunOptionalUatAsync(Func<Task> test)
    {
        try
        {
            await test();
        }
        catch (UatUnavailableException exception)
        {
            _output.WriteLine(exception.Message);
            throw SkipException.ForSkip(exception.Message);
        }
    }

    private static string Condense(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task<byte[]> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream);
    }

    private sealed class InlineProgress : IProgress<double>
    {
        private readonly Action<double> _report;

        public InlineProgress(Action<double> report)
        {
            _report = report;
        }

        public void Report(double value) => _report(value);
    }

    private sealed class UnusedFileActionService : IFileActionService
    {
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
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void OpenFile(string path) => throw new NotSupportedException();

        public void RevealInExplorer(string path) => throw new NotSupportedException();
    }

    private sealed class UatUnavailableException : Exception
    {
        public UatUnavailableException(string message)
            : base(message)
        {
        }
    }

}
