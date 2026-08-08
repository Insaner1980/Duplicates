using Duplicates.Engine.Analysis.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Duplicates.Services;

public sealed class WindowsImageSampleProvider : IImageSampleProvider
{
    private readonly MediaFingerprintCache? _cache;
    private readonly Func<string, CancellationToken, Task<ImageSample>> _sampleFactory;

    public WindowsImageSampleProvider(MediaFingerprintCache? cache = null)
        : this(cache, DecodeAsync)
    {
    }

    internal WindowsImageSampleProvider(
        MediaFingerprintCache? cache,
        Func<string, CancellationToken, Task<ImageSample>> sampleFactory)
    {
        _cache = cache;
        _sampleFactory = sampleFactory;
    }

    public Task<ImageSample> GetSampleAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _cache is null
            ? GetFreshSampleAsync(path, cancellationToken)
            : _cache.GetOrCreateImageAsync(
                path,
                token => _sampleFactory(path, token),
                cancellationToken);
    }

    public async Task<ImageSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        ImageSample sample = await _sampleFactory(path, cancellationToken).ConfigureAwait(false);
        if (sample.Width <= 0 ||
            sample.Height <= 0 ||
            sample.Luminance32x32?.Length != 1024 ||
            string.IsNullOrWhiteSpace(sample.Format))
        {
            throw new InvalidDataException("The image sample is structurally invalid.");
        }

        return sample;
    }

    private static async Task<ImageSample> DecodeAsync(string path, CancellationToken cancellationToken)
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
        BitmapDecoder decoder = await WinRtAsync.AwaitAndCloseAsync(
                BitmapDecoder.CreateAsync(stream),
                cancellationToken)
            .ConfigureAwait(false);
        BitmapFrame frame = await WinRtAsync.AwaitAndCloseAsync(
                decoder.GetFrameAsync(0),
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        int width = checked((int)frame.OrientedPixelWidth);
        int height = checked((int)frame.OrientedPixelHeight);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("The image dimensions are invalid.");
        }

        byte[] luminance = await MediaLuminanceConverter.Decode32x32Async(frame, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        BitmapCodecInformation decoderInformation = decoder.DecoderInformation;
        return new ImageSample(width, height, luminance, GetFormat(decoderInformation.CodecId));
    }

    private static string GetFormat(Guid codecId)
    {
        if (codecId == BitmapDecoder.JpegDecoderId)
        {
            return "JPEG";
        }

        if (codecId == BitmapDecoder.PngDecoderId)
        {
            return "PNG";
        }

        if (codecId == BitmapDecoder.GifDecoderId)
        {
            return "GIF";
        }

        if (codecId == BitmapDecoder.BmpDecoderId)
        {
            return "BMP";
        }

        if (codecId == BitmapDecoder.TiffDecoderId)
        {
            return "TIFF";
        }

        if (codecId == BitmapDecoder.JpegXRDecoderId)
        {
            return "JPEG-XR";
        }

        if (codecId == BitmapDecoder.IcoDecoderId)
        {
            return "ICO";
        }

        if (codecId == BitmapDecoder.HeifDecoderId)
        {
            return "HEIF";
        }

        if (codecId == BitmapDecoder.WebpDecoderId)
        {
            return "WEBP";
        }

        return codecId.ToString();
    }
}

internal static class MediaLuminanceConverter
{
    public static async Task<byte[]> Decode32x32Async(
        BitmapFrame frame,
        CancellationToken cancellationToken)
    {
        var transform = new BitmapTransform
        {
            ScaledWidth = 32,
            ScaledHeight = 32,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        PixelDataProvider pixelData = await WinRtAsync.AwaitAndCloseAsync(
                frame.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bgra = pixelData.DetachPixelData();
        cancellationToken.ThrowIfCancellationRequested();
        if (bgra.Length != 4096)
        {
            throw new InvalidDataException("The decoded image buffer is not a tightly packed 32 x 32 BGRA image.");
        }

        return ConvertBgraToLuminance(bgra);
    }

    public static byte[] ConvertBgraToLuminance(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length % 4 != 0)
        {
            throw new InvalidDataException("The BGRA buffer length is invalid.");
        }

        byte[] luminance = new byte[bgra.Length / 4];
        for (int pixelIndex = 0; pixelIndex < luminance.Length; pixelIndex++)
        {
            int offset = pixelIndex * 4;
            int alpha = bgra[offset + 3];
            int blue = CompositeOnWhite(bgra[offset], alpha);
            int green = CompositeOnWhite(bgra[offset + 1], alpha);
            int red = CompositeOnWhite(bgra[offset + 2], alpha);
            luminance[pixelIndex] = (byte)((54 * red + 183 * green + 19 * blue) >> 8);
        }

        return luminance;
    }

    private static int CompositeOnWhite(int channel, int alpha) =>
        (channel * alpha + 255 * (255 - alpha) + 127) / 255;

}

internal static class WinRtAsync
{
    public static async Task<T> AwaitAndCloseAsync<T>(
        Windows.Foundation.IAsyncOperation<T> operation,
        CancellationToken cancellationToken)
    {
        Task<T> nativeTask = operation.AsTask();
        try
        {
            try
            {
                return await nativeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    operation.Cancel();
                }
                catch
                {
                }

                try
                {
                    await nativeTask.ConfigureAwait(false);
                }
                catch
                {
                }

                throw new OperationCanceledException(cancellationToken);
            }
        }
        finally
        {
            operation.Close();
        }
    }
}
