namespace Duplicates.Services;

internal sealed class VideoOptimizerService : IVideoOptimizerService
{
    private const int MaximumTempNameAttempts = 16;
    private readonly IVideoMediaProbe _probe;
    private readonly IVideoTranscodeBackend _backend;
    private readonly IIdentityFileTransactions _transactions;
    private readonly Func<string> _tempTokenFactory;

    public VideoOptimizerService(
        IVideoMediaProbe probe,
        IVideoTranscodeBackend backend,
        IIdentityFileTransactions transactions,
        Func<string>? tempTokenFactory = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _tempTokenFactory = tempTokenFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    public async Task<VideoOptimizationResult> OptimizeAsync(
        VideoOptimizationRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var reporter = new MonotonicProgress(progress);
        reporter.Report(0);
        if (!TryValidateRequest(request, out string sourcePath, out string destinationPath, out VideoOptimizationResult? invalid))
        {
            return invalid!;
        }

        if (!Enum.IsDefined(request.Options.Preset))
        {
            return Result(
                VideoOptimizationOutcome.InvalidProfile,
                sourcePath,
                detail: "The selected video optimization preset is invalid.");
        }

        IdentityTrackedFile source;
        try
        {
            source = _transactions.Capture(sourcePath);
        }
        catch (FileNotFoundException)
        {
            return SourceChanged(sourcePath);
        }
        catch (DirectoryNotFoundException)
        {
            return SourceChanged(sourcePath);
        }
        catch (IdentityTransactionException)
        {
            return Result(
                VideoOptimizationOutcome.UnsupportedInput,
                sourcePath,
                detail: "The source file could not be opened safely.");
        }
        catch (Exception)
        {
            return Result(
                VideoOptimizationOutcome.Failed,
                sourcePath,
                detail: "The source file could not be inspected safely.");
        }

        if (!MatchesRequest(source, request))
        {
            return SourceChanged(sourcePath);
        }

        reporter.Report(2);
        VideoMediaInfo? sourceMedia = null;
        VideoMediaInfo? outputMedia = null;
        IdentityTrackedFile? artifact = null;
        bool publicationCommitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                sourceMedia = await _probe.ProbeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return Result(
                    VideoOptimizationOutcome.UnsupportedInput,
                    sourcePath,
                    detail: "The source video metadata or track layout is unsupported.");
            }

            using (_transactions.GuardSourceSnapshot(source))
            {
            }

            reporter.Report(4);
            VideoProfileBuildResult profileResult = VideoOptimizationProfilePolicy.Build(
                sourceMedia,
                request.Options);
            if (profileResult.Profile is null)
            {
                return Result(
                    profileResult.FailureOutcome ?? VideoOptimizationOutcome.InvalidProfile,
                    sourcePath,
                    sourceMedia: sourceMedia,
                    detail: profileResult.Detail);
            }

            VideoTranscodeProfile profile = profileResult.Profile;
            artifact = CreateUniqueTemp(destinationPath);
            reporter.Report(5);

            if (_transactions.EntryExistsCaseInsensitive(destinationPath))
            {
                return FinishAfterCleanup(
                    VideoOptimizationOutcome.DestinationCollision,
                    sourcePath,
                    sourceMedia,
                    null,
                    null,
                    0,
                    "The reserved output path is already occupied.",
                    artifact);
            }

            VideoTranscodeBackendResult backendResult;
            using (_transactions.GuardSourceSnapshot(source))
            using (_transactions.GuardOwnedPath(artifact))
            {
                var backendProgress = new InlineProgress(value => reporter.Report(5 + (Math.Clamp(value, 0, 100) * 0.85)));
                backendResult = await _backend.TranscodeAsync(
                    sourcePath,
                    artifact.Path,
                    profile,
                    backendProgress,
                    cancellationToken).ConfigureAwait(false);
            }

            if (backendResult.Outcome != VideoTranscodeBackendOutcome.Succeeded)
            {
                return FinishAfterCleanup(
                    MapBackendOutcome(backendResult.Outcome),
                    sourcePath,
                    sourceMedia,
                    null,
                    null,
                    0,
                    DetailForBackendOutcome(backendResult.Outcome),
                    artifact);
            }

            IdentityTrackedFile completedArtifact = _transactions.Capture(artifact.Path);
            cancellationToken.ThrowIfCancellationRequested();
            if (completedArtifact.Identity != artifact.Identity)
            {
                return Recovery(
                    sourcePath,
                    sourceMedia,
                    null,
                    "The temporary output identity changed after transcoding.",
                    [artifact.Path]);
            }

            artifact = completedArtifact with { Identity = artifact.Identity };
            bool outputVerified;
            string verificationDetail;
            try
            {
                using (_transactions.GuardSourceSnapshot(artifact))
                {
                    VideoMediaInfo candidate = await _probe.ProbeAsync(
                        artifact.Path,
                        cancellationToken).ConfigureAwait(false);
                    outputVerified = TryVerifyOutput(sourceMedia, candidate, profile, out verificationDetail);
                    outputMedia = outputVerified ? candidate : null;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                outputVerified = false;
                verificationDetail = "The temporary output could not be verified safely.";
            }

            if (!outputVerified)
            {
                return FinishAfterCleanup(
                    VideoOptimizationOutcome.VerificationFailed,
                    sourcePath,
                    sourceMedia,
                    null,
                    artifact.Length,
                    0,
                    verificationDetail,
                    artifact);
            }

            reporter.Report(95);
            try
            {
                using (_transactions.GuardSourceSnapshot(source))
                {
                }
            }
            catch (IdentitySourceChangedException)
            {
                return FinishAfterCleanup(
                    VideoOptimizationOutcome.SourceChanged,
                    sourcePath,
                    sourceMedia,
                    outputMedia,
                    artifact.Length,
                    0,
                    "The source file changed before the output could be published.",
                    artifact);
            }

            reporter.Report(97);
            long savedBytes = request.ExpectedLength - artifact.Length;
            bool outputIsNotSmaller = artifact.Length >= request.ExpectedLength;
            if (outputIsNotSmaller && !request.Options.KeepOutputWhenNotSmaller)
            {
                return FinishAfterCleanup(
                    VideoOptimizationOutcome.NoSpaceSaving,
                    sourcePath,
                    sourceMedia,
                    outputMedia,
                    artifact.Length,
                    savedBytes,
                    "The verified output was not smaller than the source and was removed.",
                    artifact);
            }

            reporter.Report(98);
            if (_transactions.EntryExistsCaseInsensitive(destinationPath))
            {
                return FinishAfterCleanup(
                    VideoOptimizationOutcome.DestinationCollision,
                    sourcePath,
                    sourceMedia,
                    outputMedia,
                    artifact.Length,
                    savedBytes,
                    "The reserved output path became occupied before publication.",
                    artifact);
            }

            IdentityMoveResult publication;
            using (_transactions.GuardSourceSnapshot(source))
            {
                reporter.Report(99);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    publication = MoveWithResolution(artifact, destinationPath);
                }
                catch (IdentitySourceChangedException)
                {
                    return FinishAfterCleanup(
                        VideoOptimizationOutcome.VerificationFailed,
                        sourcePath,
                        sourceMedia,
                        null,
                        artifact.Length,
                        savedBytes,
                        "The temporary output changed after verification.",
                        artifact);
                }
            }

            if (publication.CommitState == IdentityMoveCommitState.NotCommitted)
            {
                VideoOptimizationOutcome outcome = publication.DestinationOccupied
                    ? VideoOptimizationOutcome.DestinationCollision
                    : VideoOptimizationOutcome.Failed;
                return FinishAfterCleanup(
                    outcome,
                    sourcePath,
                    sourceMedia,
                    outputMedia,
                    artifact.Length,
                    savedBytes,
                    publication.DestinationOccupied
                        ? "The reserved output path became occupied during publication."
                        : "The optimized output could not be published.",
                    artifact);
            }

            if (publication.CommitState == IdentityMoveCommitState.Indeterminate)
            {
                return Recovery(
                    sourcePath,
                    sourceMedia,
                    outputMedia,
                    "The optimized output move did not reach a verifiable terminal state.",
                    [artifact.Path, destinationPath]);
            }

            artifact = publication.File;
            publicationCommitted = true;
            IdentityPathProbe finalProbe = _transactions.Probe(destinationPath);
            if (finalProbe.State != IdentityPathState.Present || finalProbe.Identity != artifact.Identity)
            {
                return Recovery(
                    sourcePath,
                    sourceMedia,
                    outputMedia,
                    "The published output identity could not be verified.",
                    [destinationPath]);
            }

            IdentityTrackedFile final = _transactions.Capture(destinationPath);
            if (final.Identity != artifact.Identity)
            {
                return Recovery(
                    sourcePath,
                    sourceMedia,
                    outputMedia,
                    "The published output was replaced before final verification.",
                    [destinationPath]);
            }

            artifact = null;
            reporter.Report(100);
            return Result(
                outputIsNotSmaller
                    ? VideoOptimizationOutcome.KeptWithoutSaving
                    : VideoOptimizationOutcome.Succeeded,
                sourcePath,
                destinationPath,
                final.Length,
                sourceMedia,
                outputMedia,
                request.ExpectedLength - final.Length,
                outputIsNotSmaller
                    ? "The verified output was kept even though it did not save space."
                    : "The verified optimized video was published.");
        }
        catch (OperationCanceledException exception)
        {
            if (publicationCommitted)
            {
                return Recovery(
                    sourcePath,
                    sourceMedia,
                    outputMedia,
                    "The published output requires manual verification after a post-commit cancellation.",
                    [destinationPath]);
            }

            IReadOnlyList<string> recoveryPaths = CleanupOwned(artifact);
            if (recoveryPaths.Count != 0)
            {
                throw new VideoOptimizationCancellationException(
                    recoveryPaths,
                    sourceMedia,
                    outputMedia,
                    cancellationToken,
                    exception);
            }

            throw;
        }
        catch (IdentitySourceChangedException)
        {
            if (publicationCommitted)
            {
                return Recovery(
                    sourcePath,
                    sourceMedia,
                    outputMedia,
                    "The published output requires manual verification after a post-commit source check failure.",
                    [destinationPath]);
            }

            return FinishAfterCleanup(
                VideoOptimizationOutcome.SourceChanged,
                sourcePath,
                sourceMedia,
                outputMedia,
                artifact?.Length,
                0,
                "The source file changed before the output could be published.",
                artifact);
        }
        catch (IdentityOwnedCreationRecoveryException exception)
        {
            return Recovery(
                sourcePath,
                sourceMedia,
                outputMedia,
                "A newly created temporary output requires manual recovery.",
                [exception.RecoveryPath]);
        }
        catch (Exception)
        {
            if (publicationCommitted)
            {
                return Recovery(
                    sourcePath,
                    sourceMedia,
                    outputMedia,
                    "The published output requires manual verification after an unexpected post-commit failure.",
                    [destinationPath]);
            }

            IReadOnlyList<string> recoveryPaths = CleanupOwned(artifact);
            return recoveryPaths.Count == 0
                ? Result(
                    VideoOptimizationOutcome.Failed,
                    sourcePath,
                    outputSizeBytes: artifact?.Length,
                    sourceMedia: sourceMedia,
                    outputMedia: outputMedia,
                    detail: "Video optimization failed before publication.")
                : Recovery(
                    sourcePath,
                    sourceMedia,
                    outputMedia,
                    "Video optimization failed and the temporary output requires manual recovery.",
                    recoveryPaths);
        }
    }

    private IdentityTrackedFile CreateUniqueTemp(string destinationPath)
    {
        for (int attempt = 0; attempt < MaximumTempNameAttempts; attempt++)
        {
            string candidate = $"{destinationPath}.duplicates-video-{_tempTokenFactory()}.tmp.mp4";
            if (_transactions.EntryExistsCaseInsensitive(candidate))
            {
                continue;
            }

            try
            {
                return _transactions.CreateOwnedNew(candidate);
            }
            catch (IdentityOwnedCreationRecoveryException)
            {
                throw;
            }
            catch (IOException) when (_transactions.EntryExistsCaseInsensitive(candidate))
            {
            }
        }

        throw new IOException("A unique sibling video temporary file could not be created.");
    }

    private static string DetailForBackendOutcome(VideoTranscodeBackendOutcome outcome) => outcome switch
    {
        VideoTranscodeBackendOutcome.CodecNotFound => "A required Windows media codec was not found.",
        VideoTranscodeBackendOutcome.InvalidProfile => "Windows MediaTranscoder rejected the selected output profile.",
        VideoTranscodeBackendOutcome.UnsupportedInput => "Windows MediaTranscoder rejected the source video.",
        VideoTranscodeBackendOutcome.Failed => "The Windows video transcode failed.",
        _ => "The Windows video transcode failed.",
    };

    private VideoOptimizationResult FinishAfterCleanup(
        VideoOptimizationOutcome outcome,
        string sourcePath,
        VideoMediaInfo? sourceMedia,
        VideoMediaInfo? outputMedia,
        long? outputSizeBytes,
        long savedBytes,
        string detail,
        IdentityTrackedFile? artifact)
    {
        IReadOnlyList<string> recoveryPaths = CleanupOwned(artifact);
        return recoveryPaths.Count == 0
            ? Result(
                outcome,
                sourcePath,
                outputSizeBytes: outputSizeBytes,
                sourceMedia: sourceMedia,
                outputMedia: outputMedia,
                savedBytes: savedBytes,
                detail: detail)
            : Recovery(sourcePath, sourceMedia, outputMedia, detail, recoveryPaths);
    }

    private IReadOnlyList<string> CleanupOwned(IdentityTrackedFile? artifact)
    {
        if (artifact is null)
        {
            return [];
        }

        IdentityPathProbe before = _transactions.Probe(artifact.Path);
        if (before.State == IdentityPathState.Missing)
        {
            return [];
        }

        if (before.State != IdentityPathState.Present || before.Identity != artifact.Identity)
        {
            return [artifact.Path];
        }

        try
        {
            _transactions.DeleteOwned(artifact);
        }
        catch
        {
            return [artifact.Path];
        }

        return _transactions.Probe(artifact.Path).State == IdentityPathState.Missing
            ? []
            : [artifact.Path];
    }

    private IdentityMoveResult MoveWithResolution(
        IdentityTrackedFile source,
        string destinationPath)
    {
        try
        {
            return _transactions.MoveSourceNoOverwrite(source, destinationPath);
        }
        catch (IdentityMoveException exception)
        {
            IdentityPathProbe sourceProbe = _transactions.Probe(source.Path);
            IdentityPathProbe destinationProbe = _transactions.Probe(destinationPath);
            bool sourceOwned = sourceProbe.State == IdentityPathState.Present &&
                sourceProbe.Identity == source.Identity;
            bool destinationOwned = destinationProbe.State == IdentityPathState.Present &&
                destinationProbe.Identity == source.Identity;
            bool destinationOccupied = destinationProbe.State == IdentityPathState.Present &&
                !destinationOwned;
            if (sourceOwned && destinationOwned)
            {
                return new IdentityMoveResult(IdentityMoveCommitState.Indeterminate, source);
            }

            if (destinationOwned && !sourceOwned)
            {
                return new IdentityMoveResult(
                    IdentityMoveCommitState.Committed,
                    source with { Path = destinationPath });
            }

            if (sourceOwned && exception.CommitState == IdentityMoveCommitState.NotCommitted)
            {
                return new IdentityMoveResult(
                    IdentityMoveCommitState.NotCommitted,
                    source,
                    destinationOccupied);
            }

            if (sourceOwned && destinationProbe.State == IdentityPathState.Missing)
            {
                return new IdentityMoveResult(IdentityMoveCommitState.NotCommitted, source);
            }

            return new IdentityMoveResult(IdentityMoveCommitState.Indeterminate, source);
        }
    }

    private static bool TryVerifyOutput(
        VideoMediaInfo source,
        VideoMediaInfo output,
        VideoTranscodeProfile profile,
        out string detail)
    {
        if (output.Width <= 0 || output.Height <= 0 ||
            !double.IsFinite(output.SquarePixelDisplayWidth) || output.SquarePixelDisplayWidth <= 0 ||
            !double.IsFinite(output.SquarePixelDisplayHeight) || output.SquarePixelDisplayHeight <= 0 ||
            !double.IsFinite(output.DisplayAspectRatio) || output.DisplayAspectRatio <= 0 ||
            output.PixelAspectRatioNumerator == 0 || output.PixelAspectRatioDenominator == 0 ||
            output.Duration <= TimeSpan.Zero ||
            output.FramesPerSecondNumerator == 0 || output.FramesPerSecondDenominator == 0 ||
            string.IsNullOrWhiteSpace(output.ContainerCodec) ||
            string.IsNullOrWhiteSpace(output.VideoCodec))
        {
            detail = "The optimized output reported invalid or zero media metadata.";
            return false;
        }

        if (!string.Equals(output.ContainerCodec, "MP4", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(output.VideoCodec, "H.264", StringComparison.OrdinalIgnoreCase))
        {
            detail = "The optimized output is not an MP4 file with H.264 video.";
            return false;
        }

        if (output.VideoTrackCount != 1 || output.TimedMetadataTrackCount != 0)
        {
            detail = "The optimized output has an unsupported video or timed-metadata track layout.";
            return false;
        }

        if (output.Width != profile.Width || output.Height != profile.Height ||
            output.SquarePixelDisplayWidth != profile.Width ||
            output.SquarePixelDisplayHeight != profile.Height)
        {
            detail = "The optimized output dimensions do not match the calculated profile.";
            return false;
        }

        if (output.PixelAspectRatioNumerator != output.PixelAspectRatioDenominator)
        {
            detail = "The optimized output does not use square pixels.";
            return false;
        }

        if (output.SquarePixelDisplayWidth > source.SquarePixelDisplayWidth ||
            output.SquarePixelDisplayHeight > source.SquarePixelDisplayHeight)
        {
            detail = "The optimized output unexpectedly upscaled the source.";
            return false;
        }

        double relativeAspectDifference =
            Math.Abs(output.DisplayAspectRatio - source.DisplayAspectRatio) /
            Math.Max(output.DisplayAspectRatio, source.DisplayAspectRatio);
        double allowedAspectDifference = 2d / Math.Min(profile.Width, profile.Height);
        if (relativeAspectDifference > allowedAspectDifference)
        {
            detail = "The optimized output aspect ratio changed beyond the even-dimension tolerance.";
            return false;
        }

        if ((ulong)output.FramesPerSecondNumerator * profile.FrameRateDenominator !=
            (ulong)profile.FrameRateNumerator * output.FramesPerSecondDenominator)
        {
            detail = "The optimized output frame rate does not match the calculated profile.";
            return false;
        }

        if (source.AudioTrackCount == 0)
        {
            if (output.AudioTrackCount != 0)
            {
                detail = "The optimized output unexpectedly added an audio track.";
                return false;
            }
        }
        else if (output.AudioTrackCount != 1 ||
            !string.Equals(output.AudioCodec, "AAC", StringComparison.OrdinalIgnoreCase) ||
            output.AudioBitrate == 0 ||
            output.AudioBitrate > profile.AudioBitrate)
        {
            detail = "The optimized output audio does not match the calculated AAC profile.";
            return false;
        }

        if ((output.Duration - source.Duration).Duration() > TimeSpan.FromSeconds(1))
        {
            detail = "The optimized output duration differs from the source by more than one second.";
            return false;
        }

        detail = "The optimized output passed media verification.";
        return true;
    }

    private static VideoOptimizationOutcome MapBackendOutcome(VideoTranscodeBackendOutcome outcome) => outcome switch
    {
        VideoTranscodeBackendOutcome.CodecNotFound => VideoOptimizationOutcome.CodecNotFound,
        VideoTranscodeBackendOutcome.InvalidProfile => VideoOptimizationOutcome.InvalidProfile,
        VideoTranscodeBackendOutcome.UnsupportedInput => VideoOptimizationOutcome.UnsupportedInput,
        _ => VideoOptimizationOutcome.Failed,
    };

    private static bool TryValidateRequest(
        VideoOptimizationRequest request,
        out string sourcePath,
        out string destinationPath,
        out VideoOptimizationResult? invalid)
    {
        sourcePath = request.SourcePath ?? string.Empty;
        destinationPath = request.DestinationPath ?? string.Empty;
        invalid = null;
        if (request.Options is null ||
            request.ExpectedLength < 0 ||
            string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(destinationPath) ||
            !Path.IsPathFullyQualified(sourcePath) ||
            !Path.IsPathFullyQualified(destinationPath))
        {
            invalid = Result(
                VideoOptimizationOutcome.UnsupportedInput,
                sourcePath,
                detail: "Video optimization requires fully qualified source and output paths and a valid source snapshot.");
            return false;
        }

        try
        {
            sourcePath = Path.GetFullPath(sourcePath);
            destinationPath = Path.GetFullPath(destinationPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            invalid = Result(
                VideoOptimizationOutcome.UnsupportedInput,
                sourcePath,
                detail: "A video optimization path is invalid.");
            return false;
        }

        string? sourceDirectory = Path.GetDirectoryName(sourcePath);
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(sourceDirectory) ||
            !string.Equals(sourceDirectory, destinationDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(destinationPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            invalid = Result(
                VideoOptimizationOutcome.UnsupportedInput,
                sourcePath,
                detail: "The output must be a different sibling .mp4 path.");
            return false;
        }

        return true;
    }

    private static bool MatchesRequest(IdentityTrackedFile source, VideoOptimizationRequest request) =>
        source.Length == request.ExpectedLength && source.ModifiedUtc == request.ExpectedModifiedUtc;

    private static VideoOptimizationResult SourceChanged(string sourcePath) => Result(
        VideoOptimizationOutcome.SourceChanged,
        sourcePath,
        detail: "The source file changed since it was queued.");

    private static VideoOptimizationResult Recovery(
        string sourcePath,
        VideoMediaInfo? sourceMedia,
        VideoMediaInfo? outputMedia,
        string detail,
        IReadOnlyList<string> recoveryPaths) => new(
            VideoOptimizationOutcome.RecoveryRequired,
            sourcePath,
            null,
            null,
            sourceMedia,
            outputMedia,
            0,
            detail,
            recoveryPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    private static VideoOptimizationResult Result(
        VideoOptimizationOutcome outcome,
        string sourcePath,
        string? outputPath = null,
        long? outputSizeBytes = null,
        VideoMediaInfo? sourceMedia = null,
        VideoMediaInfo? outputMedia = null,
        long savedBytes = 0,
        string detail = "") => new(
            outcome,
            sourcePath,
            outputPath,
            outputSizeBytes,
            sourceMedia,
            outputMedia,
            savedBytes,
            detail,
            []);

    private sealed class MonotonicProgress(IProgress<double>? progress)
    {
        private double _last;

        public void Report(double value)
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            double next = Math.Clamp(value, 0, 100);
            if (next < _last)
            {
                return;
            }

            _last = next;
            progress?.Report(next);
        }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
