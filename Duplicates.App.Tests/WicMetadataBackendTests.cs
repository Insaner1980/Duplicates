using Duplicates.Services;
using Duplicates.Interop;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Duplicates.App.Tests;

public sealed class WicMetadataBackendTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates-WicMetadata",
        Guid.NewGuid().ToString("N"));

    public WicMetadataBackendTests()
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
    public async Task Fixture_JpegCanBeCreatedAndDeleted()
    {
        string path = await WriteImageAsync(
            "fixture.jpg",
            BitmapEncoder.JpegEncoderId,
            new BitmapPropertySet
            {
                ["/app1/ifd/{ushort=274}"] = new BitmapTypedValue((ushort)6, PropertyType.UInt16),
            });

        Assert.True(File.Exists(path));
        File.Delete(path);
    }

    [Fact]
    public void Interop_CanCreateAndReleaseFactory()
    {
        object factory = WicMetadataInterop.CreateFactory();

        WicMetadataInterop.Release(factory);
    }

    [Fact]
    public void Interop_CreateFactoryDoesNotRetainTheRawActivationReference()
    {
        object factory = WicMetadataInterop.CreateFactory();
        nint identity = Marshal.GetIUnknownForObject(factory);
        int remainingReferences = 0;
        try
        {
            remainingReferences = Marshal.Release(identity);

            Assert.Equal(2, remainingReferences);
        }
        finally
        {
            WicMetadataInterop.Release(factory);
            if (remainingReferences > 2)
            {
                _ = Marshal.Release(identity);
            }
        }
    }

    [Fact]
    public void Interop_ColorContextProfileBufferUsesSdkInOutArrayContract()
    {
        System.Reflection.MethodInfo method = typeof(WicMetadataInterop.IWICColorContext)
            .GetMethod(nameof(WicMetadataInterop.IWICColorContext.GetProfileBytes))!;
        System.Reflection.ParameterInfo buffer = method.GetParameters()[1];
        MarshalAsAttribute marshalAs = Assert.Single(
            buffer.GetCustomAttributes(typeof(MarshalAsAttribute), inherit: false)
                .Cast<MarshalAsAttribute>());

        Assert.True(buffer.IsIn);
        Assert.True(buffer.IsOut);
        Assert.Equal(UnmanagedType.LPArray, marshalAs.Value);
        Assert.Equal(0, marshalAs.SizeParamIndex);
    }

    [Fact]
    public async Task Interop_CanDecodeHeaderAndReleaseObjects()
    {
        string path = await WriteImageAsync("header.jpg", BitmapEncoder.JpegEncoderId, null);
        WicMetadataInterop.IWICImagingFactory? factory = null;
        WicMetadataInterop.IWICBitmapDecoder? decoder = null;
        WicMetadataInterop.IWICBitmapFrameDecode? frame = null;
        try
        {
            factory = WicMetadataInterop.CreateFactory();
            Assert.Equal(0, factory.CreateDecoderFromFilename(
                path,
                0,
                WicMetadataInterop.GenericRead,
                WicMetadataInterop.DecodeMetadataCacheOnDemand,
                out decoder));
            Assert.Equal(0, decoder.GetContainerFormat(out Guid container));
            Assert.Equal(WicMetadataInterop.JpegContainerFormat, container);
            Assert.Equal(0, decoder.GetFrameCount(out uint frameCount));
            Assert.Equal((uint)1, frameCount);
            Assert.Equal(0, decoder.GetFrame(0, out frame));
            Assert.Equal(0, frame.GetSize(out uint width, out uint height));
            Assert.Equal((uint)3, width);
            Assert.Equal((uint)2, height);
            Assert.Equal(0, frame.GetResolution(out _, out _));
            Assert.Equal(0, frame.GetPixelFormat(out _));
        }
        finally
        {
            WicMetadataInterop.Release(frame);
            WicMetadataInterop.Release(decoder);
            WicMetadataInterop.Release(factory);
        }
    }

    [Fact]
    public async Task Interop_CanReadAndClearMetadataValue()
    {
        string path = await WriteImageAsync(
            "metadata.jpg",
            BitmapEncoder.JpegEncoderId,
            new BitmapPropertySet
            {
                ["/app1/ifd/{ushort=274}"] = new BitmapTypedValue((ushort)6, PropertyType.UInt16),
            });
        WicMetadataInterop.IWICImagingFactory? factory = null;
        WicMetadataInterop.IWICBitmapDecoder? decoder = null;
        WicMetadataInterop.IWICBitmapFrameDecode? frame = null;
        WicMetadataInterop.IWICMetadataQueryReader? reader = null;
        nint value = Marshal.AllocCoTaskMem(24);
        try
        {
            Marshal.Copy(new byte[24], 0, value, 24);
            factory = WicMetadataInterop.CreateFactory();
            Assert.Equal(0, factory.CreateDecoderFromFilename(
                path,
                0,
                WicMetadataInterop.GenericRead,
                WicMetadataInterop.DecodeMetadataCacheOnDemand,
                out decoder));
            Assert.Equal(0, decoder.GetFrame(0, out frame));
            Assert.Equal(0, frame.GetMetadataQueryReader(out reader));
            Assert.Equal(0, reader.GetMetadataByName("/app1/ifd/{ushort=274}", value));
            Assert.Equal((ushort)18, Marshal.PtrToStructure<WicMetadataInterop.PropVariant>(value).VariantType);
        }
        finally
        {
            _ = WicMetadataInterop.PropVariantClear(value);
            Marshal.FreeCoTaskMem(value);
            WicMetadataInterop.Release(reader);
            WicMetadataInterop.Release(frame);
            WicMetadataInterop.Release(decoder);
            WicMetadataInterop.Release(factory);
        }
    }

    [Fact]
    public async Task Interop_CanReadColorContextCount()
    {
        string path = await WriteImageAsync("contexts.jpg", BitmapEncoder.JpegEncoderId, null);
        WicMetadataInterop.IWICImagingFactory? factory = null;
        WicMetadataInterop.IWICBitmapDecoder? decoder = null;
        WicMetadataInterop.IWICBitmapFrameDecode? frame = null;
        try
        {
            factory = WicMetadataInterop.CreateFactory();
            Assert.Equal(0, factory.CreateDecoderFromFilename(
                path,
                0,
                WicMetadataInterop.GenericRead,
                WicMetadataInterop.DecodeMetadataCacheOnDemand,
                out decoder));
            Assert.Equal(0, decoder.GetFrame(0, out frame));
            Assert.Equal(0, frame.GetColorContexts(0, null, out uint count));
            Assert.InRange(count, 0u, 16u);
        }
        finally
        {
            WicMetadataInterop.Release(frame);
            WicMetadataInterop.Release(decoder);
            WicMetadataInterop.Release(factory);
        }
    }

    [Fact]
    public async Task Interop_CanEnumerateMetadataQueryNames()
    {
        string path = await WriteImageAsync(
            "enumerator.jpg",
            BitmapEncoder.JpegEncoderId,
            new BitmapPropertySet
            {
                ["/app1/ifd/gps/{ushort=1}"] = new BitmapTypedValue("N", PropertyType.String),
            });
        WicMetadataInterop.IWICImagingFactory? factory = null;
        WicMetadataInterop.IWICBitmapDecoder? decoder = null;
        WicMetadataInterop.IWICBitmapFrameDecode? frame = null;
        WicMetadataInterop.IWICMetadataQueryReader? reader = null;
        WicMetadataInterop.IEnumString? enumerator = null;
        try
        {
            factory = WicMetadataInterop.CreateFactory();
            Assert.Equal(0, factory.CreateDecoderFromFilename(
                path,
                0,
                WicMetadataInterop.GenericRead,
                WicMetadataInterop.DecodeMetadataCacheOnDemand,
                out decoder));
            Assert.Equal(0, decoder.GetFrame(0, out frame));
            Assert.Equal(0, frame.GetMetadataQueryReader(out reader));
            Assert.Equal(0, reader.GetEnumerator(out enumerator));
            var names = new List<string>();
            while (true)
            {
                int result = enumerator.Next(1, out nint namePointer, out uint fetched);
                if (fetched == 0)
                {
                    Assert.True(result >= 0);
                    break;
                }

                try
                {
                    Assert.Equal((uint)1, fetched);
                    names.Add(Marshal.PtrToStringUni(namePointer)!);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(namePointer);
                }
            }

            Assert.Contains("/app1", names);
        }
        finally
        {
            if (enumerator is not null && Marshal.IsComObject(enumerator))
            {
                _ = Marshal.ReleaseComObject(enumerator);
            }

            WicMetadataInterop.Release(reader);
            WicMetadataInterop.Release(frame);
            WicMetadataInterop.Release(decoder);
            WicMetadataInterop.Release(factory);
        }
    }

    [Fact]
    public async Task Interop_CanInitializeBgraConverter()
    {
        string path = await WriteImageAsync("converter.jpg", BitmapEncoder.JpegEncoderId, null);
        WicMetadataInterop.IWICImagingFactory? factory = null;
        WicMetadataInterop.IWICBitmapDecoder? decoder = null;
        WicMetadataInterop.IWICBitmapFrameDecode? frame = null;
        WicMetadataInterop.IWICFormatConverter? converter = null;
        try
        {
            factory = WicMetadataInterop.CreateFactory();
            Assert.Equal(0, factory.CreateDecoderFromFilename(
                path,
                0,
                WicMetadataInterop.GenericRead,
                WicMetadataInterop.DecodeMetadataCacheOnDemand,
                out decoder));
            Assert.Equal(0, decoder.GetFrame(0, out frame));
            Assert.Equal(0, frame.GetPixelFormat(out Guid sourceFormat));
            Assert.Equal(0, factory.CreateFormatConverter(out converter));
            Assert.Equal(0, converter.CanConvert(
                sourceFormat,
                WicMetadataInterop.PixelFormat32Bgra,
                out bool canConvert));
            Assert.True(canConvert);
            Assert.Equal(0, converter.Initialize(
                frame,
                WicMetadataInterop.PixelFormat32Bgra,
                0,
                0,
                0,
                0));
            byte[] pixels = new byte[3 * 2 * 4];
            Assert.Equal(0, converter.CopyPixels(0, 3 * 4, (uint)pixels.Length, pixels));
            Assert.Contains(pixels, value => value != 0);
        }
        finally
        {
            WicMetadataInterop.Release(converter);
            WicMetadataInterop.Release(frame);
            WicMetadataInterop.Release(decoder);
            WicMetadataInterop.Release(factory);
        }
    }

    [Fact]
    public async Task Inspect_JpegReturnsSingleFrameRenderStateAndStableMetadataValues()
    {
        string path = await WriteImageAsync(
            "inspect.jpg",
            BitmapEncoder.JpegEncoderId,
            new BitmapPropertySet
            {
                ["/app1/ifd/{ushort=274}"] = new BitmapTypedValue((ushort)6, PropertyType.UInt16),
                ["/app1/ifd/{ushort=271}"] = new BitmapTypedValue("CameraCo", PropertyType.String),
            });
        var backend = new WicMetadataBackend();

        WicImageInspection inspection = backend.Inspect(path);

        Assert.Equal(WicContainerKind.Jpeg, inspection.Container);
        Assert.Equal(1, inspection.RenderState.FrameCount);
        Assert.Equal((uint)3, inspection.RenderState.Width);
        Assert.Equal((uint)2, inspection.RenderState.Height);
        Assert.Equal("VT_UI2:6", inspection.RenderState.Orientation);
        Assert.False(string.IsNullOrWhiteSpace(inspection.RenderState.PixelChecksum));
        Assert.Equal("VT_LPSTR:CameraCo", inspection.Metadata["/app1/ifd/{ushort=271}"]);
    }

    [Fact]
    public async Task RemoveMetadata_JpegFastWriterRemovesLeafAndPreservesRenderCriticalState()
    {
        string path = await WriteImageAsync(
            "remove.jpg",
            BitmapEncoder.JpegEncoderId,
            new BitmapPropertySet
            {
                ["/app1/ifd/{ushort=274}"] = new BitmapTypedValue((ushort)6, PropertyType.UInt16),
                ["/app1/ifd/{ushort=271}"] = new BitmapTypedValue("CameraCo", PropertyType.String),
            });
        var backend = new WicMetadataBackend();
        WicImageInspection before = backend.Inspect(path);

        backend.RemoveMetadata(path, WicContainerKind.Jpeg, ["/app1/ifd/{ushort=271}"]);
        WicImageInspection after = backend.Inspect(path);

        Assert.DoesNotContain("/app1/ifd/{ushort=271}", after.Metadata.Keys);
        Assert.Equal(before.RenderState.Orientation, after.RenderState.Orientation);
        Assert.Equal(before.RenderState.Width, after.RenderState.Width);
        Assert.Equal(before.RenderState.Height, after.RenderState.Height);
        Assert.Equal(before.RenderState.DpiX, after.RenderState.DpiX);
        Assert.Equal(before.RenderState.DpiY, after.RenderState.DpiY);
        Assert.Equal(before.RenderState.ColorContexts, after.RenderState.ColorContexts);
        Assert.Equal(before.RenderState.PixelChecksum, after.RenderState.PixelChecksum);

        string moved = path + ".moved";
        File.Move(path, moved);
        File.Delete(moved);
    }

    [Fact]
    public async Task RemoveMetadata_MissingQueryIsIdempotentAndClosesEveryComObject()
    {
        string path = await WriteImageAsync("missing.tiff", BitmapEncoder.TiffEncoderId, null);
        var backend = new WicMetadataBackend();

        backend.RemoveMetadata(path, WicContainerKind.Tiff, ["/ifd/exif/{ushort=42037}"]);
        WicImageInspection inspection = backend.Inspect(path);

        Assert.Equal(WicContainerKind.Tiff, inspection.Container);
        Assert.Equal(1, inspection.RenderState.FrameCount);
        string moved = path + ".moved";
        File.Move(path, moved);
        File.Delete(moved);
    }

    [Fact]
    public async Task RemoveMetadata_MissingJpegSubtreeAfterOtherRemovalsIsIdempotent()
    {
        string path = await WriteImageAsync(
            "missing-subtree.jpg",
            BitmapEncoder.JpegEncoderId,
            new BitmapPropertySet
            {
                ["/app1/ifd/gps/{ushort=1}"] = new BitmapTypedValue("N", PropertyType.String),
                ["/app1/ifd/{ushort=271}"] = new BitmapTypedValue("CameraCo", PropertyType.String),
                ["/app1/ifd/{ushort=306}"] = new BitmapTypedValue("2026:08:08 12:34:56", PropertyType.String),
                ["/app1/ifd/{ushort=315}"] = new BitmapTypedValue("Example Author", PropertyType.String),
            });
        var backend = new WicMetadataBackend();

        backend.RemoveMetadata(
            path,
            WicContainerKind.Jpeg,
            ExifMetadataPolicy.AllQueries(WicContainerKind.Jpeg));
        WicImageInspection inspection = backend.Inspect(path);

        Assert.Equal(WicContainerKind.Jpeg, inspection.Container);
        Assert.DoesNotContain(
            ExifMetadataPolicy.AllQueries(WicContainerKind.Jpeg),
            inspection.Metadata.ContainsKey);
    }

    [Fact]
    public async Task Inspect_JpegReadsExactIccProfileAndClosesEveryComObject()
    {
        string path = await WriteImageAsync("profile.jpg", BitmapEncoder.JpegEncoderId, null);
        string profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool",
            "drivers",
            "color",
            "sRGB Color Space Profile.icm");
        byte[] profile = await File.ReadAllBytesAsync(profilePath, TestContext.Current.CancellationToken);
        AddIccProfile(path, profile);
        var backend = new WicMetadataBackend();

        WicImageInspection inspection = backend.Inspect(path);

        string expectedProfile = Convert.ToHexString(XxHash3.Hash(profile));
        string context = Assert.Single(inspection.RenderState.ColorContexts);
        Assert.Equal($"type:1;exif:none;profile:{expectedProfile}", context);

        string moved = path + ".moved";
        File.Move(path, moved);
        File.Delete(moved);
    }

    public static IEnumerable<object[]> DisabledSubtreeMutationCases()
    {
        yield return [true, "jpeg-gps.jpg", "/app1/ifd/gps", "/app1/ifd/gps/{ushort=1}", 0];
        yield return [true, "jpeg-iptc.jpg", "/app13/irb/8bimiptc/iptc", "/app13/irb/8bimiptc/iptc/keywords", 2];
        yield return [true, "jpeg-thumb.jpg", "/app1/thumb", "/app1/thumb/{ushort=274}", 3];
        yield return [false, "tiff-gps.tiff", "/ifd/gps", "/ifd/gps/{ushort=1}", 0];
        yield return [false, "tiff-iptc.tiff", "/ifd/iptc", "/ifd/iptc/keywords", 2];
        yield return [false, "tiff-irb-iptc.tiff", "/ifd/irb/8bimiptc/iptc", "/ifd/irb/8bimiptc/iptc/keywords", 2];
    }

    [Theory]
    [MemberData(nameof(DisabledSubtreeMutationCases))]
    public async Task Inspect_DisabledSubtreeLeafMutationChangesMetadataSnapshot(
        bool jpeg,
        string name,
        string subtree,
        string leaf,
        int valueKind)
    {
        Guid encoderId = jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.TiffEncoderId;
        string path = await WriteImageAsync(
            name,
            encoderId,
            new BitmapPropertySet { [leaf] = FixtureValue(valueKind, mutated: false) });
        Assert.Equal(
            FixtureValueText(valueKind, mutated: false),
            await ReadPropertyTextAsync(path, leaf));
        var backend = new WicMetadataBackend();
        WicImageInspection before = backend.Inspect(path);

        await SetPropertyInPlaceAsync(path, leaf, FixtureValue(valueKind, mutated: true));
        Assert.Equal(
            FixtureValueText(valueKind, mutated: true),
            await ReadPropertyTextAsync(path, leaf));
        WicImageInspection after = backend.Inspect(path);

        Assert.StartsWith("VT_UNKNOWN:TREE:SHA256:", before.Metadata[subtree], StringComparison.Ordinal);
        Assert.StartsWith("VT_UNKNOWN:TREE:SHA256:", after.Metadata[subtree], StringComparison.Ordinal);
        Assert.NotEqual(MetadataSnapshot(before.Metadata), MetadataSnapshot(after.Metadata));
    }

    [Theory]
    [InlineData(unchecked((int)0x88982F52))]
    [InlineData(unchecked((int)0x88982F41))]
    [InlineData(unchecked((int)0x88982F81))]
    [InlineData(unchecked((int)0x88982F42))]
    public void IsMetadataLayoutFailure_RecognizesEveryFrozenWicCapabilityHResult(int hresult)
    {
        Assert.True(WicMetadataBackend.IsMetadataLayoutFailure(hresult));
    }

    [Fact]
    public void IsMetadataLayoutFailure_DoesNotMisclassifyPropertyNotFound()
    {
        Assert.False(WicMetadataBackend.IsMetadataLayoutFailure(unchecked((int)0x88982F40)));
    }

    [Fact]
    public void MetadataQueryPreflight_PropertyNotFoundIsAbsent()
    {
        Assert.False(WicMetadataBackend.MetadataExistsFromResult(
            unchecked((int)0x88982F40),
            "/app1/thumb"));
    }

    [Fact]
    public void MetadataQueryPreflight_InvalidPropertySizeIsLayoutFailure()
    {
        Assert.Throws<WicMetadataLayoutException>(() =>
            WicMetadataBackend.MetadataExistsFromResult(
                unchecked((int)0x88982F42),
                "/app13/irb/8bimiptc/iptc"));
    }

    private async Task<string> WriteImageAsync(
        string name,
        Guid encoderId,
        BitmapPropertySet? properties)
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

    private static async Task SetPropertyInPlaceAsync(
        string path,
        string query,
        BitmapTypedValue value)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        BitmapEncoder encoder = await BitmapEncoder.CreateForInPlacePropertyEncodingAsync(decoder);
        await encoder.BitmapProperties.SetPropertiesAsync(
            new BitmapPropertySet { [query] = value });
        await encoder.FlushAsync();
    }

    private static async Task<string> ReadPropertyTextAsync(string path, string query)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        BitmapFrame frame = await decoder.GetFrameAsync(0);
        BitmapPropertySet properties = await frame.BitmapProperties.GetPropertiesAsync([query]);
        BitmapTypedValue value = properties[query];
        return value.Value switch
        {
            string text => text,
            ushort number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            uint number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string[] values => string.Join("|", values),
            _ => value.Value?.ToString() ?? string.Empty,
        };
    }

    private static BitmapTypedValue FixtureValue(int kind, bool mutated) => kind switch
    {
        0 => new BitmapTypedValue(mutated ? "S" : "N", PropertyType.String),
        2 => new BitmapTypedValue(
            mutated ? new[] { "bravo" } : new[] { "alpha" },
            PropertyType.StringArray),
        3 => new BitmapTypedValue(mutated ? (ushort)2 : (ushort)1, PropertyType.UInt16),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string FixtureValueText(int kind, bool mutated) => kind switch
    {
        0 => mutated ? "S" : "N",
        2 => mutated ? "bravo" : "alpha",
        3 => mutated ? "2" : "1",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string MetadataSnapshot(IReadOnlyDictionary<string, string> metadata) =>
        string.Join(
            "\n",
            metadata.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}={pair.Value}"));

    private static void AddIccProfile(string path, byte[] profile)
    {
        byte[] image = File.ReadAllBytes(path);
        Assert.Equal((byte)0xFF, image[0]);
        Assert.Equal((byte)0xD8, image[1]);
        byte[] signature = Encoding.ASCII.GetBytes("ICC_PROFILE\0");
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
}
