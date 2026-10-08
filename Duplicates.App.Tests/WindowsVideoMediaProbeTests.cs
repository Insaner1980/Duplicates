using Duplicates.Services;
using Windows.Storage.FileProperties;

namespace Duplicates.App.Tests;

public sealed class WindowsVideoMediaProbeTests
{
    [Fact]
    public void BuildInfo_NormalizesCodecsMissingParAndAnisotropicDisplayGeometry()
    {
        VideoMediaInfo info = WindowsVideoMediaProbe.BuildInfo(
            (720, 480, VideoOrientation.Normal),
            TimeSpan.FromSeconds(12),
            (5_128_000, 5_000_000, 128_000),
            (30000, 1001),
            (8, 9),
            ("H264Es", "Mpeg4", "Aac"),
            (1, 1, 0));

        Assert.Equal(720, info.Width);
        Assert.Equal(480, info.Height);
        Assert.Equal(640, info.SquarePixelDisplayWidth, 12);
        Assert.Equal(480, info.SquarePixelDisplayHeight, 12);
        Assert.Equal(4d / 3, info.DisplayAspectRatio, 12);
        Assert.Equal(8u, info.PixelAspectRatioNumerator);
        Assert.Equal(9u, info.PixelAspectRatioDenominator);
        Assert.Equal("H.264", info.VideoCodec);
        Assert.Equal("MP4", info.ContainerCodec);
        Assert.Equal("AAC", info.AudioCodec);
    }

    [Fact]
    public void BuildInfo_Rotate270SwapsCodedAndSquarePixelExtents()
    {
        VideoMediaInfo info = WindowsVideoMediaProbe.BuildInfo(
            (720, 480, VideoOrientation.Rotate270),
            TimeSpan.FromSeconds(12),
            (5_000_000, 4_900_000, 0),
            (30, 1),
            (8, 9),
            ("H264", "Mpeg4", null),
            (1, 0, 0));

        Assert.Equal(480, info.Width);
        Assert.Equal(720, info.Height);
        Assert.Equal(480, info.SquarePixelDisplayWidth, 12);
        Assert.Equal(640, info.SquarePixelDisplayHeight, 12);
        Assert.Equal(3d / 4, info.DisplayAspectRatio, 12);
    }

    [Fact]
    public void BuildInfo_NormalizesActuallyMissingParToOneToOne()
    {
        VideoMediaInfo info = WindowsVideoMediaProbe.BuildInfo(
            (640, 480, VideoOrientation.Normal),
            TimeSpan.FromSeconds(1),
            (1, 1, 0),
            (24, 1),
            (0, 0),
            ("H264", "Mpeg4", null),
            (1, 0, 0));

        Assert.Equal(1u, info.PixelAspectRatioNumerator);
        Assert.Equal(1u, info.PixelAspectRatioDenominator);
        Assert.Equal(640, info.SquarePixelDisplayWidth, 12);
    }

    [Theory]
    [InlineData(0u, 1u, 1, 0, 0)]
    [InlineData(1u, 0u, 1, 0, 0)]
    [InlineData(1u, 1u, 0, 0, 0)]
    [InlineData(1u, 1u, 2, 0, 0)]
    [InlineData(1u, 1u, 1, 2, 0)]
    [InlineData(1u, 1u, 1, 0, 1)]
    public void BuildInfo_RejectsHalfParAndTrackLayoutsThatCouldDropContent(
        uint parNumerator,
        uint parDenominator,
        int videoTracks,
        int audioTracks,
        int timedTracks)
    {
        Assert.Throws<InvalidDataException>(() => WindowsVideoMediaProbe.BuildInfo(
            (640, 480, VideoOrientation.Normal),
            TimeSpan.FromSeconds(1),
            (1, 1, audioTracks == 0 ? 0u : 128_000u),
            (24, 1),
            (parNumerator, parDenominator),
            ("H264", "Mpeg4", audioTracks == 0 ? null : "Aac"),
            (videoTracks, audioTracks, timedTracks)));
    }
}
