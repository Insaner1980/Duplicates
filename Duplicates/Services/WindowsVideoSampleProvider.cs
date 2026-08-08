using Duplicates.Engine.Analysis.Media;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Duplicates.Services;

public sealed class WindowsVideoSampleProvider : IVideoSampleProvider
{
    private static readonly int[] ThumbnailPercentages = [10, 30, 50, 70, 90];
    private readonly MediaFingerprintCache? _cache;
    private readonly Func<string, CancellationToken, Task<VideoSample>> _sampleFactory;

    public WindowsVideoSampleProvider(MediaFingerprintCache? cache = null)
        : this(cache, SampleAsync)
    {
    }

    internal WindowsVideoSampleProvider(
        MediaFingerprintCache? cache,
        Func<string, CancellationToken, Task<VideoSample>> sampleFactory)
    {
        _cache = cache;
        _sampleFactory = sampleFactory;
    }

    public Task<VideoSample> GetSampleAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _cache is null
            ? GetFreshSampleAsync(path, cancellationToken)
            : _cache.GetOrCreateVideoAsync(
                path,
                token => _sampleFactory(path, token),
                cancellationToken);
    }

    public async Task<VideoSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        VideoSample sample = await _sampleFactory(path, cancellationToken).ConfigureAwait(false);
        if (!IsValid(sample))
        {
            throw new InvalidDataException("The video sample is structurally invalid.");
        }

        return sample;
    }

    internal static VideoDisplayGeometry CalculateDisplayGeometry(
        uint codedWidth,
        uint codedHeight,
        uint parNumerator,
        uint parDenominator,
        bool hasPixelAspectRatio,
        VideoOrientation orientation)
    {
        if (codedWidth == 0 || codedHeight == 0 ||
            (hasPixelAspectRatio && (parNumerator == 0 || parDenominator == 0)))
        {
            throw new InvalidDataException("The video display geometry is invalid.");
        }

        double pixelAspectRatio = hasPixelAspectRatio
            ? (double)parNumerator / parDenominator
            : 1;
        double aspectRatio = ((double)codedWidth / codedHeight) * pixelAspectRatio;
        bool quarterTurn = orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270;
        if (quarterTurn)
        {
            aspectRatio = 1 / aspectRatio;
        }

        if (!double.IsFinite(aspectRatio) || aspectRatio <= 0)
        {
            throw new InvalidDataException("The video display aspect ratio is invalid.");
        }

        int width = checked((int)(quarterTurn ? codedHeight : codedWidth));
        int height = checked((int)(quarterTurn ? codedWidth : codedHeight));
        return new VideoDisplayGeometry(width, height, aspectRatio);
    }

    private static async Task<VideoSample> SampleAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StorageFile? file = null;
        StorageItemContentProperties? contentProperties = null;
        VideoProperties? properties = null;
        MediaClip? clip = null;
        VideoEncodingProperties? encoding = null;
        MediaRatio? frameRate = null;
        MediaRatio? pixelAspectRatio = null;
        MediaComposition? composition = null;
        IList<MediaClip>? clips = null;
        try
        {
            file = await WinRtAsync.AwaitAndCloseAsync(
                    StorageFile.GetFileFromPathAsync(path),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            contentProperties = file.Properties;
            cancellationToken.ThrowIfCancellationRequested();
            properties = await WinRtAsync.AwaitAndCloseAsync(
                    contentProperties.GetVideoPropertiesAsync(),
                    cancellationToken)
                .ConfigureAwait(false);
            clip = await WinRtAsync.AwaitAndCloseAsync(
                    MediaClip.CreateFromFileAsync(file),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            encoding = clip.GetVideoEncodingProperties();
            cancellationToken.ThrowIfCancellationRequested();

            frameRate = encoding.FrameRate;
            if (frameRate.Numerator == 0 || frameRate.Denominator == 0)
            {
                throw new InvalidDataException("The video frame rate is invalid.");
            }

            pixelAspectRatio = encoding.PixelAspectRatio;
            bool hasPixelAspectRatio = pixelAspectRatio is not null &&
                (pixelAspectRatio.Numerator != 0 || pixelAspectRatio.Denominator != 0);
            VideoDisplayGeometry geometry = CalculateDisplayGeometry(
                properties.Width,
                properties.Height,
                pixelAspectRatio?.Numerator ?? 0,
                pixelAspectRatio?.Denominator ?? 0,
                hasPixelAspectRatio,
                properties.Orientation);
            TimeSpan duration = clip.OriginalDuration;
            if (duration <= TimeSpan.Zero)
            {
                throw new InvalidDataException("The video duration is invalid.");
            }

            uint bitrate = properties.Bitrate;
            double framesPerSecond = (double)frameRate.Numerator / frameRate.Denominator;
            string codec = encoding.Subtype;
            composition = new MediaComposition();
            clips = composition.Clips;
            clips.Add(clip);
            var frames = new List<byte[]>(ThumbnailPercentages.Length);
            foreach (int percentage in ThumbnailPercentages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan position = TimeSpan.FromTicks((long)(duration.Ticks * (percentage / 100d)));
                ImageStream? thumbnail = null;
                BitmapDecoder? decoder = null;
                BitmapFrame? frame = null;
                try
                {
                    thumbnail = await WinRtAsync.AwaitAndCloseAsync(
                            composition.GetThumbnailAsync(
                                position,
                                32,
                                32,
                                VideoFramePrecision.NearestFrame),
                            cancellationToken)
                        .ConfigureAwait(false);
                    decoder = await WinRtAsync.AwaitAndCloseAsync(
                            BitmapDecoder.CreateAsync(thumbnail),
                            cancellationToken)
                        .ConfigureAwait(false);
                    frame = await WinRtAsync.AwaitAndCloseAsync(
                            decoder.GetFrameAsync(0),
                            cancellationToken)
                        .ConfigureAwait(false);

                    frames.Add(await MediaLuminanceConverter.Decode32x32Async(frame, cancellationToken)
                        .ConfigureAwait(false));
                }
                finally
                {
                    thumbnail?.Dispose();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new VideoSample(
                geometry.Width,
                geometry.Height,
                geometry.AspectRatio,
                duration,
                bitrate,
                framesPerSecond,
                codec,
                frames.ToArray());
        }
        finally
        {
            clips?.Clear();
        }
    }

    private static bool IsValid(VideoSample sample) =>
        sample.Width > 0 &&
        sample.Height > 0 &&
        double.IsFinite(sample.DisplayAspectRatio) &&
        sample.DisplayAspectRatio > 0 &&
        sample.Duration > TimeSpan.Zero &&
        double.IsFinite(sample.FramesPerSecond) &&
        sample.FramesPerSecond > 0 &&
        !string.IsNullOrWhiteSpace(sample.Codec) &&
        sample.LuminanceFrames32x32 is { Count: 5 } &&
        sample.LuminanceFrames32x32.All(frame => frame?.Length == 1024) &&
        sample.LuminanceFrames32x32.Distinct(ReferenceEqualityComparer.Instance).Count() == 5;
}

internal readonly record struct VideoDisplayGeometry(int Width, int Height, double AspectRatio);
