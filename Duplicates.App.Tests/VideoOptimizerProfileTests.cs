using Duplicates.Services;

namespace Duplicates.App.Tests;

public sealed class VideoOptimizerProfileTests
{
    [Theory]
    [InlineData(VideoOptimizationPreset.Smaller, 1280u, 720u, 30u, 1u, 3_500_000u)]
    [InlineData(VideoOptimizationPreset.Balanced, 1920u, 1080u, 60u, 1u, 8_000_000u)]
    [InlineData(VideoOptimizationPreset.HighQuality, 3840u, 2160u, 60u, 1u, 20_000_000u)]
    public void Build_UsesConcretePresetCaps(
        VideoOptimizationPreset preset,
        uint expectedWidth,
        uint expectedHeight,
        uint expectedFpsNumerator,
        uint expectedFpsDenominator,
        uint expectedBitrate)
    {
        VideoMediaInfo source = ValidMedia(
            width: 4096,
            height: 2304,
            displayWidth: 4096,
            displayHeight: 2304,
            videoBitrate: 30_000_000,
            fpsNumerator: 120,
            fpsDenominator: 1);

        VideoProfileBuildResult result = VideoOptimizationProfilePolicy.Build(
            source,
            new VideoOptimizationOptions(preset, true, false));

        VideoTranscodeProfile profile = Assert.IsType<VideoTranscodeProfile>(result.Profile);
        Assert.Null(result.FailureOutcome);
        Assert.Equal(preset, profile.Preset);
        Assert.Equal(expectedWidth, profile.Width);
        Assert.Equal(expectedHeight, profile.Height);
        Assert.Equal(expectedFpsNumerator, profile.FrameRateNumerator);
        Assert.Equal(expectedFpsDenominator, profile.FrameRateDenominator);
        Assert.Equal(expectedBitrate, profile.VideoBitrate);
        Assert.Equal(1u, profile.PixelAspectRatioNumerator);
        Assert.Equal(1u, profile.PixelAspectRatioDenominator);
        Assert.Equal("MP4", profile.ContainerCodec);
        Assert.Equal("H.264", profile.VideoCodec);
        Assert.True(profile.HardwareAccelerationEnabled);
    }

    [Theory]
    [InlineData(720, 480, 640d, 480d, 640u, 480u)]
    [InlineData(480, 720, 480d, 640d, 480u, 640u)]
    [InlineData(721, 481, 640.8888888888889d, 481d, 640u, 480u)]
    [InlineData(1080, 1920, 1080d, 1920d, 606u, 1080u)]
    public void Build_UsesSquarePixelExtentsAndEvenFloor(
        int codedWidth,
        int codedHeight,
        double displayWidth,
        double displayHeight,
        uint expectedWidth,
        uint expectedHeight)
    {
        VideoMediaInfo source = ValidMedia(
            width: codedWidth,
            height: codedHeight,
            displayWidth: displayWidth,
            displayHeight: displayHeight);

        VideoProfileBuildResult result = VideoOptimizationProfilePolicy.Build(
            source,
            new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, true, false));

