using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Duplicates.Interop;
using static Duplicates.Interop.WicMetadataInterop;

namespace Duplicates.Services;

internal sealed class WicMetadataBackend : IWicMetadataBackend
{
    private const int TooMuchMetadata = unchecked((int)0x88982F52);
    private const int PropertyNotSupported = unchecked((int)0x88982F41);
    private const int InvalidPropertySize = unchecked((int)0x88982F42);
    private const int UnsupportedOperation = unchecked((int)0x88982F81);
    private const int MaxMetadataDepth = 16;
    private const int MaxMetadataNodes = 4096;
    private const int MaxMetadataLeafBytes = 16 * 1024 * 1024;
    private const int MaxMetadataTotalBytes = 32 * 1024 * 1024;

    public WicImageInspection Inspect(string path)
    {
        string canonicalPath = Path.GetFullPath(path);
        IWICImagingFactory? factory = null;
        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        IWICMetadataQueryReader? reader = null;
        IWICFormatConverter? converter = null;
        var colorContexts = new List<IWICColorContext>();
        try
        {
            factory = CreateFactory();
            ThrowIfFailed(factory.CreateDecoderFromFilename(
                canonicalPath,
                0,
                GenericRead,
                DecodeMetadataCacheOnDemand,
                out decoder));
            WicContainerKind container = GetContainer(decoder);
            ThrowIfFailed(decoder.GetFrameCount(out uint frameCount));
            if (frameCount == 0)
            {
                throw new IOException("The image contains no decodable frames.");
            }

            ThrowIfFailed(decoder.GetFrame(0, out frame));
            ThrowIfFailed(frame.GetSize(out uint width, out uint height));
            ThrowIfFailed(frame.GetResolution(out double dpiX, out double dpiY));
            ThrowIfFailed(frame.GetMetadataQueryReader(out reader));

            string? orientation = ReadMetadata(reader, OrientationQuery(container));
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string query in ExifMetadataPolicy.AllQueries(container))
            {
                string? value = ReadMetadata(reader, query);
                if (value is not null)
                {
                    metadata.Add(query, value);
                }
            }

            IReadOnlyList<string> contexts = ReadColorContexts(factory, frame, colorContexts);
            ThrowIfFailed(frame.GetPixelFormat(out Guid sourcePixelFormat));
            ThrowIfFailed(factory.CreateFormatConverter(out converter));
            ThrowIfFailed(converter.CanConvert(sourcePixelFormat, PixelFormat32Bgra, out bool canConvert));
            if (!canConvert)
            {
                throw new IOException("The image pixels cannot be normalized to 32-bit BGRA for verification.");
            }

            ThrowIfFailed(converter.Initialize(frame, PixelFormat32Bgra, 0, 0, 0, 0));
            uint stride = checked(width * 4);
            byte[] pixels = new byte[checked((int)(stride * height))];
            ThrowIfFailed(converter.CopyPixels(0, stride, (uint)pixels.Length, pixels));
            string checksum = XxHash3.HashToUInt64(pixels).ToString("X16", CultureInfo.InvariantCulture);

            return new WicImageInspection(
                container,
                new WicRenderState(
                    checked((int)frameCount),
                    width,
                    height,
                    dpiX,
                    dpiY,
                    orientation,
                    contexts,
                    checksum),
                metadata);
        }
        finally
        {
            for (int index = colorContexts.Count - 1; index >= 0; index--)
            {
                Release(colorContexts[index]);
            }

            Release(converter);
            Release(reader);
            Release(frame);
            Release(decoder);
            Release(factory);
        }
    }

    public void RemoveMetadata(
        string path,
        WicContainerKind container,
        IReadOnlyList<string> queries)
    {
        string canonicalPath = Path.GetFullPath(path);
        IWICImagingFactory? factory = null;
        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        IWICFastMetadataEncoder? encoder = null;
        IWICMetadataQueryWriter? writer = null;
        try
        {
            factory = CreateFactory();
            ThrowIfFailed(factory.CreateDecoderFromFilename(
                canonicalPath,
                0,
                GenericRead | GenericWrite,
                DecodeMetadataCacheOnDemand,
                out decoder));
            if (GetContainer(decoder) != container)
            {
                throw new IOException("The image container changed before metadata editing.");
            }

            ThrowIfFailed(decoder.GetFrameCount(out uint frameCount));
            if (frameCount != 1)
            {
                throw new WicMetadataLayoutException("Only a single-frame image can be edited safely.");
            }

            ThrowIfFailed(decoder.GetFrame(0, out frame));
            int result = factory.CreateFastMetadataEncoderFromFrameDecode(frame, out encoder);
            ThrowMetadataResult(result, "The image does not expose a safe fast metadata writer.");
            result = encoder.GetMetadataQueryWriter(out writer);
            ThrowMetadataResult(result, "The image metadata query writer is unavailable.");

            string[] presentQueries = queries
                .Where(query => MetadataExists(writer, query))
                .ToArray();
            foreach (string query in presentQueries)
            {
                result = writer.RemoveMetadataByName(query);
                if (result == PropertyNotFound)
                {
                    continue;
                }

                ThrowMetadataResult(result, $"The metadata query '{query}' cannot be removed safely.");
            }

            ThrowMetadataResult(encoder.Commit(), "The image metadata changes could not be committed safely.");
        }
        finally
        {
            Release(writer);
            Release(encoder);
            Release(frame);
            Release(decoder);
            Release(factory);
        }
    }

    private static bool MetadataExists(IWICMetadataQueryWriter writer, string query)
    {
        const int size = 24;
        nint value = Marshal.AllocCoTaskMem(size);
        try
        {
            Marshal.Copy(new byte[size], 0, value, size);
            int result = writer.GetMetadataByName(query, value);
            return MetadataExistsFromResult(result, query);
        }
        finally
        {
            _ = PropVariantClear(value);
            Marshal.FreeCoTaskMem(value);
        }
    }

    internal static bool MetadataExistsFromResult(int result, string query)
    {
        if (result == PropertyNotFound)
        {
            return false;
        }

        ThrowMetadataResult(result, $"The metadata query '{query}' cannot be inspected safely.");
        return true;
    }

    internal static bool IsMetadataLayoutFailure(int hresult) =>
        hresult is TooMuchMetadata or PropertyNotSupported or InvalidPropertySize or UnsupportedOperation;

    private static WicContainerKind GetContainer(IWICBitmapDecoder decoder)
    {
        ThrowIfFailed(decoder.GetContainerFormat(out Guid format));
        if (format == JpegContainerFormat)
        {
            return WicContainerKind.Jpeg;
        }

        if (format == TiffContainerFormat)
        {
            return WicContainerKind.Tiff;
        }

        throw new IOException("The WIC decoder did not report a supported JPEG or TIFF container.");
    }

    private static string OrientationQuery(WicContainerKind container) =>
        container == WicContainerKind.Jpeg
            ? "/app1/ifd/{ushort=274}"
            : "/ifd/{ushort=274}";

    private static List<string> ReadColorContexts(
        IWICImagingFactory factory,
        IWICBitmapFrameDecode frame,
        List<IWICColorContext> ownedContexts)
    {
        ThrowIfFailed(frame.GetColorContexts(0, null, out uint count));
        if (count == 0)
        {
            return [];
        }

        var contexts = new IWICColorContext[checked((int)count)];
        for (int index = 0; index < contexts.Length; index++)
        {
            ThrowIfFailed(factory.CreateColorContext(out contexts[index]));
            ownedContexts.Add(contexts[index]);
        }

        ThrowIfFailed(frame.GetColorContexts(count, contexts, out uint actualCount));
        var values = new List<string>(checked((int)actualCount));
        for (int index = 0; index < actualCount; index++)
        {
            IWICColorContext context = contexts[index];
            ThrowIfFailed(context.GetType(out uint type));
            string exif = "none";
            if (type == 2)
            {
                ThrowIfFailed(context.GetExifColorSpace(out uint exifColorSpace));
                exif = exifColorSpace.ToString(CultureInfo.InvariantCulture);
            }

            string profile = "none";
            if (type == 1)
            {
                ThrowIfFailed(context.GetProfileBytes(0, null, out uint profileSize));
                byte[] bytes = new byte[checked((int)profileSize)];
                ThrowIfFailed(context.GetProfileBytes(profileSize, bytes, out uint actualSize));
                profile = Convert.ToHexString(XxHash3.Hash(bytes.AsSpan(0, checked((int)actualSize))));
            }

            values.Add($"type:{type};exif:{exif};profile:{profile}");
        }

        return values;
    }

    private static string? ReadMetadata(IWICMetadataQueryReader reader, string query)
    {
        const int size = 24;
        nint value = Marshal.AllocCoTaskMem(size);
        try
        {
            Marshal.Copy(new byte[size], 0, value, size);
            int result = reader.GetMetadataByName(query, value);
            if (result == PropertyNotFound)
            {
                return null;
            }

            ThrowIfFailed(result);
            PropVariant variant = Marshal.PtrToStructure<PropVariant>(value);
            return Canonicalize(variant);
        }
        finally
        {
            _ = PropVariantClear(value);
            Marshal.FreeCoTaskMem(value);
        }
    }

    private static string CanonicalizeMetadataTree(nint unknown)
    {
        object? nestedObject = null;
        try
        {
            nestedObject = Marshal.GetObjectForIUnknown(unknown);
            if (nestedObject is not IWICMetadataQueryReader nestedReader)
            {
                throw new IOException("A metadata block did not expose a query reader.");
            }

            var context = new MetadataSnapshotContext();
            byte[] hash = SnapshotMetadataReader(nestedReader, context, depth: 0);
            return $"VT_UNKNOWN:TREE:SHA256:{Convert.ToHexString(hash)}";
        }
        finally
        {
            ReleaseOnce(nestedObject);
        }
    }

    private static byte[] SnapshotMetadataReader(
        IWICMetadataQueryReader reader,
        MetadataSnapshotContext context,
        int depth)
    {
        if (depth > MaxMetadataDepth)
        {
            throw new IOException("The metadata hierarchy exceeds the supported depth.");
        }

        nint identity = Marshal.GetIUnknownForObject(reader);
        try
        {
            if (!context.ActiveReaders.Add(identity))
            {
                throw new IOException("The metadata hierarchy contains a cycle.");
            }

            context.AddNode();
            IEnumString? enumerator = null;
            try
            {
                ThrowIfFailed(reader.GetEnumerator(out enumerator));
                var records = new List<MetadataSnapshotRecord>();
                while (true)
                {
                    int result = enumerator.Next(1, out nint namePointer, out uint fetched);
                    if (fetched == 0)
                    {
                        ThrowIfFailed(result);
                        break;
                    }

                    if (fetched != 1)
                    {
                        throw new IOException("The metadata enumerator returned an invalid item count.");
                    }

                    string name;
                    try
                    {
                        name = Marshal.PtrToStringUni(namePointer) ??
                            throw new IOException("The metadata enumerator returned an invalid query name.");
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(namePointer);
                    }

                    context.AddNode();
                    SnapshotValue value = SnapshotMetadataValue(reader, name, context, depth + 1);
                    records.Add(new MetadataSnapshotRecord(name, value.Kind, value.Hash));
                }

                using var payload = new MemoryStream();
                foreach (MetadataSnapshotRecord record in records
                    .OrderBy(static record => record.Name, StringComparer.Ordinal)
                    .ThenBy(static record => record.Kind)
                    .ThenBy(static record => Convert.ToHexString(record.Hash), StringComparer.Ordinal))
                {
                    byte[] name = Encoding.UTF8.GetBytes(record.Name);
                    WriteLengthPrefixed(payload, name);
                    payload.WriteByte(record.Kind);
                    WriteLengthPrefixed(payload, record.Hash);
                }

                context.AddBytes(payload.Length);
                return SHA256.HashData(payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
            }
            finally
            {
                ReleaseOnce(enumerator);
            }
        }
        finally
        {
            _ = context.ActiveReaders.Remove(identity);
            _ = Marshal.Release(identity);
        }
    }

    private static SnapshotValue SnapshotMetadataValue(
        IWICMetadataQueryReader reader,
        string query,
        MetadataSnapshotContext context,
        int depth)
    {
        const int size = 24;
        nint valuePointer = Marshal.AllocCoTaskMem(size);
        try
        {
            Marshal.Copy(new byte[size], 0, valuePointer, size);
            ThrowIfFailed(reader.GetMetadataByName(query, valuePointer));
            PropVariant value = Marshal.PtrToStructure<PropVariant>(valuePointer);
            if (value.VariantType == 13)
            {
                object? nestedObject = null;
                try
                {
                    nestedObject = Marshal.GetObjectForIUnknown(value.Pointer);
                    if (nestedObject is not IWICMetadataQueryReader nestedReader)
                    {
                        throw new IOException("A nested metadata block did not expose a query reader.");
                    }

                    return new SnapshotValue(
                        Kind: 1,
                        SnapshotMetadataReader(nestedReader, context, depth));
                }
                finally
                {
                    ReleaseOnce(nestedObject);
                }
            }

            return new SnapshotValue(Kind: 0, SerializeMetadataLeaf(valuePointer, context));
        }
        finally
        {
            _ = PropVariantClear(valuePointer);
            Marshal.FreeCoTaskMem(valuePointer);
        }
    }

    private static byte[] SerializeMetadataLeaf(
        nint valuePointer,
        MetadataSnapshotContext context)
    {
        ThrowIfFailed(StgSerializePropVariant(valuePointer, out nint serialized, out uint serializedSize));
        try
        {
            if (serializedSize > MaxMetadataLeafBytes)
            {
                throw new IOException("A metadata value exceeds the supported size.");
            }

            context.AddBytes(serializedSize);
            byte[] bytes = new byte[checked((int)serializedSize)];
            if (serializedSize != 0)
            {
                Marshal.Copy(serialized, bytes, 0, bytes.Length);
            }

            return SHA256.HashData(bytes);
        }
        finally
        {
            Marshal.FreeCoTaskMem(serialized);
        }
    }

    private static void WriteLengthPrefixed(MemoryStream destination, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        destination.Write(length);
        destination.Write(value);
    }

    private static void ReleaseOnce(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            _ = Marshal.ReleaseComObject(instance);
        }
    }

    private static string Canonicalize(PropVariant value)
    {
        const ushort vector = 0x1000;
        const ushort typeMask = 0x0FFF;
        ushort type = (ushort)(value.VariantType & typeMask);
        if ((value.VariantType & vector) != 0 && type == 17)
        {
            byte[] bytes = new byte[checked((int)value.ElementCount)];
            if (value.ElementCount != 0)
            {
                Marshal.Copy(value.Elements, bytes, 0, bytes.Length);
            }

            return $"VT_VECTOR|VT_UI1:{Convert.ToHexString(XxHash3.Hash(bytes))}";
        }

        return value.VariantType switch
        {
            0 => "VT_EMPTY",
            1 => "VT_NULL",
            2 => $"VT_I2:{value.Int16.ToString(CultureInfo.InvariantCulture)}",
            3 => $"VT_I4:{value.Int32.ToString(CultureInfo.InvariantCulture)}",
            4 => $"VT_R4:{value.Float.ToString("R", CultureInfo.InvariantCulture)}",
            5 => $"VT_R8:{value.Double.ToString("R", CultureInfo.InvariantCulture)}",
            8 => $"VT_BSTR:{Marshal.PtrToStringBSTR(value.Pointer)}",
            11 => $"VT_BOOL:{value.Int16 != 0}",
            13 => CanonicalizeMetadataTree(value.Pointer),
            16 => $"VT_I1:{value.Int8.ToString(CultureInfo.InvariantCulture)}",
            17 => $"VT_UI1:{value.UInt8.ToString(CultureInfo.InvariantCulture)}",
            18 => $"VT_UI2:{value.UInt16.ToString(CultureInfo.InvariantCulture)}",
            19 => $"VT_UI4:{value.UInt32.ToString(CultureInfo.InvariantCulture)}",
            20 => $"VT_I8:{value.Int64.ToString(CultureInfo.InvariantCulture)}",
            21 => $"VT_UI8:{value.UInt64.ToString(CultureInfo.InvariantCulture)}",
            22 => $"VT_INT:{value.Int32.ToString(CultureInfo.InvariantCulture)}",
            23 => $"VT_UINT:{value.UInt32.ToString(CultureInfo.InvariantCulture)}",
            30 => $"VT_LPSTR:{Marshal.PtrToStringAnsi(value.Pointer)}",
            31 => $"VT_LPWSTR:{Marshal.PtrToStringUni(value.Pointer)}",
            64 => $"VT_FILETIME:{value.Int64.ToString(CultureInfo.InvariantCulture)}",
            65 => CanonicalBlob(value),
            72 => $"VT_CLSID:{Marshal.PtrToStructure<Guid>(value.Pointer):D}",
            _ => $"VT_{value.VariantType:X4}:present",
        };
    }

    private static string CanonicalBlob(PropVariant value)
    {
        byte[] bytes = new byte[checked((int)value.ElementCount)];
        if (value.ElementCount != 0)
        {
            Marshal.Copy(value.Elements, bytes, 0, bytes.Length);
        }

        return $"VT_BLOB:{Convert.ToHexString(XxHash3.Hash(bytes))}";
    }

    private readonly record struct SnapshotValue(byte Kind, byte[] Hash);

    private readonly record struct MetadataSnapshotRecord(string Name, byte Kind, byte[] Hash);

    private sealed class MetadataSnapshotContext
    {
        private int _nodeCount;
        private long _totalBytes;

        public HashSet<nint> ActiveReaders { get; } = [];

        public void AddNode()
        {
            _nodeCount++;
            if (_nodeCount > MaxMetadataNodes)
            {
                throw new IOException("The metadata hierarchy contains too many nodes.");
            }
        }

        public void AddBytes(long count)
        {
            _totalBytes = checked(_totalBytes + count);
            if (_totalBytes > MaxMetadataTotalBytes)
            {
                throw new IOException("The metadata hierarchy exceeds the supported size.");
            }
        }
    }

    private static void ThrowMetadataResult(int result, string message)
    {
        if (result >= 0)
        {
            return;
        }

        if (IsMetadataLayoutFailure(result))
        {
            throw new WicMetadataLayoutException(message, Marshal.GetExceptionForHR(result)!);
        }

        Marshal.ThrowExceptionForHR(result);
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }
}
