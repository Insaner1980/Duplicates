namespace Duplicates.Services;

internal sealed record VideoTranscodeProfile(
    VideoOptimizationPreset Preset,
    uint Width,
    uint Height,
    uint FrameRateNumerator,
    uint FrameRateDenominator,
    uint VideoBitrate,
    uint PixelAspectRatioNumerator,
    uint PixelAspectRatioDenominator,
    bool IncludeAudio,
    uint AudioBitrate,
    string ContainerCodec,
    string VideoCodec,
    string? AudioCodec,
    bool HardwareAccelerationEnabled);

internal enum VideoTranscodeBackendOutcome
{
    Succeeded,
    CodecNotFound,
    InvalidProfile,
    UnsupportedInput,
    Failed,
}

internal sealed record VideoTranscodeBackendResult(
    VideoTranscodeBackendOutcome Outcome,
    string Detail);

internal interface IVideoTranscodeBackend
{
    Task<VideoTranscodeBackendResult> TranscodeAsync(
        string sourcePath,
        string destinationPath,
        VideoTranscodeProfile profile,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
