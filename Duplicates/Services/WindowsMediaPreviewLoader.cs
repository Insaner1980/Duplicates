using Duplicates.Engine.Analysis;
using Duplicates.Models;
using Windows.Graphics.Imaging;
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
        return tool == ToolKind.SimilarImages && item.Evidence is ImageSimilarityEvidence
            ? LoadImageAsync(item.FullPath, cancellationToken)
            : throw new NotSupportedException($"Preview is not available for {tool}.");
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
            transform = new BitmapTransform
            {
                ScaledWidth = width,
                ScaledHeight = height,
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
