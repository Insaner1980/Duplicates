using Duplicates.Services;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.FileProperties;

namespace Duplicates.App.Tests;

public sealed class WindowsVideoTranscodeBackendTests
{
    [Theory]
    [InlineData(VideoOptimizationPreset.Smaller, VideoEncodingQuality.HD720p)]
    [InlineData(VideoOptimizationPreset.Balanced, VideoEncodingQuality.HD1080p)]
    [InlineData(VideoOptimizationPreset.HighQuality, VideoEncodingQuality.Uhd2160p)]
    public void CreateEncodingProfile_UsesConcretePresetAndExactPrimitiveValues(
        VideoOptimizationPreset preset,
        VideoEncodingQuality expectedQuality)
    {
        var requested = new VideoTranscodeProfile(
            preset,
            1268,
            712,
            30000,
            1001,
            3_123_456,
            1,
            1,
            true,
            123_456,
            "MP4",
            "H.264",
            "AAC",
            false);

        MediaEncodingProfile profile = WindowsVideoTranscodeBackend.CreateEncodingProfile(
            requested,
            VideoOrientation.Normal);

        Assert.Equal(expectedQuality, WindowsVideoTranscodeBackend.QualityFor(preset));
        Assert.Equal(1268u, profile.Video.Width);
        Assert.Equal(712u, profile.Video.Height);
        Assert.Equal(30000u, profile.Video.FrameRate.Numerator);
        Assert.Equal(1001u, profile.Video.FrameRate.Denominator);
        Assert.Equal(3_123_456u, profile.Video.Bitrate);
        Assert.Equal(1u, profile.Video.PixelAspectRatio.Numerator);
        Assert.Equal(1u, profile.Video.PixelAspectRatio.Denominator);
        Assert.Equal(MediaEncodingSubtypes.H264, profile.Video.Subtype);
        Assert.NotNull(profile.Audio);
        Assert.Equal(MediaEncodingSubtypes.Aac, profile.Audio.Subtype, ignoreCase: true);
        Assert.Equal(123_456u, profile.Audio.Bitrate);
    }

    [Fact]
    public void CreateEncodingProfile_RemovesAudioForSilentSource()
    {
        var requested = new VideoTranscodeProfile(
            VideoOptimizationPreset.Balanced,
            1280,
            720,
            30,
            1,
            3_500_000,
            1,
            1,
            false,
            0,
            "MP4",
            "H.264",
            null,
            true);

        MediaEncodingProfile profile = WindowsVideoTranscodeBackend.CreateEncodingProfile(
            requested,
            VideoOrientation.Normal);

        Assert.Null(profile.Audio);
    }

    [Theory]
    [InlineData(VideoOrientation.Normal, 360u, 640u)]
    [InlineData(VideoOrientation.Rotate90, 640u, 360u)]
    [InlineData(VideoOrientation.Rotate180, 360u, 640u)]
    [InlineData(VideoOrientation.Rotate270, 640u, 360u)]
    public void CreateEncodingProfile_CompensatesForMediaTranscoderSourceOrientation(
        VideoOrientation sourceOrientation,
        uint expectedRawWidth,
        uint expectedRawHeight)
    {
        var requested = new VideoTranscodeProfile(
            VideoOptimizationPreset.Balanced,
            360,
            640,
            30,
            1,
            3_500_000,
            1,
            1,
            false,
            0,
            "MP4",
            "H.264",
            null,
            false);

        MediaEncodingProfile profile = WindowsVideoTranscodeBackend.CreateEncodingProfile(
            requested,
            sourceOrientation);

        Assert.Equal(expectedRawWidth, profile.Video.Width);
        Assert.Equal(expectedRawHeight, profile.Video.Height);
        Guid rotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1");
        Assert.False(profile.Video.Properties.ContainsKey(rotationKey));
    }

    [Theory]
    [InlineData(false, TranscodeFailureReason.None, (int)VideoTranscodeBackendOutcome.Failed)]
    [InlineData(false, TranscodeFailureReason.CodecNotFound, (int)VideoTranscodeBackendOutcome.CodecNotFound)]
    [InlineData(false, TranscodeFailureReason.InvalidProfile, (int)VideoTranscodeBackendOutcome.InvalidProfile)]
    [InlineData(false, TranscodeFailureReason.Unknown, (int)VideoTranscodeBackendOutcome.Failed)]
    [InlineData(true, TranscodeFailureReason.None, (int)VideoTranscodeBackendOutcome.Succeeded)]
    public void MapPreparation_MapsEveryWindowsFailure(
        bool canTranscode,
        TranscodeFailureReason reason,
        int expectedOutcome)
    {
        VideoTranscodeBackendResult result = WindowsVideoTranscodeBackend.MapPreparation(
            canTranscode,
            reason);

        Assert.Equal((VideoTranscodeBackendOutcome)expectedOutcome, result.Outcome);
    }

    [Fact]
    public void MapRuntimeFailure_DoesNotExposeNativeExceptionText()
    {
        VideoTranscodeBackendResult result = WindowsVideoTranscodeBackend.MapRuntimeFailure(
            new InvalidOperationException("sensitive native exception text"));

        Assert.Equal(VideoTranscodeBackendOutcome.Failed, result.Outcome);
        Assert.Equal(
            "Windows MediaTranscoder failed while creating the optimized output.",
            result.Detail);
        Assert.DoesNotContain("sensitive", result.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
