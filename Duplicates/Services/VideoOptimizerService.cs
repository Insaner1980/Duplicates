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
        var state = new OptimizationState(request, source, sourcePath, destinationPath);
        try
        {
            VideoOptimizationResult? failure = await TranscodeAndVerifyAsync(state, reporter, cancellationToken).ConfigureAwait(false);
            return failure ?? PublishOutput(state, reporter, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            VideoOptimizationResult? recovery = HandleCancellation(state, exception, cancellationToken);
            if (recovery is not null)
            {
                return recovery;
            }

            throw;
        }
        catch (IdentitySourceChangedException)
        {
            return HandleSourceChanged(state);
        }
        catch (IdentityOwnedCreationRecoveryException exception)
        {
            return Recovery(
                sourcePath,
                state.SourceMedia,
                state.OutputMedia,
                "A newly created temporary output requires manual recovery.",
                [exception.RecoveryPath]);
        }
        catch (Exception)
        {
            return HandleUnexpectedFailure(state);
        }
    }

    private async Task<VideoOptimizationResult?> TranscodeAndVerifyAsync(
        OptimizationState state,
        MonotonicProgress reporter,
        CancellationToken cancellationToken)
    {
        string sourcePath = state.SourcePath;
        string destinationPath = state.DestinationPath;
        IdentityTrackedFile source = state.Source;
        VideoOptimizationRequest request = state.Request;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            state.SourceMedia = await _probe.ProbeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
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
            // Acquiring the guard verifies the source snapshot without mutating it.
        }

        reporter.Report(4);
        VideoProfileBuildResult profileResult = VideoOptimizationProfilePolicy.Build(
            state.SourceMedia,
            request.Options);
        if (profileResult.Profile is null)
        {
            return Result(
                profileResult.FailureOutcome ?? VideoOptimizationOutcome.InvalidProfile,
                sourcePath,
                media: (state.SourceMedia, null),
                detail: profileResult.Detail);
        }

        VideoTranscodeProfile profile = profileResult.Profile;
        IdentityTrackedFile artifact = CreateUniqueTemp(destinationPath);
        state.Artifact = artifact;
        reporter.Report(5);

        if (_transactions.EntryExistsCaseInsensitive(destinationPath))
        {
            return FinishAfterCleanup(
                VideoOptimizationOutcome.DestinationCollision,
                sourcePath,
                (state.SourceMedia, null),
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
                (state.SourceMedia, null),
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
                state.SourceMedia,
                null,
                "The temporary output identity changed after transcoding.",
                [artifact.Path]);
        }

        artifact = completedArtifact with { Identity = artifact.Identity };
        state.Artifact = artifact;
        var verification = await VerifyArtifactAsync(state, artifact, profile, cancellationToken).ConfigureAwait(false);
        bool outputVerified = verification.Verified;
        string verificationDetail = verification.Detail;

        if (!outputVerified)
        {
            return FinishAfterCleanup(
                VideoOptimizationOutcome.VerificationFailed,
                sourcePath,
                (state.SourceMedia, null),
                artifact.Length,
                0,
                verificationDetail,
                artifact);
        }

        return null;
    }

    private async Task<(bool Verified, string Detail)> VerifyArtifactAsync(
        OptimizationState state,
        IdentityTrackedFile artifact,
        VideoTranscodeProfile profile,
        CancellationToken cancellationToken)
    {
        bool outputVerified;
        string verificationDetail;
        try
        {
            using (_transactions.GuardSourceSnapshot(artifact))
            {
                VideoMediaInfo candidate = await _probe.ProbeAsync(
                    artifact.Path,
                    cancellationToken).ConfigureAwait(false);
                outputVerified = TryVerifyOutput(state.SourceMedia!, candidate, profile, out verificationDetail);
                state.OutputMedia = outputVerified ? candidate : null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            outputVerified = false;
            verificationDetail = "The temporary output could not be verified safely.";
        }

        return (outputVerified, verificationDetail);
    }

    private VideoOptimizationResult PublishOutput(
        OptimizationState state,
        MonotonicProgress reporter,
        CancellationToken cancellationToken)
    {
        string sourcePath = state.SourcePath;
        string destinationPath = state.DestinationPath;
        IdentityTrackedFile source = state.Source;
        VideoOptimizationRequest request = state.Request;
        IdentityTrackedFile artifact = state.Artifact!;
        reporter.Report(95);
        try
        {
            using (_transactions.GuardSourceSnapshot(source))
            {
                // Acquiring the guard verifies the source snapshot before publication.
            }
        }
        catch (IdentitySourceChangedException)
        {
            return FinishAfterCleanup(
                VideoOptimizationOutcome.SourceChanged,
                sourcePath,
                (state.SourceMedia, state.OutputMedia),
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
                (state.SourceMedia, state.OutputMedia),
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
                (state.SourceMedia, state.OutputMedia),
                artifact.Length,
                savedBytes,
                "The reserved output path became occupied before publication.",
                artifact);
        }

        return CommitOutput(state, artifact, reporter, savedBytes, outputIsNotSmaller, cancellationToken);
    }

    private VideoOptimizationResult CommitOutput(
        OptimizationState state,
        IdentityTrackedFile artifact,
        MonotonicProgress reporter,
        long savedBytes,
        bool outputIsNotSmaller,
        CancellationToken cancellationToken)
    {
        string sourcePath = state.SourcePath;
        string destinationPath = state.DestinationPath;
        IdentityTrackedFile source = state.Source;
        VideoOptimizationRequest request = state.Request;
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
                    (state.SourceMedia, null),
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
                (state.SourceMedia, state.OutputMedia),
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
                state.SourceMedia,
                state.OutputMedia,
                "The optimized output move did not reach a verifiable terminal state.",
                [artifact.Path, destinationPath]);
        }

        artifact = publication.File;
        state.Artifact = artifact;
        state.PublicationCommitted = true;
        IdentityPathProbe finalProbe = _transactions.Probe(destinationPath);
        if (finalProbe.State != IdentityPathState.Present || finalProbe.Identity != artifact.Identity)
        {
            return Recovery(
                sourcePath,
                state.SourceMedia,
                state.OutputMedia,
                "The published output identity could not be verified.",
                [destinationPath]);
        }

        IdentityTrackedFile final = _transactions.Capture(destinationPath);
        if (final.Identity != artifact.Identity)
        {
            return Recovery(
                sourcePath,
                state.SourceMedia,
                state.OutputMedia,
                "The published output was replaced before final verification.",
                [destinationPath]);
        }

        state.Artifact = null;
        reporter.Report(100);
        return Result(
            outputIsNotSmaller
                ? VideoOptimizationOutcome.KeptWithoutSaving
                : VideoOptimizationOutcome.Succeeded,
            sourcePath,
            destinationPath,
            final.Length,
            (state.SourceMedia, state.OutputMedia),
            request.ExpectedLength - final.Length,
            outputIsNotSmaller
                ? "The verified output was kept even though it did not save space."
                : "The verified optimized video was published.");
    }

    private VideoOptimizationResult? HandleCancellation(
        OptimizationState state,
        OperationCanceledException exception,
        CancellationToken cancellationToken)
    {
        string sourcePath = state.SourcePath;
        string destinationPath = state.DestinationPath;
        if (state.PublicationCommitted)
        {
            return Recovery(
                sourcePath,
                state.SourceMedia,
                state.OutputMedia,
                "The published output requires manual verification after a post-commit cancellation.",
                [destinationPath]);
        }

        string[] recoveryPaths = CleanupOwned(state.Artifact);
        if (recoveryPaths.Length != 0)
        {
            throw new VideoOptimizationCancellationException(
                recoveryPaths,
                state.SourceMedia,
                state.OutputMedia,
                cancellationToken,
                exception);
        }

        return null;
    }

    private VideoOptimizationResult HandleSourceChanged(OptimizationState state)
    {
        string sourcePath = state.SourcePath;
        string destinationPath = state.DestinationPath;
        if (state.PublicationCommitted)
        {
            return Recovery(
                sourcePath,
                state.SourceMedia,
                state.OutputMedia,
                "The published output requires manual verification after a post-commit source check failure.",
                [destinationPath]);
        }

        return FinishAfterCleanup(
            VideoOptimizationOutcome.SourceChanged,
            sourcePath,
            (state.SourceMedia, state.OutputMedia),
            state.Artifact?.Length,
            0,
            "The source file changed before the output could be published.",
            state.Artifact);
    }

    private VideoOptimizationResult HandleUnexpectedFailure(OptimizationState state)
    {
        string sourcePath = state.SourcePath;
        string destinationPath = state.DestinationPath;
        if (state.PublicationCommitted)
        {
            return Recovery(
                sourcePath,
                state.SourceMedia,
                state.OutputMedia,
                "The published output requires manual verification after an unexpected post-commit failure.",
                [destinationPath]);
        }

        string[] recoveryPaths = CleanupOwned(state.Artifact);
        return recoveryPaths.Length == 0
            ? Result(
                VideoOptimizationOutcome.Failed,
                sourcePath,
                outputSizeBytes: state.Artifact?.Length,
                media: (state.SourceMedia, state.OutputMedia),
                detail: "Video optimization failed before publication.")
            : Recovery(
                sourcePath,
                state.SourceMedia,
                state.OutputMedia,
                "Video optimization failed and the temporary output requires manual recovery.",
                recoveryPaths);
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
                // A raced occupant is preserved; retry with a different owned temporary name.
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
        (VideoMediaInfo? Source, VideoMediaInfo? Output) media,
        long? outputSizeBytes,
        long savedBytes,
        string detail,
        IdentityTrackedFile? artifact)
    {
        string[] recoveryPaths = CleanupOwned(artifact);
        return recoveryPaths.Length == 0
            ? Result(
                outcome,
                sourcePath,
                outputSizeBytes: outputSizeBytes,
                media: media,
                savedBytes: savedBytes,
                detail: detail)
            : Recovery(sourcePath, media.Source, media.Output, detail, recoveryPaths);
    }

    private string[] CleanupOwned(IdentityTrackedFile? artifact)
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
            ? Array.Empty<string>()
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

            if (destinationOwned)
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

        if (!DimensionsMatchProfile(output, profile))
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

        if (!TryVerifyAudio(source, output, profile, out detail))
        {
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1244", Justification = "Square-pixel output dimensions must exactly equal the integer profile dimensions; tolerance would admit invalid geometry.")]
    private static bool DimensionsMatchProfile(VideoMediaInfo output, VideoTranscodeProfile profile) =>
        output.Width == profile.Width && output.Height == profile.Height &&
        output.SquarePixelDisplayWidth == profile.Width && output.SquarePixelDisplayHeight == profile.Height;

    private static bool TryVerifyAudio(
        VideoMediaInfo source,
        VideoMediaInfo output,
        VideoTranscodeProfile profile,
        out string detail)
    {
        if (source.AudioTrackCount == 0)
        {
            detail = "The optimized output unexpectedly added an audio track.";
            return output.AudioTrackCount == 0;
        }

        detail = "The optimized output audio does not match the calculated AAC profile.";
        return output.AudioTrackCount == 1 &&
            string.Equals(output.AudioCodec, "AAC", StringComparison.OrdinalIgnoreCase) &&
            output.AudioBitrate > 0 && output.AudioBitrate <= profile.AudioBitrate;
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
        (VideoMediaInfo? Source, VideoMediaInfo? Output) media = default,
        long savedBytes = 0,
        string detail = "") => new(
            outcome,
            sourcePath,
            outputPath,
            outputSizeBytes,
            media.Source,
            media.Output,
            savedBytes,
            detail,
            []);

    private sealed class OptimizationState(
        VideoOptimizationRequest request,
        IdentityTrackedFile source,
        string sourcePath,
        string destinationPath)
    {
        public VideoOptimizationRequest Request { get; } = request;
        public IdentityTrackedFile Source { get; } = source;
        public string SourcePath { get; } = sourcePath;
        public string DestinationPath { get; } = destinationPath;
        public VideoMediaInfo? SourceMedia { get; set; }
        public VideoMediaInfo? OutputMedia { get; set; }
        public IdentityTrackedFile? Artifact { get; set; }
        public bool PublicationCommitted { get; set; }
    }

    private sealed class MonotonicProgress
    {
        private readonly IProgress<double>? _progress;

        public MonotonicProgress(IProgress<double>? progress)
        {
            _progress = progress;
        }

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
            _progress?.Report(next);
        }
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
}
