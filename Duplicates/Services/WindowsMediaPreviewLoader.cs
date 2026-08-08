using Duplicates.Engine.Analysis;
using Duplicates.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Duplicates.Services;

public sealed class WindowsMediaPreviewLoader : IMediaPreviewLoader
{
    private const uint MaximumPreviewDimension = 512;

    public Task<MediaPreviewData> LoadAsync(
        ToolKind tool,
        SimilarityItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return (tool, item.Evidence) switch
        {
            (ToolKind.SimilarImages, ImageSimilarityEvidence) =>
                LoadImageAsync(item.FullPath, cancellationToken),
            (ToolKind.SimilarVideos, VideoSimilarityEvidence video) =>
                LoadVideoAsync(item.FullPath, video, cancellationToken),
            _ => throw new NotSupportedException($"Preview is not available for {tool}."),
        };
    }

    internal static (int Width, int Height) GetVideoThumbnailSize(VideoSimilarityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!double.IsFinite(evidence.DisplayAspectRatio) || evidence.DisplayAspectRatio <= 0)
        {
            throw new InvalidDataException("The video display aspect ratio is invalid.");
        }

        return evidence.DisplayAspectRatio >= 1
            ? ((int)MaximumPreviewDimension, 0)
            : (0, (int)MaximumPreviewDimension);
    }

    private static async Task<MediaPreviewData> LoadImageAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IRandomAccessStream stream = source.AsRandomAccessStream();
        BitmapDecoder? decoder = null;
        BitmapFrame? frame = null;
        BitmapTransform? transform = null;
        PixelDataProvider? pixelData = null;
        try
        {
            decoder = await WinRtAsync.AwaitAndCloseAsync(
                    BitmapDecoder.CreateAsync(stream),
                    cancellationToken)
                .ConfigureAwait(false);
            frame = await WinRtAsync.AwaitAndCloseAsync(
                    decoder.GetFrameAsync(0),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            (uint width, uint height) = Scale(frame.OrientedPixelWidth, frame.OrientedPixelHeight);
            (uint scaledWidth, uint scaledHeight) = Scale(frame.PixelWidth, frame.PixelHeight);
            transform = new BitmapTransform
            {
                ScaledWidth = scaledWidth,
                ScaledHeight = scaledHeight,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            pixelData = await WinRtAsync.AwaitAndCloseAsync(
                    frame.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        transform,
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.ColorManageToSRgb),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bgra = pixelData.DetachPixelData();
            if (bgra.Length != checked((int)(width * height * 4)))
            {
                throw new InvalidDataException("The preview buffer is invalid.");
            }

            return new MediaPreviewData(checked((int)width), checked((int)height), bgra);
        }
        finally
        {
            MediaLuminanceConverter.ReleaseNativeObject(pixelData);
            MediaLuminanceConverter.ReleaseNativeObject(transform);
            MediaLuminanceConverter.ReleaseNativeObject(frame);
            MediaLuminanceConverter.ReleaseNativeObject(decoder);
        }
    }

    private static async Task<MediaPreviewData> LoadVideoAsync(
        string path,
        VideoSimilarityEvidence evidence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StorageFile? file = null;
        MediaClip? clip = null;
        MediaComposition? composition = null;
        IList<MediaClip>? clips = null;
        ImageStream? thumbnail = null;
        BitmapDecoder? decoder = null;
        BitmapFrame? frame = null;
        BitmapTransform? transform = null;
        PixelDataProvider? pixelData = null;
        try
        {
            file = await WinRtAsync.AwaitAndCloseAsync(
                    StorageFile.GetFileFromPathAsync(path),
                    cancellationToken)
                .ConfigureAwait(false);
            clip = await WinRtAsync.AwaitAndCloseAsync(
                    MediaClip.CreateFromFileAsync(file),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan duration = clip.OriginalDuration;
            if (duration <= TimeSpan.Zero)
            {
                throw new InvalidDataException("The video duration is invalid.");
            }

            composition = new MediaComposition();
            clips = composition.Clips;
            clips.Add(clip);
            (int scaledWidth, int scaledHeight) = GetVideoThumbnailSize(evidence);
            thumbnail = await WinRtAsync.AwaitAndCloseAsync(
                    composition.GetThumbnailAsync(
                        TimeSpan.FromTicks(duration.Ticks / 2),
                        scaledWidth,
                        scaledHeight,
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
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.PixelWidth == 0 || frame.PixelHeight == 0)
            {
                throw new InvalidDataException("The preview dimensions are invalid.");
            }

            transform = new BitmapTransform();
            pixelData = await WinRtAsync.AwaitAndCloseAsync(
                    frame.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        transform,
                        ExifOrientationMode.IgnoreExifOrientation,
                        ColorManagementMode.ColorManageToSRgb),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bgra = pixelData.DetachPixelData();
            int width = checked((int)frame.PixelWidth);
            int height = checked((int)frame.PixelHeight);
            if (bgra.Length != checked(width * height * 4))
            {
                throw new InvalidDataException("The preview buffer is invalid.");
            }

            return new MediaPreviewData(width, height, bgra);
        }
        finally
        {
            MediaLuminanceConverter.ReleaseNativeObject(pixelData);
            MediaLuminanceConverter.ReleaseNativeObject(transform);
            MediaLuminanceConverter.ReleaseNativeObject(frame);
            MediaLuminanceConverter.ReleaseNativeObject(decoder);
            try
            {
                thumbnail?.Dispose();
            }
            finally
            {
                MediaLuminanceConverter.ReleaseNativeObject(thumbnail);
                try
                {
                    clips?.Clear();
                }
                finally
                {
                    MediaLuminanceConverter.ReleaseNativeObject(clips);
                    MediaLuminanceConverter.ReleaseNativeObject(composition);
                    MediaLuminanceConverter.ReleaseNativeObject(clip);
                    MediaLuminanceConverter.ReleaseNativeObject(file);
                }
            }
        }
    }

    private static (uint Width, uint Height) Scale(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("The preview dimensions are invalid.");
        }

        if (width <= MaximumPreviewDimension && height <= MaximumPreviewDimension)
        {
            return (width, height);
        }

        if (width >= height)
        {
            return (
                MaximumPreviewDimension,
                Math.Max(1, (uint)Math.Round(
                    (double)height * MaximumPreviewDimension / width,
                    MidpointRounding.AwayFromZero)));
        }

        return (
            Math.Max(1, (uint)Math.Round(
                (double)width * MaximumPreviewDimension / height,
                MidpointRounding.AwayFromZero)),
            MaximumPreviewDimension);
    }
}