        VideoTranscodeProfile profile = Assert.IsType<VideoTranscodeProfile>(result.Profile);
        Assert.Equal(expectedWidth, profile.Width);
        Assert.Equal(expectedHeight, profile.Height);
    }

    [Theory]
    [InlineData(30000u, 1001u, 30000u, 1001u)]
    [InlineData(60000u, 1001u, 60000u, 1001u)]
    [InlineData(60001u, 1000u, 60u, 1u)]
    public void Build_PreservesOrCapsTheExactFrameRateRational(
        uint sourceNumerator,
        uint sourceDenominator,
        uint expectedNumerator,
        uint expectedDenominator)
    {
        VideoMediaInfo source = ValidMedia(
            fpsNumerator: sourceNumerator,
            fpsDenominator: sourceDenominator);

        VideoTranscodeProfile profile = Assert.IsType<VideoTranscodeProfile>(
            VideoOptimizationProfilePolicy.Build(
                source,
                new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, true, false)).Profile);

        Assert.Equal(expectedNumerator, profile.FrameRateNumerator);
        Assert.Equal(expectedDenominator, profile.FrameRateDenominator);
    }

    [Theory]
    [InlineData(0u, 8_000_000u)]
    [InlineData(8_000_000u, 8_000_000u)]
    [InlineData(8_000_001u, 8_000_000u)]
    [InlineData(2_500_000u, 2_500_000u)]
    public void Build_BoundsKnownVideoBitrateAndUsesCapWhenUnknown(
        uint sourceBitrate,
        uint expectedBitrate)
    {
        VideoMediaInfo source = ValidMedia(videoBitrate: sourceBitrate);

        VideoTranscodeProfile profile = Assert.IsType<VideoTranscodeProfile>(
            VideoOptimizationProfilePolicy.Build(
                source,
                new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, true, false)).Profile);

        Assert.Equal(expectedBitrate, profile.VideoBitrate);
    }

    [Theory]
    [InlineData(0u, 192_000u)]
    [InlineData(128_000u, 128_000u)]
    [InlineData(192_000u, 192_000u)]
    [InlineData(256_000u, 192_000u)]
    public void Build_BoundsAacAudioBitrate(uint sourceBitrate, uint expectedBitrate)
    {
        VideoMediaInfo source = ValidMedia(audioBitrate: sourceBitrate);

        VideoTranscodeProfile profile = Assert.IsType<VideoTranscodeProfile>(
            VideoOptimizationProfilePolicy.Build(
                source,
                new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, true, false)).Profile);

        Assert.True(profile.IncludeAudio);
        Assert.Equal("AAC", profile.AudioCodec);
        Assert.Equal(expectedBitrate, profile.AudioBitrate);
    }

    [Fact]
    public void Build_OmitsAudioWhenTheSourceHasNoAudioTrack()
    {
        VideoMediaInfo source = ValidMedia(audioCodec: null, audioTrackCount: 0, audioBitrate: 0);

        VideoTranscodeProfile profile = Assert.IsType<VideoTranscodeProfile>(
            VideoOptimizationProfilePolicy.Build(
                source,
                new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, false, false)).Profile);

        Assert.False(profile.IncludeAudio);
        Assert.Null(profile.AudioCodec);
        Assert.Equal(0u, profile.AudioBitrate);
        Assert.False(profile.HardwareAccelerationEnabled);
    }

    [Theory]
    [InlineData(2, 1, 0)]
    [InlineData(1, 2, 0)]
    [InlineData(1, 1, 1)]
    public void Build_RejectsTrackLayoutsThatWouldDropContent(
        int videoTracks,
        int audioTracks,
        int timedMetadataTracks)
    {
        VideoMediaInfo source = ValidMedia(
            videoTrackCount: videoTracks,
            audioTrackCount: audioTracks,
            timedMetadataTrackCount: timedMetadataTracks);

        VideoProfileBuildResult result = VideoOptimizationProfilePolicy.Build(
            source,
            new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, true, false));

        Assert.Null(result.Profile);
        Assert.Equal(VideoOptimizationOutcome.UnsupportedInput, result.FailureOutcome);
    }

    [Theory]
    [InlineData(1d, 1080d)]
    [InlineData(1920d, 1d)]
    [InlineData(double.NaN, 1080d)]
    [InlineData(1920d, double.PositiveInfinity)]
    public void Build_RejectsOnePixelOrNonfiniteDisplayExtents(double width, double height)
    {
        VideoMediaInfo source = ValidMedia(displayWidth: width, displayHeight: height);

        VideoProfileBuildResult result = VideoOptimizationProfilePolicy.Build(
            source,
            new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, true, false));

        Assert.Null(result.Profile);
        Assert.Equal(VideoOptimizationOutcome.UnsupportedInput, result.FailureOutcome);
    }

    [Theory]
    [InlineData("Mpeg4", "MP4")]
    [InlineData("mp4", "MP4")]
    public void NormalizeContainer_UsesStableNames(string source, string expected) =>
        Assert.Equal(expected, VideoCodecNormalization.NormalizeContainer(source));

    [Theory]
    [InlineData("H264", "H.264")]
    [InlineData("h264es", "H.264")]
    [InlineData("Hevc", "HEVC")]
    [InlineData("HEVCES", "HEVC")]
    public void NormalizeVideo_UsesStableNames(string source, string expected) =>
        Assert.Equal(expected, VideoCodecNormalization.NormalizeVideo(source));

    [Theory]
    [InlineData("Aac", "AAC")]
    [InlineData("aacadts", "AAC ADTS")]
    public void NormalizeAudio_DoesNotTreatAdtsAsMp4Aac(string source, string expected) =>
        Assert.Equal(expected, VideoCodecNormalization.NormalizeAudio(source));

    private static VideoMediaInfo ValidMedia(
        int width = 1920,
        int height = 1080,
        double displayWidth = 1920,
        double displayHeight = 1080,
        uint videoBitrate = 6_000_000,
        uint fpsNumerator = 30000,
        uint fpsDenominator = 1001,
        string? audioCodec = "AAC",
        uint audioBitrate = 128_000,
        int videoTrackCount = 1,
        int audioTrackCount = 1,
        int timedMetadataTrackCount = 0) => new(
            width,
            height,
            displayWidth,
            displayHeight,
            displayWidth / displayHeight,
            1,
            1,
            TimeSpan.FromSeconds(10),
            6_128_000,
            videoBitrate,
            fpsNumerator,
            fpsDenominator,
            "H.264",
            "MP4",
            audioCodec,
            audioBitrate,
            videoTrackCount,
            audioTrackCount,
            timedMetadataTrackCount);
}
