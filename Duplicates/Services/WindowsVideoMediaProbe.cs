using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Duplicates.Services;

internal sealed class WindowsVideoMediaProbe : IVideoMediaProbe
{
    public async Task<VideoMediaInfo> ProbeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        MediaComposition? composition = null;
        IList<MediaClip>? clips = null;
        ImageStream? thumbnail = null;
        try
        {
            StorageFile file = await WinRtAsync.AwaitAndCloseAsync(
                    StorageFile.GetFileFromPathAsync(path),
                    cancellationToken)
                .ConfigureAwait(false);
            VideoProperties properties = await WinRtAsync.AwaitAndCloseAsync(
                    file.Properties.GetVideoPropertiesAsync(),
                    cancellationToken)
                .ConfigureAwait(false);
            MediaEncodingProfile encodingProfile = await WinRtAsync.AwaitAndCloseAsync(
                    MediaEncodingProfile.CreateFromFileAsync(file),
                    cancellationToken)
                .ConfigureAwait(false);
            var videoTracks = encodingProfile.GetVideoTracks();
            var audioTracks = encodingProfile.GetAudioTracks();
            var timedMetadataTracks = encodingProfile.GetTimedMetadataTracks();
            if (videoTracks.Count != 1 || audioTracks.Count > 1 || timedMetadataTracks.Count != 0)
            {
                throw new InvalidDataException(
                    "The video contains a track layout that cannot be optimized without dropping content.");
            }

            VideoEncodingProperties video = videoTracks[0].EncodingProperties;
            AudioEncodingProperties? audio = audioTracks.Count == 1
                ? audioTracks[0].EncodingProperties
                : null;
            MediaRatio frameRate = video.FrameRate;
            MediaRatio pixelAspectRatio = video.PixelAspectRatio;
            VideoMediaInfo info = BuildInfo(
                (properties.Width, properties.Height, properties.Orientation),
                properties.Duration,
                (properties.Bitrate, video.Bitrate, audio?.Bitrate ?? 0),
                (frameRate.Numerator, frameRate.Denominator),
                (pixelAspectRatio.Numerator, pixelAspectRatio.Denominator),
                (video.Subtype, encodingProfile.Container?.Subtype ?? string.Empty, audio?.Subtype),
                (videoTracks.Count, audioTracks.Count, timedMetadataTracks.Count));

            MediaClip clip = await WinRtAsync.AwaitAndCloseAsync(
                    MediaClip.CreateFromFileAsync(file),
                    cancellationToken)
                .ConfigureAwait(false);
            composition = new MediaComposition();
            clips = composition.Clips;
            clips.Add(clip);
            thumbnail = await WinRtAsync.AwaitAndCloseAsync(
                    composition.GetThumbnailAsync(
                        TimeSpan.FromTicks(info.Duration.Ticks / 2),
                        32,
                        32,
                        VideoFramePrecision.NearestFrame),
                    cancellationToken)
                .ConfigureAwait(false);
            BitmapDecoder decoder = await WinRtAsync.AwaitAndCloseAsync(
                    BitmapDecoder.CreateAsync(thumbnail),
                    cancellationToken)
                .ConfigureAwait(false);
            BitmapFrame frame = await WinRtAsync.AwaitAndCloseAsync(
                    decoder.GetFrameAsync(0),
                    cancellationToken)
                .ConfigureAwait(false);
            byte[] pixels = await MediaLuminanceConverter.Decode32x32Async(
                    frame,
                    cancellationToken)
                .ConfigureAwait(false);
            if (pixels.Length != 1024)
            {
                throw new InvalidDataException("The video midpoint frame could not be decoded.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return info;
        }
        finally
        {
            try
            {
                thumbnail?.Dispose();
            }
            finally
            {
                clips?.Clear();
            }
        }
    }

    internal static VideoMediaInfo BuildInfo(
        (uint Width, uint Height, VideoOrientation Orientation) dimensions,
        TimeSpan duration,
        (uint Total, uint Video, uint Audio) bitrates,
        (uint Numerator, uint Denominator) frameRate,
        (uint Numerator, uint Denominator) pixelAspectRatio,
        (string Video, string Container, string? Audio) codecs,
        (int Video, int Audio, int TimedMetadata) trackCounts)
    {
        (uint codedWidth, uint codedHeight, VideoOrientation orientation) = dimensions;
        (uint totalBitrate, uint videoBitrate, uint audioBitrate) = bitrates;
        (uint frameRateNumerator, uint frameRateDenominator) = frameRate;
        (uint pixelAspectRatioNumerator, uint pixelAspectRatioDenominator) = pixelAspectRatio;
        (string videoCodec, string containerCodec, string? audioCodec) = codecs;
        (int videoTrackCount, int audioTrackCount, int timedMetadataTrackCount) = trackCounts;
        bool missingPixelAspectRatio =
            pixelAspectRatioNumerator == 0 && pixelAspectRatioDenominator == 0;
        if (duration <= TimeSpan.Zero ||
            frameRateNumerator == 0 || frameRateDenominator == 0 ||
            videoTrackCount != 1 || audioTrackCount is < 0 or > 1 ||
            timedMetadataTrackCount != 0 ||
            (audioTrackCount == 1 && string.IsNullOrWhiteSpace(audioCodec)))
        {
            throw new InvalidDataException("The video media metadata is structurally invalid.");
        }

        VideoDisplayGeometry geometry = WindowsVideoSampleProvider.CalculateDisplayGeometry(
            codedWidth,
            codedHeight,
            pixelAspectRatioNumerator,
            pixelAspectRatioDenominator,
            !missingPixelAspectRatio,
            orientation);
        uint normalizedParNumerator = missingPixelAspectRatio ? 1u : pixelAspectRatioNumerator;
        uint normalizedParDenominator = missingPixelAspectRatio ? 1u : pixelAspectRatioDenominator;
        string normalizedVideo = VideoCodecNormalization.NormalizeVideo(videoCodec);
        string normalizedContainer = VideoCodecNormalization.NormalizeContainer(containerCodec);
        string? normalizedAudio = audioTrackCount == 0
            ? null
            : VideoCodecNormalization.NormalizeAudio(audioCodec!);

        return new VideoMediaInfo(
            geometry.Width,
            geometry.Height,
            geometry.SquarePixelDisplayWidth,
            geometry.SquarePixelDisplayHeight,
            geometry.AspectRatio,
            normalizedParNumerator,
            normalizedParDenominator,
            duration,
            totalBitrate,
            videoBitrate,
            frameRateNumerator,
            frameRateDenominator,
            normalizedVideo,
            normalizedContainer,
            normalizedAudio,
            audioTrackCount == 0 ? 0 : audioBitrate,
            videoTrackCount,
            audioTrackCount,
            timedMetadataTrackCount);
    }
}
