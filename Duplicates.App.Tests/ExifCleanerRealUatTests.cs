using System.Security.Cryptography;
using Duplicates.Models;
using Duplicates.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit.Abstractions;

namespace Duplicates.App.Tests;

public sealed class ExifCleanerRealUatTests : IDisposable
{
    private const string UatCategory = "Task17RealUat";

    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates-ExifCleaner-Uat",
        Guid.NewGuid().ToString("N"));

    public ExifCleanerRealUatTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, System.IO.FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", UatCategory)]
    public async Task DefaultClean_RealWicAndNtfsRemovesAvailablePrivacyMetadataWithoutChangingSource(
        bool jpeg)
    {
        RichFixture fixture = await CreateRichFixtureAsync(jpeg);
        byte[] sourceHash = await HashFileAsync(fixture.Path);
        var backend = new WicMetadataBackend();
        WicImageInspection before = backend.Inspect(fixture.Path);
        var recycle = new RecordingRecycleBinService();
        var service = new ExifCleanerService(backend, new ExifFileTransactions(), recycle);
        var progress = new InlineProgress();
        Assert.All(fixture.RequiredQueries, query => Assert.Contains(query, before.Metadata));

        ExifCleanResult result = await service.CleanAsync(
            CreateRequest(fixture.Path, replaceOriginal: false),
            progress,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.Succeeded, result.Outcome);
        string outputPath = Assert.IsType<string>(result.OutputPath);
        Assert.NotEqual(fixture.Path, outputPath);
        Assert.Equal(sourceHash, await HashFileAsync(fixture.Path));
        WicImageInspection after = backend.Inspect(outputPath);
        AssertRenderEquivalent(before.RenderState, after.RenderState);
        AssertSelectedQueriesAbsent(after, fixture.Container);
        Assert.Equal(1, progress.Values[^1]);
        Assert.Empty(recycle.Paths);
        AssertNoTransactionalResidue();
    }

    [Fact]
    [Trait("Category", UatCategory)]
    public async Task ReplaceOriginal_RealWicAndNtfsUsesRecycleSeamAndLeavesNoRollbackResidue()
    {
        RichFixture fixture = await CreateRichFixtureAsync(jpeg: true);
        var backend = new WicMetadataBackend();
        WicImageInspection before = backend.Inspect(fixture.Path);
        var recycle = new RecordingRecycleBinService();
        var service = new ExifCleanerService(backend, new ExifFileTransactions(), recycle);
        Assert.All(fixture.RequiredQueries, query => Assert.Contains(query, before.Metadata));

        ExifCleanResult result = await service.CleanAsync(
            CreateRequest(fixture.Path, replaceOriginal: true),
            new InlineProgress(),
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.Succeeded, result.Outcome);
        Assert.Equal(fixture.Path, result.OutputPath);
        string rollbackPath = Assert.Single(recycle.Paths);
        Assert.False(File.Exists(rollbackPath));
        WicImageInspection after = backend.Inspect(fixture.Path);
        AssertRenderEquivalent(before.RenderState, after.RenderState);
        AssertSelectedQueriesAbsent(after, WicContainerKind.Jpeg);
        AssertNoTransactionalResidue();
    }

    [Fact]
    [Trait("Category", UatCategory)]
    public async Task DefaultClean_RealNtfsPreservesCollisionAndSupportsReadOnlySource()
    {
        string source = await WriteImageAsync("read-only.jpg", BitmapEncoder.JpegEncoderId);
        string collision = Path.Combine(_root, "read-only.clean.jpg");
        byte[] collisionBytes = [9, 8, 7, 6];
        await File.WriteAllBytesAsync(collision, collisionBytes);
        File.SetAttributes(source, File.GetAttributes(source) | System.IO.FileAttributes.ReadOnly);
        byte[] sourceHash = await HashFileAsync(source);
        var service = new ExifCleanerService(
            new WicMetadataBackend(),
            new ExifFileTransactions(),
            new RecordingRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            CreateRequest(source, replaceOriginal: false),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.Succeeded, result.Outcome);
        Assert.Equal(Path.Combine(_root, "read-only.clean (2).jpg"), result.OutputPath);
        Assert.Equal(collisionBytes, await File.ReadAllBytesAsync(collision));
        Assert.Equal(sourceHash, await HashFileAsync(source));
        AssertNoTransactionalResidue();
    }

    [Fact]
    [Trait("Category", UatCategory)]
    public async Task LockedSource_RealNtfsFailsClosedWithoutArtifacts()
    {
        string source = await WriteImageAsync("locked.jpg", BitmapEncoder.JpegEncoderId);
        ExifCleanRequest request = CreateRequest(source, replaceOriginal: false);
        var service = new ExifCleanerService(
            new WicMetadataBackend(),
            new ExifFileTransactions(),
            new RecordingRecycleBinService());
        using var exclusive = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);

        ExifCleanResult result = await service.CleanAsync(request, null, CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.Failed, result.Outcome);
        Assert.Null(result.OutputPath);
        AssertNoTransactionalResidue();
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_root),
            path => Path.GetFileName(path).StartsWith("locked.clean", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", UatCategory)]
    public async Task UnsupportedPngAndMultiframeTiff_RealWicFailClosedWithoutArtifacts()
    {
        string png = await WriteImageAsync("unsupported.png", BitmapEncoder.PngEncoderId);
        string tiff = WriteTwoFrameTiff("multiframe.tiff");
        var service = new ExifCleanerService(
            new WicMetadataBackend(),
            new ExifFileTransactions(),
            new RecordingRecycleBinService());

        ExifCleanResult pngResult = await service.CleanAsync(
            CreateRequest(png, replaceOriginal: false),
            null,
            CancellationToken.None);
        ExifCleanResult tiffResult = await service.CleanAsync(
            CreateRequest(tiff, replaceOriginal: false),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.UnsupportedFormat, pngResult.Outcome);
        Assert.Equal(ExifCleanOutcome.UnsupportedFormat, tiffResult.Outcome);
        AssertNoTransactionalResidue();
    }

    [Fact]
    [Trait("Category", UatCategory)]
    public async Task UnpaddedJpegIptcLayout_RealWicReturnsUnsupportedLayoutWithoutArtifacts()
    {
        string source = await WriteImageAsync(
            "iptc-layout.jpg",
            BitmapEncoder.JpegEncoderId,
            new BitmapPropertySet
            {
                ["/app13/irb/8bimiptc/iptc/keywords"] =
                    new BitmapTypedValue(new[] { "alpha" }, PropertyType.StringArray),
            });
        byte[] sourceHash = await HashFileAsync(source);
        var service = new ExifCleanerService(
            new WicMetadataBackend(),
            new ExifFileTransactions(),
            new RecordingRecycleBinService());
        var options = new ExifCleanOptions(
            RemoveGps: false,
            RemoveDeviceIdentifiers: false,
            RemoveDates: false,
            RemoveAuthorAndDescription: false,
            RemoveEmbeddedThumbnail: false,
            RemoveXmpAndIptc: true,
            ReplaceOriginal: false);

        ExifCleanResult result = await service.CleanAsync(
            CreateRequest(source, options),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.UnsupportedMetadataLayout, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Equal(sourceHash, await HashFileAsync(source));
        AssertNoTransactionalResidue();
    }

    [Fact]
    [Trait("Category", UatCategory)]
    public async Task LargeJpegCancellation_RealChunkedCopyRemovesOwnedTempAndLeavesSource()
    {
        string source = await WriteImageAsync("large.jpg", BitmapEncoder.JpegEncoderId);
        await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(stream.Length + (16 * 1024 * 1024));
        }

        byte[] sourceHash = await HashFileAsync(source);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value > 0)
            {
                cancellation.Cancel();
            }
        });
        var service = new ExifCleanerService(
            new WicMetadataBackend(),
            new ExifFileTransactions(),
            new RecordingRecycleBinService());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CleanAsync(
            CreateRequest(source, replaceOriginal: false),
            progress,
            cancellation.Token));

        Assert.Equal(sourceHash, await HashFileAsync(source));
        AssertNoTransactionalResidue();
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_root),
            path => Path.GetFileName(path).StartsWith("large.clean", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<RichFixture> CreateRichFixtureAsync(bool jpeg)
    {
        WicContainerKind container = jpeg ? WicContainerKind.Jpeg : WicContainerKind.Tiff;
        string extension = jpeg ? ".jpg" : ".tiff";
        Guid encoder = jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.TiffEncoderId;
        string ifd = jpeg ? "/app1/ifd" : "/ifd";
        string gps = $"{ifd}/gps/{{ushort=1}}";
        string device = $"{ifd}/{{ushort=271}}";
        string date = $"{ifd}/{{ushort=306}}";
        string author = $"{ifd}/{{ushort=315}}";
        string description = $"{ifd}/{{ushort=270}}";
        var properties = new BitmapPropertySet
        {
            [$"{ifd}/{{ushort=274}}"] = new BitmapTypedValue((ushort)6, PropertyType.UInt16),
            [gps] = new BitmapTypedValue("N", PropertyType.String),
            [device] = new BitmapTypedValue("CameraCo", PropertyType.String),
            [date] = new BitmapTypedValue("2026:08:08 12:34:56", PropertyType.String),
            [author] = new BitmapTypedValue("Example Author", PropertyType.String),
            [description] = new BitmapTypedValue("Example Description", PropertyType.String),
        };
        var requiredQueries = new List<string>
        {
            jpeg ? "/app1/ifd/gps" : "/ifd/gps",
            device,
            date,
            author,
            description,
        };

        if (jpeg)
        {
            RecordUnavailable(
                "JPEG IPTC fixture",
                "the locked WinRT encoder creates an APP13 layout that the fast writer rejects with WINCODEC_ERR_PROPERTYSIZE, including PaddingSchema:Padding=4096");
        }
        else
        {
            properties["/ifd/iptc/keywords"] =
                new BitmapTypedValue(new[] { "alpha" }, PropertyType.StringArray);
            requiredQueries.Add("/ifd/iptc");
        }

        RecordUnavailable(
            jpeg ? "JPEG embedded-thumbnail fixture" : "TIFF embedded-thumbnail fixture",
            "the locked WinRT encoder cannot create a deterministic removable embedded thumbnail");

        RecordUnavailable(
            jpeg ? "JPEG XMP fixture" : "TIFF XMP fixture",
            "the locked WinRT encoder does not create an XMP block from the supported property API");
        string path = await WriteImageAsync($"rich{extension}", encoder, properties);
        await AddIccProfileIfAvailableAsync(path, jpeg);
        return new RichFixture(path, container, requiredQueries);
    }

    private async Task AddIccProfileIfAvailableAsync(string path, bool jpeg)
    {
        string profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool",
            "drivers",
            "color",
            "sRGB Color Space Profile.icm");
        if (!File.Exists(profilePath))
        {
            RecordUnavailable("ICC fixture", $"profile not installed at {profilePath}");
            return;
        }

        byte[] profile = await File.ReadAllBytesAsync(profilePath);
        if (jpeg)
        {
            AddJpegIccProfile(path, profile);
            return;
        }

        RecordUnavailable(
            "TIFF ICC fixture",
            "the locked WinRT TIFF encoder does not expose a deterministic color-profile writer");
    }

    private async Task<string> WriteImageAsync(
        string name,
        Guid encoderId,
        BitmapPropertySet? properties = null)
    {
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_root);
        StorageFile file = await folder.CreateFileAsync(name, CreationCollisionOption.FailIfExists);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        byte[] pixels =
        [
            0, 0, 255, 255,
            0, 255, 0, 255,
            255, 0, 0, 255,
            255, 255, 255, 255,
            32, 64, 128, 255,
            128, 64, 32, 255,
        ];
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            3,
            2,
            96,
            96,
            pixels);
        if (properties is not null)
        {
            await encoder.BitmapProperties.SetPropertiesAsync(properties);
        }

        await encoder.FlushAsync();
        return file.Path;
    }

    private string WriteTwoFrameTiff(string name)
    {
        string path = Path.Combine(_root, name);
        const uint firstIfd = 8;
        const uint secondIfd = 158;
        const uint firstXResolution = 308;
        const uint firstYResolution = 316;
        const uint secondXResolution = 324;
        const uint secondYResolution = 332;
        const uint firstPixel = 340;
        const uint secondPixel = 341;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write(firstIfd);
        WriteTiffIfd(writer, secondIfd, firstPixel, firstXResolution, firstYResolution);
        WriteTiffIfd(writer, 0, secondPixel, secondXResolution, secondYResolution);
        WriteRational(writer, 96, 1);
        WriteRational(writer, 96, 1);
        WriteRational(writer, 96, 1);
        WriteRational(writer, 96, 1);
        writer.Write((byte)0);
        writer.Write((byte)255);
        return path;
    }

    private static void WriteTiffIfd(
        BinaryWriter writer,
        uint nextIfd,
        uint pixelOffset,
        uint xResolutionOffset,
        uint yResolutionOffset)
    {
        writer.Write((ushort)12);
        WriteLongEntry(writer, 256, 1);
        WriteLongEntry(writer, 257, 1);
        WriteShortEntry(writer, 258, 8);
        WriteShortEntry(writer, 259, 1);
        WriteShortEntry(writer, 262, 1);
        WriteLongEntry(writer, 273, pixelOffset);
        WriteShortEntry(writer, 277, 1);
        WriteLongEntry(writer, 278, 1);
        WriteLongEntry(writer, 279, 1);
        WriteRationalEntry(writer, 282, xResolutionOffset);
        WriteRationalEntry(writer, 283, yResolutionOffset);
        WriteShortEntry(writer, 296, 2);
        writer.Write(nextIfd);
    }

    private static void WriteShortEntry(BinaryWriter writer, ushort tag, ushort value)
    {
        writer.Write(tag);
        writer.Write((ushort)3);
        writer.Write((uint)1);
        writer.Write(value);
        writer.Write((ushort)0);
    }

    private static void WriteLongEntry(BinaryWriter writer, ushort tag, uint value)
    {
        writer.Write(tag);
        writer.Write((ushort)4);
        writer.Write((uint)1);
        writer.Write(value);
    }

    private static void WriteRationalEntry(BinaryWriter writer, ushort tag, uint offset)
    {
        writer.Write(tag);
        writer.Write((ushort)5);
        writer.Write((uint)1);
        writer.Write(offset);
    }

    private static void WriteRational(BinaryWriter writer, uint numerator, uint denominator)
    {
        writer.Write(numerator);
        writer.Write(denominator);
    }

    private static ExifCleanRequest CreateRequest(string path, bool replaceOriginal) =>
        CreateRequest(
            path,
            new ExifCleanOptions(true, true, true, true, true, true, replaceOriginal));

    private static ExifCleanRequest CreateRequest(string path, ExifCleanOptions options)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return new ExifCleanRequest(
            info.FullName,
            info.Length,
            info.LastWriteTimeUtc,
            options);
    }

    private static void AssertSelectedQueriesAbsent(
        WicImageInspection inspection,
        WicContainerKind container)
    {
        IReadOnlyList<string> selected = ExifMetadataPolicy.AllQueries(container);
        Assert.DoesNotContain(selected, inspection.Metadata.ContainsKey);
    }

    private static void AssertRenderEquivalent(WicRenderState expected, WicRenderState actual)
    {
        Assert.Equal(expected.FrameCount, actual.FrameCount);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.DpiX, actual.DpiX, precision: 6);
        Assert.Equal(expected.DpiY, actual.DpiY, precision: 6);
        Assert.Equal(expected.Orientation, actual.Orientation);
        Assert.Equal(expected.ColorContexts, actual.ColorContexts);
        Assert.Equal(expected.PixelChecksum, actual.PixelChecksum);
    }

    private void AssertNoTransactionalResidue()
    {
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_root),
            path => Path.GetFileName(path).Contains(".duplicates-exif-", StringComparison.OrdinalIgnoreCase));
    }

    private void RecordUnavailable(string capability, string reason) =>
        _output.WriteLine($"UNAVAILABLE: {capability}: {reason}");

    private static async Task<byte[]> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream);
    }

    private static void AddJpegIccProfile(string path, byte[] profile)
    {
        byte[] image = File.ReadAllBytes(path);
        Assert.Equal((byte)0xFF, image[0]);
        Assert.Equal((byte)0xD8, image[1]);
        byte[] signature = System.Text.Encoding.ASCII.GetBytes("ICC_PROFILE\0");
        int payloadLength = checked(signature.Length + 2 + profile.Length);
        int segmentLength = checked(payloadLength + 2);
        Assert.InRange(segmentLength, 2, ushort.MaxValue);
        byte[] withProfile = new byte[checked(image.Length + payloadLength + 4)];
        withProfile[0] = 0xFF;
        withProfile[1] = 0xD8;
        withProfile[2] = 0xFF;
        withProfile[3] = 0xE2;
        withProfile[4] = checked((byte)(segmentLength >> 8));
        withProfile[5] = checked((byte)(segmentLength & 0xFF));
        signature.CopyTo(withProfile, 6);
        withProfile[6 + signature.Length] = 1;
        withProfile[7 + signature.Length] = 1;
        profile.CopyTo(withProfile, 8 + signature.Length);
        image.AsSpan(2).CopyTo(withProfile.AsSpan(8 + signature.Length + profile.Length));
        File.WriteAllBytes(path, withProfile);
    }

    private sealed record RichFixture(
        string Path,
        WicContainerKind Container,
        IReadOnlyList<string> RequiredQueries);

    private sealed class InlineProgress(Action<double>? callback = null) : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value)
        {
            Values.Add(value);
            callback?.Invoke(value);
        }
    }

    private sealed class RecordingRecycleBinService : IRecycleBinService
    {
        public List<string> Paths { get; } = [];

        public Task RecycleFileAsync(
            string path,
            FileSystemIdentity expectedIdentity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transactions = new ExifFileTransactions();
            ExifTrackedFile rollback = transactions.Capture(path);
            Assert.Equal(expectedIdentity, rollback.Identity);
            transactions.DeleteOwned(rollback);
            Paths.Add(path);
            return Task.CompletedTask;
        }
    }
}
