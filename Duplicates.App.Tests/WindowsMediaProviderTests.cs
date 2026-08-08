using Duplicates.Engine.Analysis.Media;
using Duplicates.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.UI;

namespace Duplicates.App.Tests;

public sealed class WindowsMediaProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Duplicates.App.Tests", Guid.NewGuid().ToString("N"));

    public WindowsMediaProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ImageFreshSample_BypassesCacheWithoutChangingCachedSample()
    {
        string path = await WriteSourceAsync("image.png", [1]);
        string cachePath = Path.Combine(_root, "image-cache.json");
        int coreCalls = 0;
        var provider = new WindowsImageSampleProvider(
            new MediaFingerprintCache(cachePath),
            (_, _) => Task.FromResult(Image((byte)++coreCalls)));

        ImageSample first = await provider.GetSampleAsync(path, CancellationToken.None);
        ImageSample hit = await provider.GetSampleAsync(path, CancellationToken.None);
        byte[] beforeFresh = await File.ReadAllBytesAsync(cachePath);
        ImageSample fresh = await provider.GetFreshSampleAsync(path, CancellationToken.None);
        ImageSample stillCached = await provider.GetSampleAsync(path, CancellationToken.None);

        Assert.Equal(2, coreCalls);
        Assert.Equal(1, first.Luminance32x32[0]);
        Assert.Equal(1, hit.Luminance32x32[0]);
        Assert.Equal(2, fresh.Luminance32x32[0]);
        Assert.Equal(1, stillCached.Luminance32x32[0]);
        Assert.Equal(beforeFresh, await File.ReadAllBytesAsync(cachePath));
    }

    [Fact]
    public async Task VideoFreshSample_BypassesCacheWithoutChangingCachedSample()
    {
        string path = await WriteSourceAsync("video.mp4", [1]);
        string cachePath = Path.Combine(_root, "video-cache.json");
        int coreCalls = 0;
        var provider = new WindowsVideoSampleProvider(
            new MediaFingerprintCache(cachePath),
            (_, _) => Task.FromResult(Video((byte)++coreCalls)));

        VideoSample first = await provider.GetSampleAsync(path, CancellationToken.None);
        VideoSample hit = await provider.GetSampleAsync(path, CancellationToken.None);
        byte[] beforeFresh = await File.ReadAllBytesAsync(cachePath);
        VideoSample fresh = await provider.GetFreshSampleAsync(path, CancellationToken.None);
        VideoSample stillCached = await provider.GetSampleAsync(path, CancellationToken.None);

        Assert.Equal(2, coreCalls);
        Assert.Equal(1, first.LuminanceFrames32x32[0][0]);
        Assert.Equal(1, hit.LuminanceFrames32x32[0][0]);
        Assert.Equal(2, fresh.LuminanceFrames32x32[0][0]);
        Assert.Equal(1, stillCached.LuminanceFrames32x32[0][0]);
        Assert.Equal(beforeFresh, await File.ReadAllBytesAsync(cachePath));
    }

    [Fact]
    public async Task VideoFreshSample_RejectsEmptyCodec()
    {
        string path = await WriteSourceAsync("missing-codec.mp4", [1]);
        var provider = new WindowsVideoSampleProvider(
            cache: null,
            (_, _) => Task.FromResult(Video(1) with { Codec = string.Empty }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetFreshSampleAsync(path, CancellationToken.None));
    }

    [Theory]
    [InlineData("generated.png", "PNG")]
    [InlineData("generated.jpg", "JPEG")]
    public async Task ImageProvider_DecodesGeneratedImageWithStableFormatAndDimensions(
        string fileName,
        string expectedFormat)
    {
        string path = await WriteSolidImageAsync(fileName);

        ImageSample sample = await new WindowsImageSampleProvider().GetFreshSampleAsync(
            path,
            CancellationToken.None);

        Assert.Equal(2, sample.Width);
        Assert.Equal(1, sample.Height);
        Assert.Equal(expectedFormat, sample.Format);
        Assert.Equal(1024, sample.Luminance32x32.Length);
    }

    [Theory]
    [InlineData("released.png")]
    [InlineData("released.jpg")]
    public async Task ImageProvider_ReleasesSourceAfterDecode(string fileName)
    {
        string path = await WriteSolidImageAsync(fileName);
        byte[] original = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, original);
        MoveAwayAndBack(path);
        long length = new FileInfo(path).Length;

        _ = await new WindowsImageSampleProvider().GetFreshSampleAsync(path, CancellationToken.None);

        MoveAwayAndBack(path);
        await File.WriteAllBytesAsync(path, new byte[length]);
        Assert.Equal(length, new FileInfo(path).Length);
    }

    [Fact]
    public async Task VideoProvider_UsesCodedDimensionsAndDisplayRenderedPixelsForRotate90()
    {
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_root);
        StorageFile image = await WriteAsymmetricImageAsync(folder);
        StorageFile video = await folder.CreateFileAsync("rotated.mp4", CreationCollisionOption.ReplaceExisting);
        var composition = new MediaComposition();
        composition.Clips.Add(await MediaClip.CreateFromImageFileAsync(image, TimeSpan.FromSeconds(1)));
        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Wvga);
        Guid rotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1");
        profile.Video.Properties.Add(rotationKey, PropertyValue.CreateInt32(90));
        Assert.Equal(
            TranscodeFailureReason.None,
            await composition.RenderToFileAsync(video, MediaTrimmingPreference.Precise, profile));
        VideoProperties properties = await video.Properties.GetVideoPropertiesAsync();
        Assert.Equal(VideoOrientation.Rotate90, properties.Orientation);
        byte[] original = await File.ReadAllBytesAsync(video.Path);
        await File.WriteAllBytesAsync(video.Path, original);
        MoveAwayAndBack(video.Path);

        VideoSample sample = await new WindowsVideoSampleProvider().GetFreshSampleAsync(
            video.Path,
            CancellationToken.None);
        byte[] frame = sample.LuminanceFrames32x32[0];
        double leftRight = Math.Abs(Average(frame, 0, 0, 16, 32) - Average(frame, 16, 0, 16, 32));
        double topBottom = Math.Abs(Average(frame, 0, 0, 32, 16) - Average(frame, 0, 16, 32, 16));

        Assert.Equal(480, sample.Width);
        Assert.Equal(800, sample.Height);
        Assert.True(topBottom > leftRight, $"top/bottom={topBottom}, left/right={leftRight}");
        MoveAwayAndBack(video.Path);
        await File.WriteAllBytesAsync(video.Path, new byte[original.Length]);
    }

    [Fact]
    public void ConvertBgraToLuminance_UsesChannelOrderAndExactAlphaComposite()
    {
        byte[] bgra =
        [
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
            41, 91, 173, 0,
            0, 0, 255, 128,
        ];

        byte[] luminance = MediaLuminanceConverter.ConvertBgraToLuminance(bgra);

        Assert.Equal([18, 182, 53, 255, 154], luminance);
    }

    [Theory]
    [InlineData(1920u, 1080u, 1u, 1u, true, VideoOrientation.Normal, 1920, 1080, 1.7777777777777777)]
    [InlineData(720u, 480u, 8u, 9u, true, VideoOrientation.Normal, 720, 480, 1.3333333333333333)]
    [InlineData(1920u, 1080u, 1u, 1u, true, VideoOrientation.Rotate90, 1080, 1920, 0.5625)]
    [InlineData(720u, 480u, 8u, 9u, true, VideoOrientation.Rotate270, 480, 720, 0.75)]
    [InlineData(640u, 480u, 0u, 0u, false, VideoOrientation.Normal, 640, 480, 1.3333333333333333)]
    public void CalculateDisplayGeometry_UsesParAndOrientation(
        uint codedWidth,
        uint codedHeight,
        uint parNumerator,
        uint parDenominator,
        bool hasPixelAspectRatio,
        VideoOrientation orientation,
        int expectedWidth,
        int expectedHeight,
        double expectedAspectRatio)
    {
        VideoDisplayGeometry result = WindowsVideoSampleProvider.CalculateDisplayGeometry(
            codedWidth,
            codedHeight,
            parNumerator,
            parDenominator,
            hasPixelAspectRatio,
            orientation);

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
        Assert.Equal(expectedAspectRatio, result.AspectRatio, 12);
    }

    [Theory]
    [InlineData(0u, 480u, 1u, 1u, true)]
    [InlineData(640u, 0u, 1u, 1u, true)]
    [InlineData(640u, 480u, 0u, 1u, true)]
    [InlineData(640u, 480u, 1u, 0u, true)]
    public void CalculateDisplayGeometry_RejectsStructurallyInvalidRatios(
        uint codedWidth,
        uint codedHeight,
        uint parNumerator,
        uint parDenominator,
        bool hasPixelAspectRatio)
    {
        Assert.Throws<InvalidDataException>(() => WindowsVideoSampleProvider.CalculateDisplayGeometry(
            codedWidth,
            codedHeight,
            parNumerator,
            parDenominator,
            hasPixelAspectRatio,
            VideoOrientation.Normal));
    }

    [Fact]
    public void AppServices_ComposesOneCacheAndAllMediaProviders()
    {
        var services = new AppServices();

        Assert.IsType<MediaFingerprintCache>(services.MediaFingerprintCache);
        Assert.IsType<WindowsImageSampleProvider>(services.ImageSampleProvider);
        Assert.IsType<WindowsVideoSampleProvider>(services.VideoSampleProvider);
        Assert.IsType<WindowsMusicMetadataProvider>(services.MusicMetadataProvider);
    }

    private async Task<string> WriteSourceAsync(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private async Task<string> WriteSolidImageAsync(string name)
    {
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_root);
        StorageFile file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        Guid encoderId = string.Equals(Path.GetExtension(name), ".png", StringComparison.OrdinalIgnoreCase)
            ? BitmapEncoder.PngEncoderId
            : BitmapEncoder.JpegEncoderId;
        BitmapEncoder? encoder = null;
        var createOperation = BitmapEncoder.CreateAsync(encoderId, stream);
        try
        {
            encoder = await createOperation;
        }
        finally
        {
            MediaLuminanceConverter.ReleaseNativeObject(createOperation);
        }

        try
        {
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                2,
                1,
                96,
                96,
                [0, 0, 255, 255, 0, 0, 255, 255]);
            var flushOperation = encoder.FlushAsync();
            try
            {
                await flushOperation;
            }
            finally
            {
                MediaLuminanceConverter.ReleaseNativeObject(flushOperation);
            }
        }
        finally
        {
            MediaLuminanceConverter.ReleaseNativeObject(encoder);
        }

        return file.Path;
    }

    private static async Task<StorageFile> WriteAsymmetricImageAsync(StorageFolder folder)
    {
        StorageFile file = await folder.CreateFileAsync("asymmetric.png", CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder? encoder = null;
        var createOperation = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        try
        {
            encoder = await createOperation;
        }
        finally
        {
            MediaLuminanceConverter.ReleaseNativeObject(createOperation);
        }

        try
        {
            byte[] pixels = new byte[64 * 32 * 4];
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    int offset = ((y * 64) + x) * 4;
                    byte value = x < 32 ? (byte)0 : (byte)255;
                    pixels[offset] = value;
                    pixels[offset + 1] = value;
                    pixels[offset + 2] = value;
                    pixels[offset + 3] = 255;
                }
            }

            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 64, 32, 96, 96, pixels);
            var flushOperation = encoder.FlushAsync();
            try
            {
                await flushOperation;
            }
            finally
            {
                MediaLuminanceConverter.ReleaseNativeObject(flushOperation);
            }
        }
        finally
        {
            MediaLuminanceConverter.ReleaseNativeObject(encoder);
        }

        return file;
    }

    private static double Average(byte[] frame, int x, int y, int width, int height)
    {
        long total = 0;
        for (int row = y; row < y + height; row++)
        {
            for (int column = x; column < x + width; column++)
            {
                total += frame[(row * 32) + column];
            }
        }

        return (double)total / (width * height);
    }

    private static void MoveAwayAndBack(string path)
    {
        string movedPath = path + ".moved";
        File.Move(path, movedPath);
        File.Move(movedPath, path);
    }

    private static ImageSample Image(byte value) =>
        new(64, 32, Enumerable.Repeat(value, 1024).ToArray(), "PNG");

    private static VideoSample Video(byte value) => new(
        1920,
        1080,
        16d / 9d,
        TimeSpan.FromSeconds(10),
        1_000_000,
        30,
        "H264",
        Enumerable.Range(0, 5)
            .Select(index => Enumerable.Repeat((byte)(value + index), 1024).ToArray())
            .ToArray());
}
