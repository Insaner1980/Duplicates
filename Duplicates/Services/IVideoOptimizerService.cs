namespace Duplicates.Services;

public enum VideoOptimizationPreset
{
    Smaller,
    Balanced,
    HighQuality,
}

public sealed record VideoOptimizationOptions(
    VideoOptimizationPreset Preset,
    bool HardwareAccelerationEnabled,
    bool KeepOutputWhenNotSmaller);

public sealed record VideoMediaInfo(
    int Width,
    int Height,
    double SquarePixelDisplayWidth,
    double SquarePixelDisplayHeight,
    double DisplayAspectRatio,
    uint PixelAspectRatioNumerator,
    uint PixelAspectRatioDenominator,
    TimeSpan Duration,
    uint TotalBitrate,
    uint VideoBitrate,
    uint FramesPerSecondNumerator,
    uint FramesPerSecondDenominator,
    string VideoCodec,
    string ContainerCodec,
    string? AudioCodec,
    uint AudioBitrate,
    int VideoTrackCount,
    int AudioTrackCount,
    int TimedMetadataTrackCount);

public sealed record VideoOptimizationRequest(
    string SourcePath,
    long ExpectedLength,
    DateTime ExpectedModifiedUtc,
    string DestinationPath,
    VideoOptimizationOptions Options);

public enum VideoOptimizationOutcome
{
    Succeeded,
    KeptWithoutSaving,
    NoSpaceSaving,
    SourceChanged,
    CodecNotFound,
    InvalidProfile,
    UnsupportedInput,
    VerificationFailed,
    DestinationCollision,
    RecoveryRequired,
    Failed,
}

public sealed record VideoOptimizationResult(
    VideoOptimizationOutcome Outcome,
    string SourcePath,
    string? OutputPath,
    long? OutputSizeBytes,
    VideoMediaInfo? SourceMedia,
    VideoMediaInfo? OutputMedia,
    long SavedBytes,
    string Detail,
    IReadOnlyList<string> RecoveryPaths);

public sealed class VideoOptimizationCancellationException : OperationCanceledException
{
    public VideoOptimizationCancellationException(
        IReadOnlyList<string> recoveryPaths,
        VideoMediaInfo? sourceMedia,
        VideoMediaInfo? outputMedia,
        CancellationToken cancellationToken,
        Exception? innerException = null)
        : base(
            "Video optimization was cancelled, but one or more owned artifacts require manual recovery.",
            innerException,
            cancellationToken)
    {
        RecoveryPaths = recoveryPaths?.ToArray() ?? throw new ArgumentNullException(nameof(recoveryPaths));
        SourceMedia = sourceMedia;
        OutputMedia = outputMedia;
    }

    public IReadOnlyList<string> RecoveryPaths { get; }

    public VideoMediaInfo? SourceMedia { get; }

    public VideoMediaInfo? OutputMedia { get; }
}

public interface IVideoOptimizerService
{
    Task<VideoOptimizationResult> OptimizeAsync(
        VideoOptimizationRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
