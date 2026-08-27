using System.Runtime.InteropServices;
using System.Text;

namespace Duplicates.Interop;

internal static class WicMetadataInterop
{
    internal static readonly Guid FactoryClassId = new("317d06e8-5f24-433d-bdf7-79ce68d8abc2");
    internal static readonly Guid FactoryInterfaceId = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    internal static readonly Guid JpegContainerFormat = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
    internal static readonly Guid TiffContainerFormat = new("163bcc30-e2e9-4f0b-961d-a3e9fdb788a3");
    internal static readonly Guid PixelFormat32Bgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint DecodeMetadataCacheOnDemand = 0;
    internal const int PropertyNotFound = unchecked((int)0x88982F40);

    internal static IWICImagingFactory CreateFactory()
    {
        int result = CoCreateInstance(
            FactoryClassId,
            0,
            1,
            FactoryInterfaceId,
            out nint instance);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            return (IWICImagingFactory)Marshal.GetObjectForIUnknown(instance);
        }
        finally
        {
            _ = Marshal.Release(instance);
        }
    }

    internal static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            _ = Marshal.FinalReleaseComObject(instance);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)]
        internal ushort VariantType;

        [FieldOffset(8)]
        internal sbyte Int8;

        [FieldOffset(8)]
        internal byte UInt8;

        [FieldOffset(8)]
        internal short Int16;

        [FieldOffset(8)]
        internal ushort UInt16;

        [FieldOffset(8)]
        internal int Int32;

        [FieldOffset(8)]
        internal uint UInt32;

        [FieldOffset(8)]
        internal long Int64;

        [FieldOffset(8)]
        internal ulong UInt64;

        [FieldOffset(8)]
        internal float Float;

        [FieldOffset(8)]
        internal double Double;

        [FieldOffset(8)]
        internal nint Pointer;

        [FieldOffset(8)]
        internal uint ElementCount;

        [FieldOffset(16)]
        internal nint Elements;
    }

    [ComImport]
    [Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICImagingFactory
    {
        [PreserveSig]
        int CreateDecoderFromFilename(
            [MarshalAs(UnmanagedType.LPWStr)] string fileName,
            nint vendor,
            uint desiredAccess,
            uint metadataOptions,
            out IWICBitmapDecoder decoder);

        [PreserveSig] int CreateDecoderFromStream(nint stream, nint vendor, uint metadataOptions, out nint decoder);
        [PreserveSig] int CreateDecoderFromFileHandle(nint fileHandle, nint vendor, uint metadataOptions, out nint decoder);
        [PreserveSig] int CreateComponentInfo(in Guid component, out nint componentInfo);
        [PreserveSig] int CreateDecoder(in Guid containerFormat, nint vendor, out nint decoder);
        [PreserveSig] int CreateEncoder(in Guid containerFormat, nint vendor, out nint encoder);
        [PreserveSig] int CreatePalette(out nint palette);

        [PreserveSig]
        int CreateFormatConverter(out IWICFormatConverter converter);

        [PreserveSig] int CreateBitmapScaler(out nint scaler);
        [PreserveSig] int CreateBitmapClipper(out nint clipper);
        [PreserveSig] int CreateBitmapFlipRotator(out nint rotator);
        [PreserveSig] int CreateStream(out nint stream);

        [PreserveSig]
        int CreateColorContext(out IWICColorContext colorContext);

        [PreserveSig] int CreateColorTransformer(out nint colorTransformer);
        [PreserveSig] int CreateBitmap(uint width, uint height, in Guid pixelFormat, uint cacheOption, out nint bitmap);
        [PreserveSig] int CreateBitmapFromSource(nint source, uint cacheOption, out nint bitmap);
        [PreserveSig] int CreateBitmapFromSourceRect(nint source, uint x, uint y, uint width, uint height, out nint bitmap);
        [PreserveSig] int CreateBitmapFromMemory(uint width, uint height, in Guid pixelFormat, uint stride, uint bufferSize, nint buffer, out nint bitmap);
        [PreserveSig] int CreateBitmapFromHBITMAP(nint bitmap, nint palette, uint options, out nint output);
        [PreserveSig] int CreateBitmapFromHICON(nint icon, out nint output);
        [PreserveSig] int CreateComponentEnumerator(uint componentTypes, uint options, out nint enumerator);
        [PreserveSig] int CreateFastMetadataEncoderFromDecoder(nint decoder, out nint encoder);

        [PreserveSig]
        int CreateFastMetadataEncoderFromFrameDecode(
            IWICBitmapFrameDecode frame,
            out IWICFastMetadataEncoder encoder);

        [PreserveSig] int CreateQueryWriter(in Guid metadataFormat, nint vendor, out nint writer);
        [PreserveSig] int CreateQueryWriterFromReader(nint reader, nint vendor, out nint writer);
    }

    [ComImport]
    [Guid("9EDDE9E7-8DEE-47ea-99DF-E6FAF2ED44BF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICBitmapDecoder
    {
        [PreserveSig] int QueryCapability(nint stream, out uint capability);
        [PreserveSig] int Initialize(nint stream, uint cacheOptions);
        [PreserveSig] int GetContainerFormat(out Guid containerFormat);
        [PreserveSig] int GetDecoderInfo(out nint decoderInfo);
        [PreserveSig] int CopyPalette(nint palette);
        [PreserveSig] int GetMetadataQueryReader(out IWICMetadataQueryReader queryReader);
        [PreserveSig] int GetPreview(out nint preview);
        [PreserveSig] int GetColorContexts(uint count, nint contexts, out uint actualCount);
        [PreserveSig] int GetThumbnail(out nint thumbnail);
        [PreserveSig] int GetFrameCount(out uint frameCount);

        [PreserveSig]
        int GetFrame(uint index, out IWICBitmapFrameDecode frame);
    }

    [ComImport]
    [Guid("3B16811B-6A43-4ec9-A813-3D930C13B940")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICBitmapFrameDecode
    {
        [PreserveSig] int GetSize(out uint width, out uint height);
        [PreserveSig] int GetPixelFormat(out Guid pixelFormat);
        [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
        [PreserveSig] int CopyPalette(nint palette);
        [PreserveSig] int CopyPixels(nint rectangle, uint stride, uint bufferSize, [Out] byte[] buffer);
        [PreserveSig] int GetMetadataQueryReader(out IWICMetadataQueryReader queryReader);

        [PreserveSig]
        int GetColorContexts(
            uint count,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IWICColorContext[]? contexts,
            out uint actualCount);

        [PreserveSig] int GetThumbnail(out nint thumbnail);
    }

    [ComImport]
    [Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICFormatConverter
    {
        [PreserveSig] int GetSize(out uint width, out uint height);
        [PreserveSig] int GetPixelFormat(out Guid pixelFormat);
        [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
        [PreserveSig] int CopyPalette(nint palette);
        [PreserveSig]
        int CopyPixels(
            nint rectangle,
            uint stride,
            uint bufferSize,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer);

        [PreserveSig]
        int Initialize(
            IWICBitmapFrameDecode source,
            in Guid destinationFormat,
            uint dither,
            nint palette,
            double alphaThreshold,
            uint paletteTranslate);

        [PreserveSig] int CanConvert(in Guid sourceFormat, in Guid destinationFormat, [MarshalAs(UnmanagedType.Bool)] out bool canConvert);
    }

    [ComImport]
    [Guid("00000101-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumString
    {
        [PreserveSig] int Next(uint count, out nint element, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumString enumerator);
    }

    [ComImport]
    [Guid("30989668-E1C9-4597-B395-458EEDB808DF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICMetadataQueryReader
    {
        [PreserveSig] int GetContainerFormat(out Guid metadataFormat);
        [PreserveSig] int GetLocation(uint characterCount, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder? location, out uint actualLength);
        [PreserveSig] int GetMetadataByName([MarshalAs(UnmanagedType.LPWStr)] string name, nint value);
        [PreserveSig] int GetEnumerator(out IEnumString enumerator);
    }

    [ComImport]
    [Guid("a721791a-0def-4d06-bd91-2118bf1db10b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICMetadataQueryWriter
    {
        [PreserveSig] int GetContainerFormat(out Guid metadataFormat);
        [PreserveSig] int GetLocation(uint characterCount, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder? location, out uint actualLength);
        [PreserveSig] int GetMetadataByName([MarshalAs(UnmanagedType.LPWStr)] string name, nint value);
        [PreserveSig] int GetEnumerator(out IEnumString enumerator);
        [PreserveSig] int SetMetadataByName([MarshalAs(UnmanagedType.LPWStr)] string name, nint value);
        [PreserveSig] int RemoveMetadataByName([MarshalAs(UnmanagedType.LPWStr)] string name);
    }

    [ComImport]
    [Guid("b84e2c09-78c9-4ac4-8bd3-524ae1663a2f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICFastMetadataEncoder
    {
        [PreserveSig] int Commit();
        [PreserveSig] int GetMetadataQueryWriter(out IWICMetadataQueryWriter queryWriter);
    }

    [ComImport]
    [Guid("3C613A02-34B2-44ea-9A7C-45AEA9C6FD6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWICColorContext
    {
        [PreserveSig] int InitializeFromFilename([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        [PreserveSig] int InitializeFromMemory([In] byte[] buffer, uint bufferSize);
        [PreserveSig] int InitializeFromExifColorSpace(uint value);
        [PreserveSig] int GetType(out uint contextType);
        [PreserveSig]
        int GetProfileBytes(
            uint bufferSize,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] byte[]? buffer,
            out uint actualSize);
        [PreserveSig] int GetExifColorSpace(out uint value);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint classContext,
        in Guid interfaceId,
        out nint instance);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(nint value);

    [DllImport("propsys.dll")]
    internal static extern int StgSerializePropVariant(
        nint value,
        out nint serialized,
        out uint serializedSize);
}
