using System.Security.Cryptography;
using Duplicates.Models;
using Duplicates.Services;

namespace Duplicates.App.Tests;

public sealed partial class VideoOptimizerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates-VideoOptimizer",
        Guid.NewGuid().ToString("N"));

    public VideoOptimizerServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("relative.mp4", "output.mp4", 100, VideoOptimizationPreset.Balanced)]
    [InlineData("source.mp4", "source.mp4", 100, VideoOptimizationPreset.Balanced)]
    [InlineData("source.mp4", "nested\\output.mp4", 100, VideoOptimizationPreset.Balanced)]
    [InlineData("source.mp4", "output.mkv", 100, VideoOptimizationPreset.Balanced)]
    [InlineData("source.mp4", "output.mp4", -1, VideoOptimizationPreset.Balanced)]
    [InlineData("source.mp4", "output.mp4", 100, (VideoOptimizationPreset)999)]
    public async Task OptimizeAsync_RejectsInvalidRequestBeforeProbeOrBackend(
        string sourceName,
        string destinationName,
        long expectedLength,
        VideoOptimizationPreset preset)
    {
        string sourcePath = sourceName == "relative.mp4"
            ? sourceName
            : WriteSource(sourceName, 100);
        string destinationPath = Path.IsPathFullyQualified(sourceName)
            ? destinationName
            : Path.Combine(_root, destinationName);
        var probe = new FakeProbe();
        var backend = new FakeBackend();
        var service = CreateService(probe, backend);
        var request = new VideoOptimizationRequest(
            sourcePath,
            expectedLength,
            File.Exists(sourcePath) ? File.GetLastWriteTimeUtc(sourcePath) : DateTime.UtcNow,
            destinationPath,
            new VideoOptimizationOptions(preset, true, false));

        VideoOptimizationResult result = await service.OptimizeAsync(
            request,
            null,
            CancellationToken.None);

        Assert.Contains(result.Outcome, new[]
        {
            VideoOptimizationOutcome.UnsupportedInput,
            VideoOptimizationOutcome.InvalidProfile,
        });
        Assert.Equal(0, probe.CallCount);
        Assert.Equal(0, backend.CallCount);
    }

    [Fact]
    public async Task OptimizeAsync_RequestSnapshotMismatchReturnsSourceChangedBeforeProbe()
    {
        string sourcePath = WriteSource("changed.mp4", 500);
        var probe = new FakeProbe();
        var backend = new FakeBackend();
        var service = CreateService(probe, backend);
        VideoOptimizationRequest request = Request(sourcePath, expectedLength: 499);

        VideoOptimizationResult result = await service.OptimizeAsync(request, null, CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.SourceChanged, result.Outcome);
        Assert.Null(result.SourceMedia);
        Assert.Equal(0, probe.CallCount);
        Assert.Equal(0, backend.CallCount);
    }

    [Fact]
    public async Task OptimizeAsync_DestinationCollisionDoesNotCallBackendOrDeleteOccupant()
    {
        string sourcePath = WriteSource("source.mp4", 500);
        string destinationPath = WriteSource("source.optimized.mp4", 77);
        var probe = new FakeProbe { Source = SourceMedia() };
        var backend = new FakeBackend();
        var service = CreateService(probe, backend);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath, destinationPath: destinationPath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.DestinationCollision, result.Outcome);
        Assert.Equal(0, backend.CallCount);
        Assert.Equal(77, new FileInfo(destinationPath).Length);
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_SmallerVerifiedOutputPublishesAndPreservesSource()
    {
        string sourcePath = WriteSource("source.mp4", 1_000);
        byte[] beforeHash = SHA256.HashData(File.ReadAllBytes(sourcePath));
        DateTime beforeModified = File.GetLastWriteTimeUtc(sourcePath);
        var backend = new FakeBackend { OutputLength = 600, ProgressValues = [0, 40, 100] };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () => OutputMedia(backend.LastProfile!);
        var service = CreateService(probe, backend);
        var progress = new List<double>();

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            new InlineProgress(progress.Add),
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.Succeeded, result.Outcome);
        Assert.Equal(Path.Combine(_root, "source.optimized.mp4"), result.OutputPath);
        Assert.Equal(600, result.OutputSizeBytes);
        Assert.Equal(400, result.SavedBytes);
        Assert.NotNull(result.SourceMedia);
        Assert.NotNull(result.OutputMedia);
        Assert.Equal(beforeHash, SHA256.HashData(File.ReadAllBytes(sourcePath)));
        Assert.Equal(beforeModified, File.GetLastWriteTimeUtc(sourcePath));
        Assert.Equal(100, progress[^1]);
        Assert.Equal(progress.Order(), progress);
        Assert.All(progress, value => Assert.InRange(value, 0, 100));
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_DropsNonfiniteBackendProgressAndKeepsPublishedProgressBounded()
    {
        string sourcePath = WriteSource("progress.mp4", 1_000);
        var backend = new FakeBackend
        {
            OutputLength = 500,
            ProgressValues = [double.NaN, double.NegativeInfinity, 40, double.PositiveInfinity],
        };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () => OutputMedia(backend.LastProfile!);
        var service = CreateService(probe, backend);
        var progress = new List<double>();

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            new InlineProgress(progress.Add),
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.Succeeded, result.Outcome);
        Assert.All(progress, value => Assert.True(double.IsFinite(value)));
        Assert.All(progress, value => Assert.InRange(value, 0, 100));
        Assert.Equal(progress.Order(), progress);
        Assert.Equal(100, progress[^1]);
    }

    [Fact]
    public async Task OptimizeAsync_TemporaryOutputRetainsMp4ExtensionForWindowsMediaProjection()
    {
        string sourcePath = WriteSource("temp-extension.mp4", 1_000);
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () => OutputMedia(backend.LastProfile!);
        var service = CreateService(probe, backend);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.Succeeded, result.Outcome);
        Assert.Equal(".mp4", Path.GetExtension(backend.LastDestinationPath));
        Assert.Contains(
            ".duplicates-video-",
            Path.GetFileName(backend.LastDestinationPath),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, VideoOptimizationOutcome.NoSpaceSaving, false)]
    [InlineData(true, VideoOptimizationOutcome.KeptWithoutSaving, true)]
    public async Task OptimizeAsync_EqualOrLargerOutputHonorsKeepOverride(
        bool keepOutput,
        VideoOptimizationOutcome expectedOutcome,
        bool outputExists)
    {
        string sourcePath = WriteSource("efficient.mp4", 1_000);
        var backend = new FakeBackend { OutputLength = 1_000 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () => OutputMedia(backend.LastProfile!);
        var service = CreateService(probe, backend);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath, keepOutput: keepOutput),
            null,
            CancellationToken.None);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(outputExists, File.Exists(Path.Combine(_root, "efficient.optimized.mp4")));
        Assert.Empty(TemporaryArtifacts());
    }

    [Theory]
    [InlineData((int)VideoTranscodeBackendOutcome.CodecNotFound, VideoOptimizationOutcome.CodecNotFound, "A required Windows media codec was not found.")]
    [InlineData((int)VideoTranscodeBackendOutcome.InvalidProfile, VideoOptimizationOutcome.InvalidProfile, "Windows MediaTranscoder rejected the selected output profile.")]
    [InlineData((int)VideoTranscodeBackendOutcome.UnsupportedInput, VideoOptimizationOutcome.UnsupportedInput, "Windows MediaTranscoder rejected the source video.")]
    [InlineData((int)VideoTranscodeBackendOutcome.Failed, VideoOptimizationOutcome.Failed, "The Windows video transcode failed.")]
    public async Task OptimizeAsync_MapsEveryBackendFailureAndCleansPartial(
        int backendOutcomeValue,
        VideoOptimizationOutcome expectedOutcome,
        string expectedDetail)
    {
        string sourcePath = WriteSource("failure.mp4", 1_000);
        var backendOutcome = (VideoTranscodeBackendOutcome)backendOutcomeValue;
        var backend = new FakeBackend { Outcome = backendOutcome, OutputLength = 200 };
        var probe = new FakeProbe { Source = SourceMedia() };
        var service = CreateService(probe, backend);
        var progress = new List<double>();

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            new InlineProgress(progress.Add),
            CancellationToken.None);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedDetail, result.Detail);
        Assert.NotNull(result.SourceMedia);
        Assert.Null(result.OutputMedia);
        Assert.DoesNotContain(100, progress);
        Assert.Empty(TemporaryArtifacts());
    }

    [Theory]
    [InlineData("MP4", "HEVC", 10d, VideoOptimizationOutcome.VerificationFailed)]
    [InlineData("MP4", "H.264", 11.0000001d, VideoOptimizationOutcome.VerificationFailed)]
    [InlineData("MP4", "H.264", 11d, VideoOptimizationOutcome.Succeeded)]
    public async Task OptimizeAsync_VerifiesCodecAndInclusiveOneSecondDurationBoundary(
        string container,
        string videoCodec,
        double durationSeconds,
        VideoOptimizationOutcome expectedOutcome)
    {
        string sourcePath = WriteSource("verify.mp4", 1_000);
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () => OutputMedia(backend.LastProfile!) with
        {
            ContainerCodec = container,
            VideoCodec = videoCodec,
            Duration = TimeSpan.FromSeconds(durationSeconds)
        };
        var service = CreateService(probe, backend);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            CancellationToken.None);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedOutcome == VideoOptimizationOutcome.Succeeded, File.Exists(result.OutputPath));
        Assert.Empty(TemporaryArtifacts());
    }

    [Theory]
    [InlineData("coded-width")]
    [InlineData("display-width")]
    [InlineData("display-width-smallest-change")]
    [InlineData("aspect")]
    [InlineData("frame-rate")]
    [InlineData("audio-codec")]
    [InlineData("audio-bitrate")]
    [InlineData("audio-tracks")]
    [InlineData("video-tracks")]
    [InlineData("timed-track")]
    [InlineData("zero-duration")]
    public async Task OptimizeAsync_VerifiesTheCompleteOutputMediaContract(string violation)
    {
        string sourcePath = WriteSource($"verify-{violation}.mp4", 1_000);
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () =>
        {
            VideoMediaInfo output = OutputMedia(backend.LastProfile!);
            return violation switch
            {
                "coded-width" => output with { Width = output.Width - 2 },
                "display-width" => output with { SquarePixelDisplayWidth = output.SquarePixelDisplayWidth - 2 },
                "display-width-smallest-change" => output with { SquarePixelDisplayWidth = Math.BitIncrement(output.SquarePixelDisplayWidth) },
                "aspect" => output with { DisplayAspectRatio = 1 },
                "frame-rate" => output with { FramesPerSecondNumerator = output.FramesPerSecondNumerator - 1 },
                "audio-codec" => output with { AudioCodec = "AacAdts" },
                "audio-bitrate" => output with { AudioBitrate = backend.LastProfile!.AudioBitrate + 1 },
                "audio-tracks" => output with { AudioTrackCount = 2 },
                "video-tracks" => output with { VideoTrackCount = 2 },
                "timed-track" => output with { TimedMetadataTrackCount = 1 },
                "zero-duration" => output with { Duration = TimeSpan.Zero },
                _ => throw new InvalidOperationException(),
            };
        };
        var service = CreateService(probe, backend);
        var progress = new List<double>();

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            new InlineProgress(progress.Add),
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.VerificationFailed, result.Outcome);
        Assert.NotNull(result.SourceMedia);
        Assert.Null(result.OutputMedia);
        Assert.DoesNotContain(100, progress);
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_PreservesNoAudioAndAcceptsSemanticSquarePixelRatio()
    {
        string sourcePath = WriteSource("silent.mp4", 1_000);
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe
        {
            Source = SourceMedia() with
            {
                TotalBitrate = 6_000_000,
                AudioCodec = null,
                AudioBitrate = 0,
                AudioTrackCount = 0
            },
        };
        probe.OutputFactory = () => OutputMedia(backend.LastProfile!) with
        {
            PixelAspectRatioNumerator = 2,
            PixelAspectRatioDenominator = 2
        };
        var service = CreateService(probe, backend);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.Succeeded, result.Outcome);
        Assert.False(backend.LastProfile!.IncludeAudio);
        Assert.Equal(0, result.OutputMedia!.AudioTrackCount);
        Assert.Null(result.OutputMedia.AudioCodec);
    }

    [Fact]
    public async Task OptimizeAsync_SourceMutationAfterOutputProbeWinsOverNoSpaceSaving()
    {
        string sourcePath = WriteSource("mutated.mp4", 1_000);
        var backend = new FakeBackend { OutputLength = 1_100 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () =>
        {
            File.AppendAllText(sourcePath, "changed");
            return OutputMedia(backend.LastProfile!);
        };
        var service = CreateService(probe, backend);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.SourceChanged, result.Outcome);
        Assert.NotNull(result.SourceMedia);
        Assert.NotNull(result.OutputMedia);
        Assert.False(File.Exists(Path.Combine(_root, "mutated.optimized.mp4")));
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_EqualLengthAndTimestampIdentityReplacementReturnsSourceChanged()
    {
        string sourcePath = WriteSource("identity-swap.mp4", 1_000);
        DateTime snapshot = File.GetLastWriteTimeUtc(sourcePath);
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () =>
        {
            File.Delete(sourcePath);
            File.WriteAllBytes(sourcePath, Enumerable.Repeat((byte)217, 1_000).ToArray());
            File.SetLastWriteTimeUtc(sourcePath, snapshot);
            return OutputMedia(backend.LastProfile!);
        };
        var service = CreateService(probe, backend);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath, expectedLength: 1_000),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.SourceChanged, result.Outcome);
        Assert.False(File.Exists(Path.Combine(_root, "identity-swap.optimized.mp4")));
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_FinalCollisionRacePreservesOccupantAndCleansOwnedTemp()
    {
        string sourcePath = WriteSource("race.mp4", 1_000);
        string destinationPath = Path.Combine(_root, "race.optimized.mp4");
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () =>
        {
            File.WriteAllBytes(destinationPath, new byte[77]);
            return OutputMedia(backend.LastProfile!);
        };
        var service = CreateService(probe, backend);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath, destinationPath: destinationPath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.DestinationCollision, result.Outcome);
        Assert.Equal(77, new FileInfo(destinationPath).Length);
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_CancellationDuringBackendCleansOwnedTempAndRethrows()
    {
        string sourcePath = WriteSource("cancel.mp4", 1_000);
        using var cancellation = new CancellationTokenSource();
        var backend = new FakeBackend
        {
            OutputLength = 200,
            BeforeComplete = () => cancellation.Cancel(),
        };
        var probe = new FakeProbe { Source = SourceMedia() };
        var service = CreateService(probe, backend);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.OptimizeAsync(
            Request(sourcePath),
            null,
            cancellation.Token));

        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(Path.Combine(_root, "cancel.optimized.mp4")));
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_InvalidSourceProbeMapsToUnsupportedInputBeforeBackendOrTemp()
    {
        string sourcePath = WriteSource("unsupported.mp4", 1_000);
        var probe = new FakeProbe
        {
            SourceFailure = new InvalidDataException("Unsupported track layout."),
        };
        var backend = new FakeBackend();
        var service = CreateService(probe, backend);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.UnsupportedInput, result.Outcome);
        Assert.Equal("The source video metadata or track layout is unsupported.", result.Detail);
        Assert.Null(result.SourceMedia);
        Assert.Equal(0, backend.CallCount);
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_InvalidOutputProbeMapsToVerificationFailedAndCleansTemp()
    {
        string sourcePath = WriteSource("invalid-output.mp4", 1_000);
        var probe = new FakeProbe
        {
            Source = SourceMedia(),
            OutputFailure = new InvalidDataException("The output frame could not be decoded."),
        };
        var backend = new FakeBackend { OutputLength = 500 };
        var service = CreateService(probe, backend);
        var progress = new List<double>();

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            new InlineProgress(progress.Add),
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.VerificationFailed, result.Outcome);
        Assert.Equal("The temporary output could not be verified safely.", result.Detail);
        Assert.NotNull(result.SourceMedia);
        Assert.Null(result.OutputMedia);
        Assert.Equal(1, backend.CallCount);
        Assert.DoesNotContain(100, progress);
        Assert.False(File.Exists(Path.Combine(_root, "invalid-output.optimized.mp4")));
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_UnexpectedBackendExceptionUsesStableDetail()
    {
        string sourcePath = WriteSource("backend-exception.mp4", 1_000);
        var backend = new FakeBackend
        {
            Failure = new InvalidOperationException("sensitive backend exception text"),
        };
        var probe = new FakeProbe { Source = SourceMedia() };
        var service = CreateService(probe, backend);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.Failed, result.Outcome);
        Assert.Equal("Video optimization failed before publication.", result.Detail);
        Assert.DoesNotContain("sensitive", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_OutputMutationDuringProbeIsNotPublished()
    {
        string sourcePath = WriteSource("probe-mutation.mp4", 1_000);
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () =>
        {
            File.WriteAllBytes(backend.LastDestinationPath!, new byte[500]);
            return OutputMedia(backend.LastProfile!);
        };
        var service = CreateService(probe, backend);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.VerificationFailed, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Null(result.OutputMedia);
        Assert.False(File.Exists(Path.Combine(_root, "probe-mutation.optimized.mp4")));
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_VerifiedOutputMutationBeforePublicationIsNotPublished()
    {
        string sourcePath = WriteSource("publication-mutation.mp4", 1_000);
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () => OutputMedia(backend.LastProfile!);
        var transactions = new MutateBeforeMoveTransactions(new IdentityFileTransactions());
        var service = new VideoOptimizerService(probe, backend, transactions);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            CancellationToken.None);

        Assert.True(transactions.Mutated);
        Assert.Equal(VideoOptimizationOutcome.VerificationFailed, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Null(result.OutputMedia);
        Assert.False(File.Exists(Path.Combine(_root, "publication-mutation.optimized.mp4")));
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_PreCancelledTokenWinsBeforeProgressValidationOrFilesystem()
    {
        string sourcePath = Path.Combine(_root, "missing.mp4");
        var probe = new FakeProbe();
        var backend = new FakeBackend();
        var service = CreateService(probe, backend);
        var progress = new List<double>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var request = new VideoOptimizationRequest(
            sourcePath,
            123,
            DateTime.UtcNow,
            Path.Combine(_root, "missing.optimized.mp4"),
            new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, true, false));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.OptimizeAsync(
            request,
            new InlineProgress(progress.Add),
            cancellation.Token));

        Assert.Empty(progress);
        Assert.Equal(0, probe.CallCount);
        Assert.Equal(0, backend.CallCount);
        Assert.Empty(TemporaryArtifacts());
    }

    [Fact]
    public async Task OptimizeAsync_PostCommitCaptureFailurePreservesPublishedOutputForRecovery()
    {
        string sourcePath = WriteSource("post-commit.mp4", 1_000);
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () => OutputMedia(backend.LastProfile!);
        var transactions = new ThrowOnFinalCaptureTransactions(new IdentityFileTransactions());
        var service = new VideoOptimizerService(probe, backend, transactions);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(
            "The published output requires manual verification after an unexpected post-commit failure.",
            result.Detail);
        string recoveryPath = Assert.Single(result.RecoveryPaths);
        Assert.Equal(transactions.FinalPath, recoveryPath);
        Assert.True(File.Exists(recoveryPath));
        Assert.Equal(500, new FileInfo(recoveryPath).Length);
    }

    [Fact]
    public async Task OptimizeAsync_DualIdentityMoveRaceIsAmbiguousAndPreservesBothPaths()
    {
        string sourcePath = WriteSource("dual-link.mp4", 1_000);
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () => OutputMedia(backend.LastProfile!);
        var transactions = new DualOwnedMoveTransactions(new IdentityFileTransactions());
        var service = new VideoOptimizerService(probe, backend, transactions);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(
            new[] { transactions.TempPath!, transactions.DestinationPath! },
            result.RecoveryPaths,
            StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(transactions.TempPath));
    }

    [Fact]
    public async Task OptimizeAsync_UniqueTempCollisionRetriesWithoutTouchingExistingEntry()
    {
        string sourcePath = WriteSource("temp-collision.mp4", 1_000);
        string destinationPath = Path.Combine(_root, "temp-collision.optimized.mp4");
        string collisionPath = $"{destinationPath}.duplicates-video-collision.tmp.mp4";
        File.WriteAllBytes(collisionPath, new byte[77]);
        var tokens = new Queue<string>(["collision", "unique"]);
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () => OutputMedia(backend.LastProfile!);
        var service = new VideoOptimizerService(
            probe,
            backend,
            new IdentityFileTransactions(),
            () => tokens.Dequeue());

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath, destinationPath: destinationPath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.Succeeded, result.Outcome);
        Assert.Equal(77, new FileInfo(collisionPath).Length);
        Assert.True(File.Exists(destinationPath));
    }

    [Fact]
    public async Task OptimizeAsync_OwnedTempCreationRecoveryIsNotRetriedAsCollision()
    {
        string sourcePath = WriteSource("temp-creation-recovery.mp4", 1_000);
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        var transactions = new CreationRecoveryTransactions(new IdentityFileTransactions());
        var service = new VideoOptimizerService(probe, backend, transactions);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(transactions.RecoveryPath, Assert.Single(result.RecoveryPaths));
        Assert.Equal(1, transactions.CreateCallCount);
        Assert.Equal(0, backend.CallCount);
        Assert.True(File.Exists(transactions.RecoveryPath));
    }

    [Fact]
    public async Task OptimizeAsync_ForeignTempOccupantIsPreservedAndReportedForRecovery()
    {
        string sourcePath = WriteSource("foreign.mp4", 1_000);
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        var transactions = new ReplaceTempOnCaptureTransactions(
            new IdentityFileTransactions(),
            ReplaceWithForeignOccupant);
        var service = new VideoOptimizerService(probe, backend, transactions);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            CancellationToken.None);

        Assert.Equal(VideoOptimizationOutcome.RecoveryRequired, result.Outcome);
        string recoveryPath = Assert.Single(result.RecoveryPaths);
        Assert.Equal([9, 8, 7], File.ReadAllBytes(recoveryPath));
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task OptimizeAsync_CancelledForeignTempThrowsRecoveryCancellationWithExactPath()
    {
        string sourcePath = WriteSource("cancel-recovery.mp4", 1_000);
        using var cancellation = new CancellationTokenSource();
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        var transactions = new ReplaceTempOnCaptureTransactions(
            new IdentityFileTransactions(),
            path =>
            {
                ReplaceWithForeignOccupant(path);
                cancellation.Cancel();
            });
        var service = new VideoOptimizerService(probe, backend, transactions);

        VideoOptimizationCancellationException exception =
            await Assert.ThrowsAsync<VideoOptimizationCancellationException>(() => service.OptimizeAsync(
                Request(sourcePath),
                null,
                cancellation.Token));

        string recoveryPath = Assert.Single(exception.RecoveryPaths);
        Assert.Equal([9, 8, 7], File.ReadAllBytes(recoveryPath));
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task OptimizeAsync_CancellationRecoveryCarriesVerifiedMediaSnapshots()
    {
        string sourcePath = WriteSource("cancel-verified-recovery.mp4", 1_000);
        string destinationPath = Path.Combine(_root, "cancel-verified-recovery.optimized.mp4");
        using var cancellation = new CancellationTokenSource();
        var backend = new FakeBackend { OutputLength = 500 };
        VideoMediaInfo sourceMedia = SourceMedia();
        var probe = new FakeProbe { Source = sourceMedia };
        VideoMediaInfo? verifiedOutput = null;
        probe.OutputFactory = () => verifiedOutput = OutputMedia(backend.LastProfile!);
        var transactions = new CancelAfterOutputVerificationTransactions(
            new IdentityFileTransactions(),
            destinationPath,
            cancellation);
        var service = new VideoOptimizerService(probe, backend, transactions);

        VideoOptimizationCancellationException exception =
            await Assert.ThrowsAsync<VideoOptimizationCancellationException>(() => service.OptimizeAsync(
                Request(sourcePath, destinationPath: destinationPath),
                null,
                cancellation.Token));

        Assert.Same(sourceMedia, exception.SourceMedia);
        Assert.Same(verifiedOutput, exception.OutputMedia);
        Assert.Equal(transactions.TempPath, Assert.Single(exception.RecoveryPaths));
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task OptimizeAsync_CancellationAfterCommittedMoveReturnsCommittedSuccess()
    {
        string sourcePath = WriteSource("committed.mp4", 1_000);
        using var cancellation = new CancellationTokenSource();
        var backend = new FakeBackend { OutputLength = 500 };
        var probe = new FakeProbe { Source = SourceMedia() };
        probe.OutputFactory = () => OutputMedia(backend.LastProfile!);
        var transactions = new CancelAfterMoveTransactions(
            new IdentityFileTransactions(),
            cancellation);
        var service = new VideoOptimizerService(probe, backend, transactions);

        VideoOptimizationResult result = await service.OptimizeAsync(
            Request(sourcePath),
            null,
            cancellation.Token);

        Assert.Equal(VideoOptimizationOutcome.Succeeded, result.Outcome);
        Assert.True(File.Exists(result.OutputPath));
        Assert.True(cancellation.IsCancellationRequested);
    }

    private static VideoOptimizerService CreateService(FakeProbe probe, FakeBackend backend) =>
        new(probe, backend, new IdentityFileTransactions());

    private static VideoOptimizationRequest Request(
        string sourcePath,
        long? expectedLength = null,
        string? destinationPath = null,
        bool keepOutput = false) => new(
            sourcePath,
            expectedLength ?? new FileInfo(sourcePath).Length,
            File.GetLastWriteTimeUtc(sourcePath),
            destinationPath ?? Path.Combine(
                Path.GetDirectoryName(sourcePath)!,
                $"{Path.GetFileNameWithoutExtension(sourcePath)}.optimized.mp4"),
            new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, true, keepOutput));

    private string WriteSource(string name, int length)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Range(0, length).Select(index => (byte)index).ToArray());
        return path;
    }

    private string[] TemporaryArtifacts() => Directory
        .EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path)
            .Contains(".duplicates-video-", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    private static void ReplaceWithForeignOccupant(string path)
    {
        string ownedAside = path + ".owned";
        File.Move(path, ownedAside);
        File.WriteAllBytes(path, [9, 8, 7]);
        File.Delete(ownedAside);
    }

    private static VideoMediaInfo SourceMedia() => new(
        1920,
        1080,
        1920,
        1080,
        16d / 9,
        1,
        1,
        TimeSpan.FromSeconds(10),
        6_128_000,
        6_000_000,
        30000,
        1001,
        "H.264",
        "MP4",
        "AAC",
        128_000,
        1,
        1,
        0);

    private static VideoMediaInfo OutputMedia(VideoTranscodeProfile profile) => new(
        checked((int)profile.Width),
        checked((int)profile.Height),
        profile.Width,
        profile.Height,
        (double)profile.Width / profile.Height,
        profile.PixelAspectRatioNumerator,
        profile.PixelAspectRatioDenominator,
        TimeSpan.FromSeconds(10),
        profile.VideoBitrate + profile.AudioBitrate,
        profile.VideoBitrate,
        profile.FrameRateNumerator,
        profile.FrameRateDenominator,
        profile.VideoCodec,
        profile.ContainerCodec,
        profile.AudioCodec,
        profile.AudioBitrate,
        1,
        profile.IncludeAudio ? 1 : 0,
        0);

    private sealed class FakeProbe : IVideoMediaProbe
    {
        public VideoMediaInfo Source { get; init; } = SourceMedia();

        public Exception? SourceFailure { get; init; }

        public Exception? OutputFailure { get; init; }

        public Func<VideoMediaInfo>? OutputFactory { get; set; }

        public int CallCount { get; private set; }

        public Task<VideoMediaInfo> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (CallCount == 1 && SourceFailure is not null)
            {
                throw SourceFailure;
            }

            if (CallCount > 1 && OutputFailure is not null)
            {
                throw OutputFailure;
            }

            return Task.FromResult(CallCount == 1 ? Source : OutputFactory?.Invoke() ?? Source);
        }
    }

    private sealed class FakeBackend : IVideoTranscodeBackend
    {
        public VideoTranscodeBackendOutcome Outcome { get; init; } = VideoTranscodeBackendOutcome.Succeeded;

        public int OutputLength { get; init; } = 500;

        public IReadOnlyList<double> ProgressValues { get; init; } = [100];

        public Action? BeforeComplete { get; init; }

        public Exception? Failure { get; init; }

        public int CallCount { get; private set; }

        public VideoTranscodeProfile? LastProfile { get; private set; }

        public string? LastDestinationPath { get; private set; }

        public async Task<VideoTranscodeBackendResult> TranscodeAsync(
            string sourcePath,
            string destinationPath,
            VideoTranscodeProfile profile,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastProfile = profile;
            LastDestinationPath = destinationPath;
            if (Failure is not null)
            {
                throw Failure;
            }

            await File.WriteAllBytesAsync(destinationPath, new byte[OutputLength], CancellationToken.None);
            foreach (double value in ProgressValues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(value);
            }

            BeforeComplete?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return new VideoTranscodeBackendResult(Outcome, $"Backend outcome: {Outcome}");
        }
    }

    private sealed class InlineProgress : IProgress<double>
    {
        private readonly Action<double> _callback;

        public InlineProgress(Action<double> callback)
        {
            _callback = callback;
        }

        public void Report(double value) => _callback(value);
    }

    private sealed class CancelAfterMoveTransactions : IIdentityFileTransactions
    {
        private readonly IIdentityFileTransactions _inner;
        private readonly CancellationTokenSource _cancellation;

        public CancelAfterMoveTransactions(
        IIdentityFileTransactions inner,
        CancellationTokenSource cancellation)
        {
            _inner = inner;
            _cancellation = cancellation;
        }

        public IdentityTrackedFile Capture(string path) => _inner.Capture(path);

        public IdentityTrackedFile CreateOwnedNew(string destinationPath) =>
            _inner.CreateOwnedNew(destinationPath);

        public Task<IdentityTrackedFile> CopyAndFlushAsync(
            IdentityTrackedFile source,
            IdentityTrackedFile destination,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            _inner.CopyAndFlushAsync(source, destination, progress, cancellationToken);

        public IDisposable GuardOwnedPath(IdentityTrackedFile file) => _inner.GuardOwnedPath(file);

        public IDisposable GuardSourceSnapshot(IdentityTrackedFile source) =>
            _inner.GuardSourceSnapshot(source);

        public bool EntryExistsCaseInsensitive(string path) => _inner.EntryExistsCaseInsensitive(path);

        public IdentityMoveResult MoveNoOverwrite(IdentityTrackedFile source, string destinationPath)
        {
            IdentityMoveResult result = _inner.MoveNoOverwrite(source, destinationPath);
            _cancellation.Cancel();
            return result;
        }

        public IdentityMoveResult MoveSourceNoOverwrite(
            IdentityTrackedFile source,
            string destinationPath)
        {
            IdentityMoveResult result = _inner.MoveSourceNoOverwrite(source, destinationPath);
            _cancellation.Cancel();
            return result;
        }

        public void DeleteOwned(IdentityTrackedFile file) => _inner.DeleteOwned(file);

        public IdentityPathProbe Probe(string path) => _inner.Probe(path);
    }

    private sealed class CancelAfterOutputVerificationTransactions : IIdentityFileTransactions
    {
        private readonly IIdentityFileTransactions _inner;
        private readonly string _destinationPath;
        private readonly CancellationTokenSource _cancellation;

        public CancelAfterOutputVerificationTransactions(
        IIdentityFileTransactions inner,
        string destinationPath,
        CancellationTokenSource cancellation)
        {
            _inner = inner;
            _destinationPath = destinationPath;
            _cancellation = cancellation;
        }

        private int _destinationChecks;

        public string? TempPath { get; private set; }

        public IdentityTrackedFile Capture(string path) => _inner.Capture(path);

        public IdentityTrackedFile CreateOwnedNew(string path)
        {
            IdentityTrackedFile created = _inner.CreateOwnedNew(path);
            TempPath = created.Path;
            return created;
        }

        public Task<IdentityTrackedFile> CopyAndFlushAsync(
            IdentityTrackedFile source,
            IdentityTrackedFile destination,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            _inner.CopyAndFlushAsync(source, destination, progress, cancellationToken);

        public IDisposable GuardOwnedPath(IdentityTrackedFile file) => _inner.GuardOwnedPath(file);

        public IDisposable GuardSourceSnapshot(IdentityTrackedFile source) =>
            _inner.GuardSourceSnapshot(source);

        public bool EntryExistsCaseInsensitive(string path)
        {
            if (string.Equals(path, _destinationPath, StringComparison.OrdinalIgnoreCase) &&
                ++_destinationChecks == 2)
            {
                ReplaceWithForeignOccupant(TempPath!);
                _cancellation.Cancel();
            }

            return _inner.EntryExistsCaseInsensitive(path);
        }

        public IdentityMoveResult MoveNoOverwrite(IdentityTrackedFile source, string destination) =>
            _inner.MoveNoOverwrite(source, destination);

        public IdentityMoveResult MoveSourceNoOverwrite(
            IdentityTrackedFile source,
            string destination) => _inner.MoveSourceNoOverwrite(source, destination);

        public void DeleteOwned(IdentityTrackedFile file) => _inner.DeleteOwned(file);

        public IdentityPathProbe Probe(string path) => _inner.Probe(path);
    }

    private sealed class ReplaceTempOnCaptureTransactions : IIdentityFileTransactions
    {
        private readonly IIdentityFileTransactions _inner;
        private readonly Action<string> _replace;

        public ReplaceTempOnCaptureTransactions(
        IIdentityFileTransactions inner,
        Action<string> replace)
        {
            _inner = inner;
            _replace = replace;
        }

        private bool _replaced;

        public IdentityTrackedFile Capture(string path)
        {
            if (!_replaced && Path.GetFileName(path)
                .Contains(".duplicates-video-", StringComparison.OrdinalIgnoreCase))
            {
                _replaced = true;
                _replace(path);
            }

            return _inner.Capture(path);
        }

        public IdentityTrackedFile CreateOwnedNew(string destinationPath) =>
            _inner.CreateOwnedNew(destinationPath);

        public Task<IdentityTrackedFile> CopyAndFlushAsync(
            IdentityTrackedFile source,
            IdentityTrackedFile destination,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            _inner.CopyAndFlushAsync(source, destination, progress, cancellationToken);

        public IDisposable GuardOwnedPath(IdentityTrackedFile file) => _inner.GuardOwnedPath(file);

        public IDisposable GuardSourceSnapshot(IdentityTrackedFile source) =>
            _inner.GuardSourceSnapshot(source);

        public bool EntryExistsCaseInsensitive(string path) => _inner.EntryExistsCaseInsensitive(path);

        public IdentityMoveResult MoveNoOverwrite(IdentityTrackedFile source, string destinationPath) =>
            _inner.MoveNoOverwrite(source, destinationPath);

        public IdentityMoveResult MoveSourceNoOverwrite(
            IdentityTrackedFile source,
            string destinationPath) => _inner.MoveSourceNoOverwrite(source, destinationPath);

        public void DeleteOwned(IdentityTrackedFile file) => _inner.DeleteOwned(file);

        public IdentityPathProbe Probe(string path) => _inner.Probe(path);
    }

    private sealed class CreationRecoveryTransactions : IIdentityFileTransactions
    {
        private readonly IIdentityFileTransactions _inner;

        public CreationRecoveryTransactions(IIdentityFileTransactions inner)
        {
            _inner = inner;
        }

        public int CreateCallCount { get; private set; }

        public string RecoveryPath { get; private set; } = string.Empty;

        public IdentityTrackedFile Capture(string path) => _inner.Capture(path);

        public IdentityTrackedFile CreateOwnedNew(string destinationPath)
        {
            CreateCallCount++;
            if (CreateCallCount != 1)
            {
                return _inner.CreateOwnedNew(destinationPath);
            }

            RecoveryPath = destinationPath;
            File.WriteAllBytes(destinationPath, [4, 2]);
            throw new IdentityOwnedCreationRecoveryException(
                destinationPath,
                new IOException("Injected owned creation validation failure."),
                new IOException("Injected owned creation cleanup failure."));
        }

        public Task<IdentityTrackedFile> CopyAndFlushAsync(
            IdentityTrackedFile source,
            IdentityTrackedFile destination,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            _inner.CopyAndFlushAsync(source, destination, progress, cancellationToken);

        public IDisposable GuardOwnedPath(IdentityTrackedFile file) => _inner.GuardOwnedPath(file);

        public IDisposable GuardSourceSnapshot(IdentityTrackedFile source) =>
            _inner.GuardSourceSnapshot(source);

        public bool EntryExistsCaseInsensitive(string path) => _inner.EntryExistsCaseInsensitive(path);

        public IdentityMoveResult MoveNoOverwrite(IdentityTrackedFile source, string destinationPath) =>
            _inner.MoveNoOverwrite(source, destinationPath);

        public IdentityMoveResult MoveSourceNoOverwrite(
            IdentityTrackedFile source,
            string destinationPath) => _inner.MoveSourceNoOverwrite(source, destinationPath);

        public void DeleteOwned(IdentityTrackedFile file) => _inner.DeleteOwned(file);

        public IdentityPathProbe Probe(string path) => _inner.Probe(path);
    }

    private sealed class MutateBeforeMoveTransactions : IIdentityFileTransactions
    {
        private readonly IIdentityFileTransactions _inner;

        public MutateBeforeMoveTransactions(IIdentityFileTransactions inner)
        {
            _inner = inner;
        }

        public bool Mutated { get; private set; }

        public IdentityTrackedFile Capture(string path) => _inner.Capture(path);

        public IdentityTrackedFile CreateOwnedNew(string destinationPath) =>
            _inner.CreateOwnedNew(destinationPath);

        public Task<IdentityTrackedFile> CopyAndFlushAsync(
            IdentityTrackedFile source,
            IdentityTrackedFile destination,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            _inner.CopyAndFlushAsync(source, destination, progress, cancellationToken);

        public IDisposable GuardOwnedPath(IdentityTrackedFile file) => _inner.GuardOwnedPath(file);

        public IDisposable GuardSourceSnapshot(IdentityTrackedFile source) =>
            _inner.GuardSourceSnapshot(source);

        public bool EntryExistsCaseInsensitive(string path) => _inner.EntryExistsCaseInsensitive(path);

        public IdentityMoveResult MoveNoOverwrite(IdentityTrackedFile source, string destinationPath)
        {
            Mutate(source);
            return _inner.MoveNoOverwrite(source, destinationPath);
        }

        public IdentityMoveResult MoveSourceNoOverwrite(
            IdentityTrackedFile source,
            string destinationPath)
        {
            Mutate(source);
            return _inner.MoveSourceNoOverwrite(source, destinationPath);
        }

        public void DeleteOwned(IdentityTrackedFile file) => _inner.DeleteOwned(file);

        public IdentityPathProbe Probe(string path) => _inner.Probe(path);

        private void Mutate(IdentityTrackedFile source)
        {
            if (Mutated)
            {
                return;
            }

            Mutated = true;
            File.WriteAllBytes(source.Path, Enumerable.Repeat((byte)0xA5, checked((int)source.Length)).ToArray());
            File.SetLastWriteTimeUtc(source.Path, source.ModifiedUtc.AddSeconds(5));
        }
    }

    private sealed class ThrowOnFinalCaptureTransactions : IIdentityFileTransactions
    {
        private readonly IIdentityFileTransactions _inner;

        public ThrowOnFinalCaptureTransactions(IIdentityFileTransactions inner)
        {
            _inner = inner;
        }

        public string? FinalPath { get; private set; }

        public IdentityTrackedFile Capture(string path)
        {
            if (FinalPath is not null && string.Equals(path, FinalPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Injected final capture failure.");
            }

            return _inner.Capture(path);
        }

        public IdentityTrackedFile CreateOwnedNew(string destinationPath) => _inner.CreateOwnedNew(destinationPath);

        public Task<IdentityTrackedFile> CopyAndFlushAsync(
            IdentityTrackedFile source,
            IdentityTrackedFile destination,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            _inner.CopyAndFlushAsync(source, destination, progress, cancellationToken);

        public IDisposable GuardOwnedPath(IdentityTrackedFile file) => _inner.GuardOwnedPath(file);

        public IDisposable GuardSourceSnapshot(IdentityTrackedFile source) => _inner.GuardSourceSnapshot(source);

        public bool EntryExistsCaseInsensitive(string path) => _inner.EntryExistsCaseInsensitive(path);

        public IdentityMoveResult MoveNoOverwrite(IdentityTrackedFile source, string destinationPath)
        {
            IdentityMoveResult result = _inner.MoveNoOverwrite(source, destinationPath);
            FinalPath = destinationPath;
            return result;
        }

        public IdentityMoveResult MoveSourceNoOverwrite(IdentityTrackedFile source, string destinationPath)
        {
            IdentityMoveResult result = _inner.MoveSourceNoOverwrite(source, destinationPath);
            FinalPath = destinationPath;
            return result;
        }

        public void DeleteOwned(IdentityTrackedFile file) => _inner.DeleteOwned(file);

        public IdentityPathProbe Probe(string path) => _inner.Probe(path);
    }

    private sealed class DualOwnedMoveTransactions : IIdentityFileTransactions
    {
        private readonly IIdentityFileTransactions _inner;

        public DualOwnedMoveTransactions(IIdentityFileTransactions inner)
        {
            _inner = inner;
        }

        private FileSystemIdentity? _identity;

        public string? TempPath { get; private set; }

        public string? DestinationPath { get; private set; }

        public IdentityTrackedFile Capture(string path) => _inner.Capture(path);

        public IdentityTrackedFile CreateOwnedNew(string destinationPath)
        {
            IdentityTrackedFile created = _inner.CreateOwnedNew(destinationPath);
            TempPath = created.Path;
            _identity = created.Identity;
            return created;
        }

        public Task<IdentityTrackedFile> CopyAndFlushAsync(
            IdentityTrackedFile source,
            IdentityTrackedFile destination,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            _inner.CopyAndFlushAsync(source, destination, progress, cancellationToken);

        public IDisposable GuardOwnedPath(IdentityTrackedFile file) => _inner.GuardOwnedPath(file);

        public IDisposable GuardSourceSnapshot(IdentityTrackedFile source) => _inner.GuardSourceSnapshot(source);

        public bool EntryExistsCaseInsensitive(string path) => _inner.EntryExistsCaseInsensitive(path);

        public IdentityMoveResult MoveNoOverwrite(IdentityTrackedFile source, string destinationPath)
        {
            DestinationPath = destinationPath;
            throw new IdentityMoveException(
                "Injected dual-owned move race.",
                IdentityMoveCommitState.NotCommitted,
                source.Path,
                destinationPath,
                source.Identity);
        }

        public IdentityMoveResult MoveSourceNoOverwrite(IdentityTrackedFile source, string destinationPath)
        {
            DestinationPath = destinationPath;
            throw new IdentityMoveException(
                "Injected dual-owned move race.",
                IdentityMoveCommitState.NotCommitted,
                source.Path,
                destinationPath,
                source.Identity);
        }

        public void DeleteOwned(IdentityTrackedFile file) => _inner.DeleteOwned(file);

        public IdentityPathProbe Probe(string path)
        {
            if (DestinationPath is not null &&
                string.Equals(path, DestinationPath, StringComparison.OrdinalIgnoreCase))
            {
                return new IdentityPathProbe(IdentityPathState.Present, _identity);
            }

            return _inner.Probe(path);
        }
    }
}
