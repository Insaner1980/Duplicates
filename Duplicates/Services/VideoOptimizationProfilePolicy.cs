namespace Duplicates.Services;

internal sealed record VideoProfileBuildResult(
    VideoTranscodeProfile? Profile,
    VideoOptimizationOutcome? FailureOutcome,
    string Detail);

internal static class VideoOptimizationProfilePolicy
{
    private const uint AudioBitrateCap = 192_000;

    public static VideoProfileBuildResult Build(
        VideoMediaInfo source,
        VideoOptimizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Preset) || !IsStructurallyValid(source))
        {
            return Unsupported("The source video metadata is incomplete or unsupported.");
        }

        (uint maxWidth, uint maxHeight, uint fpsCap, uint bitrateCap) = options.Preset switch
        {
            VideoOptimizationPreset.Smaller => (1280u, 720u, 30u, 3_500_000u),
            VideoOptimizationPreset.Balanced => (1920u, 1080u, 60u, 8_000_000u),
            VideoOptimizationPreset.HighQuality => (3840u, 2160u, 60u, 20_000_000u),
            _ => throw new InvalidOperationException("The video optimization preset is invalid."),
        };

        double scale = Math.Min(
            1,
            Math.Min(
                maxWidth / source.SquarePixelDisplayWidth,
                maxHeight / source.SquarePixelDisplayHeight));
        double scaledWidth = source.SquarePixelDisplayWidth * scale;
        double scaledHeight = source.SquarePixelDisplayHeight * scale;
        if (!double.IsFinite(scaledWidth) || !double.IsFinite(scaledHeight) ||
            scaledWidth > uint.MaxValue || scaledHeight > uint.MaxValue)
        {
            return Unsupported("The source display geometry cannot be represented by an MP4 profile.");
        }

        uint targetWidth = checked((uint)(2 * Math.Floor(scaledWidth / 2)));
        uint targetHeight = checked((uint)(2 * Math.Floor(scaledHeight / 2)));
        if (targetWidth < 2 || targetHeight < 2)
        {
            return Unsupported("The source display geometry is too small for an even-sized MP4 output.");
        }

        bool sourceFpsWithinCap =
            (ulong)source.FramesPerSecondNumerator <= (ulong)fpsCap * source.FramesPerSecondDenominator;
        uint targetFpsNumerator = sourceFpsWithinCap
            ? source.FramesPerSecondNumerator
            : fpsCap;
        uint targetFpsDenominator = sourceFpsWithinCap
            ? source.FramesPerSecondDenominator
            : 1;
        uint targetVideoBitrate = source.VideoBitrate == 0
            ? bitrateCap
            : Math.Min(source.VideoBitrate, bitrateCap);
        bool includeAudio = source.AudioTrackCount == 1;
        uint targetAudioBitrate = 0;
        if (includeAudio)
        {
            targetAudioBitrate = source.AudioBitrate == 0
                ? AudioBitrateCap
                : Math.Min(source.AudioBitrate, AudioBitrateCap);
        }

        return new VideoProfileBuildResult(
            new VideoTranscodeProfile(
                options.Preset,
                targetWidth,
                targetHeight,
                targetFpsNumerator,
                targetFpsDenominator,
                targetVideoBitrate,
                1,
                1,
                includeAudio,
                targetAudioBitrate,
                "MP4",
                "H.264",
                includeAudio ? "AAC" : null,
                options.HardwareAccelerationEnabled),
            null,
            "The output profile is valid.");
    }

    private static bool IsStructurallyValid(VideoMediaInfo source) =>
        source.Width > 0 &&
        source.Height > 0 &&
        double.IsFinite(source.SquarePixelDisplayWidth) &&
        source.SquarePixelDisplayWidth > 0 &&
        double.IsFinite(source.SquarePixelDisplayHeight) &&
        source.SquarePixelDisplayHeight > 0 &&
        double.IsFinite(source.DisplayAspectRatio) &&
        source.DisplayAspectRatio > 0 &&
        source.PixelAspectRatioNumerator > 0 &&
        source.PixelAspectRatioDenominator > 0 &&
        source.Duration > TimeSpan.Zero &&
        source.FramesPerSecondNumerator > 0 &&
        source.FramesPerSecondDenominator > 0 &&
        !string.IsNullOrWhiteSpace(source.VideoCodec) &&
        !string.IsNullOrWhiteSpace(source.ContainerCodec) &&
        source.VideoTrackCount == 1 &&
        source.AudioTrackCount is 0 or 1 &&
        source.TimedMetadataTrackCount == 0 &&
        (source.AudioTrackCount == 0 || !string.IsNullOrWhiteSpace(source.AudioCodec));

    private static VideoProfileBuildResult Unsupported(string detail) =>
        new(null, VideoOptimizationOutcome.UnsupportedInput, detail);
}

internal static class VideoCodecNormalization
{
    public static string NormalizeContainer(string value) => Normalize(value, static subtype => subtype switch
    {
        "MPEG4" or "MP4" => "MP4",
        _ => null,
    });

    public static string NormalizeVideo(string value) => Normalize(value, static subtype => subtype switch
    {
        "H264" or "H264ES" or "H.264" => "H.264",
        "HEVC" or "HEVCES" => "HEVC",
        _ => null,
    });

    public static string NormalizeAudio(string value) => Normalize(value, static subtype => subtype switch
    {
        "AAC" => "AAC",
        "AACADTS" or "AAC ADTS" => "AAC ADTS",
        _ => null,
    });

    private static string Normalize(string value, Func<string, string?> known)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        return known(trimmed.ToUpperInvariant()) ?? trimmed;
    }
}
